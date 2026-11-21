namespace TidalStudio.Core.Ml;

/// <summary>A piece of music as parts, for training: every part spans the whole song (<see cref="Bars"/>).</summary>
public sealed class MSong
{
    public string Source { get; init; } = "";
    public double Bpm { get; init; } = 120;
    public int Bars { get; init; }
    public List<MPart> Parts { get; init; } = new();
    public int Style { get; init; } = -1;
}

/// <summary>
/// Reads Standard MIDI Files (format 0 and 1) into parts. 1 bar of 4/4 = 1 cycle. Songs not in 4/4 are skipped.
/// Roles: channel 10 → drums; bass programs (32–39) → bass; other tracks → melody when mostly one note at a time,
/// chords otherwise.
/// </summary>
public static class MidiReader
{
    private sealed record RawNote(int Channel, int Program, int Key, long OnTick, long OffTick, int Velocity, int Track);

    public static MSong? Read(string path, int maxBars = 400)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (IOException) { return null; }
        try { return Parse(data, Path.GetFileName(path), maxBars); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException) { return null; }
    }

    public static MSong? Parse(byte[] d, string source, int maxBars = 400)
    {
        int p = 0;
        if (d.Length < 14 || d[0] != 'M' || d[1] != 'T' || d[2] != 'h' || d[3] != 'd') return null;
        int headerLen = BE32(d, 4);
        int format = BE16(d, 8), tracks = BE16(d, 10), division = BE16(d, 12);
        if (format > 1 || (division & 0x8000) != 0 || division == 0) return null;
        p = 8 + headerLen;
        var notes = new List<RawNote>();
        var tempos = new List<(long Tick, double Usq)>();
        bool non44 = false;
        for (int track = 0; track < tracks && p + 8 <= d.Length; track++)
        {
            if (d[p] != 'M' || d[p + 1] != 'T' || d[p + 2] != 'r' || d[p + 3] != 'k') break;
            int len = BE32(d, p + 4);
            int end = Math.Min(d.Length, p + 8 + len);
            int i = p + 8;
            long tick = 0;
            int status = 0;
            var programs = new int[16];
            var open = new Dictionary<(int, int), (long Tick, int Vel)>();
            while (i < end)
            {
                tick += VarLen(d, ref i);
                if (i >= end) break;
                int b = d[i];
                if (b == 0xFF)
                {
                    int type = d[i + 1];
                    i += 2;
                    int mlen = (int)VarLen(d, ref i);
                    if (type == 0x51 && mlen == 3) tempos.Add((tick, (d[i] << 16) | (d[i + 1] << 8) | d[i + 2]));
                    else if (type == 0x58 && mlen >= 2 && tick == 0 && (d[i] != 4 || d[i + 1] != 2)) non44 = true;
                    else if (type == 0x2F) { i += mlen; break; }
                    i += mlen;
                    continue;
                }
                if (b is 0xF0 or 0xF7)
                {
                    i++;
                    int slen = (int)VarLen(d, ref i);
                    i += slen;
                    continue;
                }
                if ((b & 0x80) != 0) { status = b; i++; }
                if (status == 0) break;
                int kind = status & 0xF0, ch = status & 0x0F;
                int a1 = d[i], a2 = kind is 0xC0 or 0xD0 ? 0 : d[i + 1];
                i += kind is 0xC0 or 0xD0 ? 1 : 2;
                switch (kind)
                {
                    case 0xC0: programs[ch] = a1; break;
                    case 0x90 when a2 > 0:
                        if (open.Remove((ch, a1), out var prev)) notes.Add(new RawNote(ch, programs[ch], a1, prev.Tick, tick, prev.Vel, track));
                        open[(ch, a1)] = (tick, a2);
                        break;
                    case 0x80:
                    case 0x90:
                        if (open.Remove((ch, a1), out var on)) notes.Add(new RawNote(ch, programs[ch], a1, on.Tick, tick, on.Vel, track));
                        break;
                }
            }
            p = 8 + p + len;
        }
        if (non44 || notes.Count < 16) return null;

        double usq = tempos.Count > 0 ? tempos.OrderBy(t => t.Tick).First().Usq : 500000;
        double bpm = Math.Round(60_000_000 / usq);
        double ticksPerBar = division * 4.0;
        long lastTick = notes.Max(n => n.OffTick);
        int bars = Math.Min(maxBars, (int)Math.Ceiling(lastTick / ticksPerBar));
        if (bars < 2) return null;

        var parts = new List<MPart>();
        foreach (var group in notes.GroupBy(n => (n.Track, n.Channel)))
        {
            var list = group.Where(n => n.OnTick / ticksPerBar < bars).ToList();
            if (list.Count < 8) continue;
            PartRole role;
            if (group.Key.Channel == 9) role = PartRole.Drums;
            else
            {
                int program = list[0].Program;
                if (program is >= 112) continue; // sound effects, percussive "instruments"
                if (program is >= 32 and <= 39 || list.Average(n => n.Key) < 45) role = PartRole.Bass;
                else role = Polyphony(list) > 1.6 ? PartRole.Chords : PartRole.Melody;
            }
            var part = new MPart { Role = role, Bars = bars };
            foreach (var n in list)
            {
                double start = n.OnTick / ticksPerBar, dur = Math.Max(1.0 / 32, (n.OffTick - n.OnTick) / ticksPerBar);
                int pitch = role == PartRole.Drums ? Drums.FromGeneralMidi(n.Key) : n.Key;
                part.Notes.Add(new MNote(start, dur, pitch, n.Velocity / 127.0));
            }
            part.Notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            parts.Add(part);
        }
        if (parts.Count == 0) return null;
        return new MSong { Source = source, Bpm = bpm is > 30 and < 300 ? bpm : 120, Bars = bars, Parts = parts };
    }

    /// <summary>Average number of notes sounding at each onset.</summary>
    private static double Polyphony(List<RawNote> notes)
    {
        var onsets = notes.GroupBy(n => n.OnTick).Select(g => g.Count()).ToList();
        return onsets.Count == 0 ? 1 : onsets.Average();
    }

    private static int BE32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
    private static int BE16(byte[] d, int i) => (d[i] << 8) | d[i + 1];

    private static long VarLen(byte[] d, ref int i)
    {
        long v = 0;
        for (int k = 0; k < 4 && i < d.Length; k++)
        {
            byte b = d[i++];
            v = (v << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }
}

/// <summary>Cuts songs into training sequences and writes them for the Python trainer.</summary>
public static class Dataset
{
    /// <summary>Training sequences from one song: every part, in windows of 1, 2, 4 and 8 bars.</summary>
    public static IEnumerable<List<int>> Sequences(MSong song, Random random)
    {
        var pitchedAll = song.Parts.Where(p => p.Role != PartRole.Drums).SelectMany(p => p.Notes).ToList();
        var key = Theory.DetectKey(pitchedAll);
        var kicks = song.Parts.Where(p => p.Role == PartRole.Drums).SelectMany(p => p.Notes).Where(n => n.Pitch == 0).ToList();
        foreach (int len in new[] { 4, 2, 8, 1 })
        {
            int stride = len == 8 ? 4 : len == 1 ? 3 : 2;
            for (int start = 0; start + len <= song.Bars; start += stride)
            {
                var window = song.Parts.Select(p => Window(p, start, len)).ToList();
                var pitched = window.Where(w => w.Role != PartRole.Drums).SelectMany(w => w.Notes).ToList();
                var chords = Theory.DetectChords(pitched, len);
                var kickSteps = kicks.Where(n => n.Start >= start && n.Start < start + 1).Select(n => (int)Math.Round((n.Start - start) * 16)).Where(s => s < 16).Distinct().ToList();
                foreach (var part in window)
                {
                    if (part.Notes.Count < Math.Max(2, len)) continue;
                    // Not every window of every length: keep the dataset balanced.
                    if (len == 1 && random.NextDouble() < 0.6) continue;
                    var request = new PartRequest
                    {
                        Role = part.Role, Key = key, Bars = len, Density = Tokenizer.DensityOf(part), Bpm = song.Bpm,
                        Chords = chords, KickSteps = part.Role == PartRole.Drums ? Array.Empty<int>() : kickSteps, Style = song.Style,
                    };
                    var seq = Tokenizer.Encode(part, request);
                    if (seq.Count <= Tokenizer.MaxLength) yield return seq;
                }
            }
        }
    }

    /// <summary>The notes of a part that start in bars [start, start + len), moved to start at 0.</summary>
    public static MPart Window(MPart part, int start, int len) => new()
    {
        Role = part.Role, Bars = len, Sound = part.Sound,
        Notes = part.Notes.Where(n => n.Start >= start - 1e-9 && n.Start < start + len - 1e-9)
            .Select(n => n with { Start = n.Start - start, Dur = Math.Min(n.Dur, start + len - n.Start) }).ToList(),
    };

    /// <summary>A 64-bit fingerprint of a sequence, to skip duplicates without keeping them in memory.</summary>
    public static ulong Hash(IReadOnlyList<int> sequence)
    {
        ulong h = 14695981039346656037UL;
        foreach (int t in sequence) { h ^= (ulong)t; h *= 1099511628211UL; }
        return h ^ (ulong)sequence.Count;
    }

    /// <summary>Appends sequences to <c>tokens.bin</c> (uint16) and their start offsets to <c>index.bin</c> (int64).</summary>
    public sealed class Writer : IDisposable
    {
        private readonly BinaryWriter _tokens, _index;
        private long _offset;
        public long Count { get; private set; }

        public Writer(string folder)
        {
            Directory.CreateDirectory(folder);
            _tokens = new BinaryWriter(File.Create(Path.Combine(folder, "tokens.bin")));
            _index = new BinaryWriter(File.Create(Path.Combine(folder, "index.bin")));
            File.WriteAllText(Path.Combine(folder, "vocab.json"), Tokenizer.VocabularyJson());
        }

        public void Add(IReadOnlyList<int> sequence)
        {
            _index.Write(_offset);
            foreach (int t in sequence) _tokens.Write((ushort)t);
            _offset += sequence.Count;
            Count++;
        }

        public void Dispose()
        {
            _index.Write(_offset); // end sentinel
            _tokens.Dispose();
            _index.Dispose();
        }
    }
}
