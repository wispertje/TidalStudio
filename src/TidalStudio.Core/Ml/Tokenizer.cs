using System.Text.Json;

namespace TidalStudio.Core.Ml;

/// <summary>What a part is asked to be: the conditioning at the start of every token sequence.</summary>
public sealed record PartRequest
{
    public PartRole Role { get; init; } = PartRole.Melody;
    public MKey Key { get; init; } = MKey.CMinor;
    public int Bars { get; init; } = 4;
    /// <summary>0 (sparse) … 4 (busy).</summary>
    public int Density { get; init; } = 2;
    public double Bpm { get; init; } = 120;
    /// <summary>One chord per bar (may be shorter than <see cref="Bars"/>; it repeats).</summary>
    public IReadOnlyList<MChord> Chords { get; init; } = Array.Empty<MChord>();
    /// <summary>Kick drum steps (0–15) of the first bar, when drums are playing.</summary>
    public IReadOnlyList<int> KickSteps { get; init; } = Array.Empty<int>();
    /// <summary>Style tag 0–7, or -1 for none.</summary>
    public int Style { get; init; } = -1;
}

/// <summary>
/// Token vocabulary shared by the importers, the Python trainer and generation. A sequence is
/// <c>BOS ROLE MODE TEMPO LEN DENS [STYLE] CH×bars [KICK…] SEP (BAR (POS (PITCH DUR VEL | DRUM VEL)+)*)×bars EOS</c>.
/// Pitches are transposed so the key's tonic is C.
/// </summary>
public static class Tokenizer
{
    public const int Pad = 0, Bos = 1, Eos = 2, Sep = 3, Bar = 4;
    public const int Role0 = 5, Mode0 = 9, Tempo0 = 11, Len0 = 14, Dens0 = 18;
    public const int ChNone = 23, ChMaj0 = 24, ChMin0 = 36, ChDim0 = 48;
    public const int Pos0 = 60, PosCount = 16;
    public const int Pitch0 = 76, PitchMin = 24, PitchCount = 88;
    public const int Drum0 = 164, DrumCount = 14;
    public const int Dur0 = 178, DurCount = 16;
    public const int Vel0 = 194, VelCount = 4;
    public const int Style0 = 198, StyleCount = 8;
    public const int Kick0 = 206;
    public const int VocabSize = 256;
    public const int MaxLength = 1024;

    public static readonly int[] LengthChoices = { 1, 2, 4, 8 };

    public static bool IsPos(int t) => t >= Pos0 && t < Pos0 + PosCount;
    public static bool IsPitch(int t) => t >= Pitch0 && t < Pitch0 + PitchCount;
    public static bool IsDrum(int t) => t >= Drum0 && t < Drum0 + DrumCount;
    public static bool IsDur(int t) => t >= Dur0 && t < Dur0 + DurCount;
    public static bool IsVel(int t) => t >= Vel0 && t < Vel0 + VelCount;

    /// <summary>The semitones that move <paramref name="key"/>'s tonic to C (as little as possible).</summary>
    public static int Shift(MKey key) => key.Tonic <= 6 ? -key.Tonic : 12 - key.Tonic;

    public static int DensityOf(MPart part)
    {
        double perBar = part.Notes.Select(n => Math.Round(n.Start * 16)).Distinct().Count() / (double)Math.Max(1, part.Bars);
        if (part.Role == PartRole.Drums) perBar /= 2.5;
        return perBar switch { <= 2 => 0, <= 4 => 1, <= 8 => 2, <= 12 => 3, _ => 4 };
    }

    public static int TempoBucket(double bpm) => bpm < 100 ? 0 : bpm <= 125 ? 1 : 2;

    public static int ChordToken(MChord c, int shift)
    {
        int r = ((c.Root + shift) % 12 + 12) % 12;
        return c.Quality switch
        {
            ChordQuality.Major => ChMaj0 + r,
            ChordQuality.Minor => ChMin0 + r,
            ChordQuality.Diminished => ChDim0 + r,
            _ => ChNone,
        };
    }

    public static MChord ChordOf(int token, int shift)
    {
        int Back(int r) => ((r - shift) % 12 + 12) % 12;
        if (token >= ChMaj0 && token < ChMaj0 + 12) return new MChord(Back(token - ChMaj0), ChordQuality.Major);
        if (token >= ChMin0 && token < ChMin0 + 12) return new MChord(Back(token - ChMin0), ChordQuality.Minor);
        if (token >= ChDim0 && token < ChDim0 + 12) return new MChord(Back(token - ChDim0), ChordQuality.Diminished);
        return MChord.None;
    }

    public static int VelBucket(double v) => v < 0.4 ? 0 : v < 0.65 ? 1 : v < 0.85 ? 2 : 3;
    public static double VelOf(int bucket) => bucket switch { 0 => 0.3, 1 => 0.55, 2 => 0.75, _ => 0.95 };

    public static int PitchToken(int pitch)
    {
        while (pitch < PitchMin) pitch += 12;
        while (pitch >= PitchMin + PitchCount) pitch -= 12;
        return Pitch0 + pitch - PitchMin;
    }

    /// <summary>The conditioning tokens up to and including SEP.</summary>
    public static List<int> Header(PartRequest r)
    {
        int shift = Shift(r.Key);
        int bars = LengthChoices.Contains(r.Bars) ? r.Bars : 4;
        var t = new List<int>
        {
            Bos, Role0 + (int)r.Role, Mode0 + (r.Key.Minor ? 1 : 0), Tempo0 + TempoBucket(r.Bpm),
            Len0 + Array.IndexOf(LengthChoices, bars), Dens0 + Math.Clamp(r.Density, 0, 4),
        };
        if (r.Style is >= 0 and < StyleCount) t.Add(Style0 + r.Style);
        for (int b = 0; b < bars; b++)
            t.Add(r.Chords.Count == 0 ? ChNone : ChordToken(r.Chords[b % r.Chords.Count], shift));
        if (r.Role != PartRole.Drums)
            foreach (int k in r.KickSteps.Distinct().Where(k => k is >= 0 and < 16).OrderBy(k => k)) t.Add(Kick0 + k);
        t.Add(Sep);
        return t;
    }

    /// <summary>A whole training sequence: header, the part's notes, EOS.</summary>
    public static List<int> Encode(MPart part, PartRequest request)
    {
        var tokens = Header(request with { Bars = part.Bars, Role = part.Role });
        int shift = Shift(request.Key);
        for (int b = 0; b < part.Bars; b++)
        {
            tokens.Add(Bar);
            var onsets = part.Notes.Where(n => n.Start >= b - 1e-9 && n.Start < b + 1 - 1e-9)
                .GroupBy(n => Math.Clamp((int)Math.Round((n.Start - b) * 16), 0, 15)).OrderBy(g => g.Key);
            foreach (var g in onsets)
            {
                tokens.Add(Pos0 + g.Key);
                if (part.Role == PartRole.Drums)
                {
                    foreach (var n in g.GroupBy(n => n.Pitch).Select(x => x.First()).OrderBy(n => n.Pitch))
                    {
                        tokens.Add(Drum0 + Math.Clamp(n.Pitch, 0, DrumCount - 1));
                        tokens.Add(Vel0 + VelBucket(n.Vel));
                    }
                }
                else
                {
                    // Bass and melody are monophonic: the lowest / highest note of a step.
                    var notes = g.GroupBy(n => n.Pitch).Select(x => x.First()).OrderBy(n => n.Pitch).ToList();
                    if (part.Role == PartRole.Bass) notes = notes.Take(1).ToList();
                    else if (part.Role == PartRole.Melody) notes = notes.TakeLast(1).ToList();
                    foreach (var n in notes)
                    {
                        tokens.Add(PitchToken(n.Pitch + shift));
                        tokens.Add(Dur0 + Math.Clamp((int)Math.Round(n.Dur * 16), 1, DurCount) - 1);
                        tokens.Add(Vel0 + VelBucket(n.Vel));
                    }
                }
            }
        }
        tokens.Add(Eos);
        return tokens;
    }

    /// <summary>Reads the part back from a sequence (header + body). Pitches move back to <paramref name="key"/>.</summary>
    public static MPart Decode(IReadOnlyList<int> tokens, MKey key)
    {
        int shift = Shift(key);
        var role = PartRole.Melody;
        int bars = 1, bar = -1, pos = 0, sep = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            int t = tokens[i];
            if (t >= Role0 && t < Role0 + 4) role = (PartRole)(t - Role0);
            else if (t >= Len0 && t < Len0 + 4) bars = LengthChoices[t - Len0];
            else if (t == Sep) { sep = i; break; }
        }
        var part = new MPart { Role = role, Bars = bars };
        for (int i = sep + 1; i < tokens.Count; i++)
        {
            int t = tokens[i];
            if (t == Eos) break;
            if (t == Bar) { bar++; pos = 0; continue; }
            if (bar < 0 || bar >= bars) continue;
            if (IsPos(t)) pos = t - Pos0;
            else if (IsDrum(t))
            {
                double vel = i + 1 < tokens.Count && IsVel(tokens[i + 1]) ? VelOf(tokens[i + 1] - Vel0) : 0.8;
                part.Notes.Add(new MNote(bar + pos / 16.0, 1 / 16.0, t - Drum0, vel));
            }
            else if (IsPitch(t))
            {
                int dur = i + 1 < tokens.Count && IsDur(tokens[i + 1]) ? tokens[i + 1] - Dur0 + 1 : 1;
                double vel = i + 2 < tokens.Count && IsVel(tokens[i + 2]) ? VelOf(tokens[i + 2] - Vel0) : 0.8;
                part.Notes.Add(new MNote(bar + pos / 16.0, dur / 16.0, t - Pitch0 + PitchMin - shift, vel));
            }
        }
        return part;
    }

    /// <summary>The vocabulary as JSON, for the Python trainer.</summary>
    public static string VocabularyJson()
    {
        var names = new string[VocabSize];
        for (int i = 0; i < VocabSize; i++) names[i] = $"RES_{i}";
        names[Pad] = "PAD"; names[Bos] = "BOS"; names[Eos] = "EOS"; names[Sep] = "SEP"; names[Bar] = "BAR";
        for (int i = 0; i < 4; i++) names[Role0 + i] = "ROLE_" + (PartRole)i;
        names[Mode0] = "MODE_major"; names[Mode0 + 1] = "MODE_minor";
        for (int i = 0; i < 3; i++) names[Tempo0 + i] = "TEMPO_" + i;
        for (int i = 0; i < 4; i++) names[Len0 + i] = "LEN_" + LengthChoices[i];
        for (int i = 0; i < 5; i++) names[Dens0 + i] = "DENS_" + i;
        names[ChNone] = "CH_none";
        for (int i = 0; i < 12; i++) { names[ChMaj0 + i] = $"CH_{i}_maj"; names[ChMin0 + i] = $"CH_{i}_min"; names[ChDim0 + i] = $"CH_{i}_dim"; }
        for (int i = 0; i < PosCount; i++) names[Pos0 + i] = "POS_" + i;
        for (int i = 0; i < PitchCount; i++) names[Pitch0 + i] = "PITCH_" + (PitchMin + i);
        for (int i = 0; i < DrumCount; i++) names[Drum0 + i] = "DRUM_" + Drums.Names[i];
        for (int i = 0; i < DurCount; i++) names[Dur0 + i] = "DUR_" + (i + 1);
        for (int i = 0; i < VelCount; i++) names[Vel0 + i] = "VEL_" + i;
        for (int i = 0; i < StyleCount; i++) names[Style0 + i] = "STYLE_" + i;
        for (int i = 0; i < 16; i++) names[Kick0 + i] = "KICK_" + i;
        return JsonSerializer.Serialize(new { vocab_size = VocabSize, max_length = MaxLength, pad = Pad, bos = Bos, eos = Eos, sep = Sep, tokens = names },
            new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// Which tokens may come next, so generation always gives a valid part of the asked length, in key and in range.
/// </summary>
public sealed class TokenGrammar
{
    private readonly PartRole _role;
    private readonly int _bars;
    private readonly bool[] _pitchAllowed = new bool[Tokenizer.PitchCount];

    /// <param name="inKeyOnly">Only notes of the (transposed) scale: C major or C minor.</param>
    public TokenGrammar(PartRole role, int bars, bool minor, bool inKeyOnly)
    {
        _role = role;
        _bars = bars;
        var (lo, hi) = Range(role);
        var scale = new MKey(0, minor).Scale;
        for (int i = 0; i < Tokenizer.PitchCount; i++)
        {
            int p = Tokenizer.PitchMin + i;
            _pitchAllowed[i] = p >= lo && p <= hi && (!inKeyOnly || scale.Contains(((p % 12) + 12) % 12));
        }
    }

    /// <summary>Usable pitch range per role (Tidal MIDI, after transposing to C).</summary>
    public static (int Lo, int Hi) Range(PartRole role) => role switch
    {
        PartRole.Bass => (24, 55),
        PartRole.Chords => (48, 84),
        _ => (55, 91),
    };

    /// <summary>Allowed next tokens after the body so far (tokens after SEP).</summary>
    public bool[] Allowed(IReadOnlyList<int> body)
    {
        var ok = new bool[Tokenizer.VocabSize];
        int bars = 0, lastPos = -1, last = Tokenizer.Sep, lastPitch = -1;
        foreach (int t in body)
        {
            if (t == Tokenizer.Bar) { bars++; lastPos = -1; lastPitch = -1; }
            else if (Tokenizer.IsPos(t)) { lastPos = t - Tokenizer.Pos0; lastPitch = -1; }
            else if (Tokenizer.IsPitch(t) || Tokenizer.IsDrum(t)) lastPitch = t;
            last = t;
        }
        bool drums = _role == PartRole.Drums;
        void AllowPositionsAfter(int p)
        {
            for (int i = p + 1; i < Tokenizer.PosCount; i++) ok[Tokenizer.Pos0 + i] = true;
        }
        void AllowBarOrEnd()
        {
            if (bars < _bars) ok[Tokenizer.Bar] = true;
            else ok[Tokenizer.Eos] = true;
        }
        void AllowNotesAbove(int previous)
        {
            if (drums)
                for (int i = 0; i < Tokenizer.DrumCount; i++) ok[Tokenizer.Drum0 + i] = Tokenizer.Drum0 + i > previous;
            else
                for (int i = 0; i < Tokenizer.PitchCount; i++) ok[Tokenizer.Pitch0 + i] = _pitchAllowed[i] && Tokenizer.Pitch0 + i > previous;
        }

        if (last == Tokenizer.Sep) ok[Tokenizer.Bar] = true;
        else if (last == Tokenizer.Bar)
        {
            AllowPositionsAfter(-1);
            AllowBarOrEnd();
        }
        else if (Tokenizer.IsPos(last)) AllowNotesAbove(-1);
        else if (Tokenizer.IsPitch(last)) for (int i = 0; i < Tokenizer.DurCount; i++) ok[Tokenizer.Dur0 + i] = true;
        else if (Tokenizer.IsDur(last) || Tokenizer.IsDrum(last)) for (int i = 0; i < Tokenizer.VelCount; i++) ok[Tokenizer.Vel0 + i] = true;
        else if (Tokenizer.IsVel(last))
        {
            // Another note at this step (chords, drums), a later step, or the next bar.
            if (drums || _role == PartRole.Chords) AllowNotesAbove(lastPitch);
            AllowPositionsAfter(lastPos);
            AllowBarOrEnd();
        }
        else ok[Tokenizer.Eos] = true;
        return ok;
    }
}
