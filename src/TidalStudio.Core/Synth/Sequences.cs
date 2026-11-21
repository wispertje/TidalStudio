using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidalStudio.Core.Synth;

/// <summary>How a sequenced note ends, as in Tidal: cut at its length (legato 1), the whole sample, or until the next note (cut 1).</summary>
public enum NoteEnd { Legato, Full, Cut }

/// <summary>One note in a piano roll: starts at a step (sixteenth), lasts some steps, at a MIDI pitch.</summary>
public sealed class SeqNote
{
    public int Step { get; set; }
    public int Length { get; set; } = 1;
    public int Midi { get; set; } = 60;

    public SeqNote Clone() => (SeqNote)MemberwiseClone();
}

/// <summary>Notes played together (same start and length) with the sample they use.</summary>
public sealed record SeqUnit(int Step, int Length, int Sample, int[] Midis);

/// <summary>Settings both sequencers share: tempo, length in sixteenth steps (16 = one Tidal cycle), swing.</summary>
public abstract class SequenceBase
{
    public const int StepsPerCycle = 16;
    public const int MaxSteps = 64;

    public double Bpm { get; set; } = 120;
    /// <summary>Steps that play, 1..64 (16 = one bar = one Tidal cycle).</summary>
    public int Length { get; set; } = 16;
    /// <summary>Delays every second sixteenth, 0..0.5 of a step (Tidal: swingBy).</summary>
    public double Swing { get; set; }
    /// <summary>The Tidal channel the code plays on: d1, d2, …</summary>
    public int Orbit { get; set; } = 1;

    /// <summary>Seconds per sixteenth note.</summary>
    [JsonIgnore]
    public double StepSeconds => 60.0 / Math.Clamp(Bpm, 40, 300) / 4;

    /// <summary>Length of the loop in samples.</summary>
    public int LoopSamples(int sampleRate) => Math.Max(1, (int)Math.Round(Math.Clamp(Length, 1, MaxSteps) * StepSeconds * sampleRate));

    /// <summary>Where a step starts in the loop, in samples (swing delays the odd steps).</summary>
    public int StepStart(int step, int sampleRate)
    {
        double t = step * StepSeconds + (step % 2 == 1 ? Math.Clamp(Swing, 0, 0.5) * StepSeconds : 0);
        return (int)Math.Round(t * sampleRate);
    }

    /// <summary>The step playing <paramref name="seconds"/> into the loop.</summary>
    public int StepAt(double seconds)
    {
        int sr = 10000;
        int at = (int)(seconds * sr);
        for (int i = Math.Clamp(Length, 1, MaxSteps) - 1; i >= 0; i--)
            if (StepStart(i, sr) <= at) return i;
        return 0;
    }

    protected void RepairBase()
    {
        Bpm = Math.Clamp(Bpm, 40, 300);
        Length = Math.Clamp(Length, 1, MaxSteps);
        Swing = Math.Clamp(Swing, 0, 0.5);
        Orbit = Math.Clamp(Orbit, 1, 12);
    }

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>A piano-roll sequence for a melodic sound (Tidepool, Tidepool FM).</summary>
public sealed class MelodySequence : SequenceBase
{
    public NoteEnd End { get; set; } = NoteEnd.Legato;
    /// <summary>MIDI note of the key (only for highlighting and Random / Mutate).</summary>
    public int Root { get; set; } = 60;
    public AcidScale Scale { get; set; } = AcidScale.Minor;
    public List<SeqNote> Notes { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MelodySequence FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<MelodySequence>(json, JsonOptions) ?? new MelodySequence();
        s.Repair();
        return s;
    }

    public MelodySequence Clone() => FromJson(ToJson());

    public void Repair()
    {
        RepairBase();
        Root = Math.Clamp(Root, 0, 127);
        Notes = (Notes ?? new()).Where(n => n is not null).ToList();
        foreach (var n in Notes)
        {
            n.Midi = Math.Clamp(n.Midi, 0, 127);
            n.Step = Math.Clamp(n.Step, 0, MaxSteps - 1);
            n.Length = Math.Clamp(n.Length, 1, MaxSteps - n.Step);
        }
    }

    /// <summary>The notes that start within the length, clipped to it.</summary>
    [JsonIgnore]
    public IEnumerable<SeqNote> Playing => Notes.Where(n => n.Step < Length)
        .Select(n => new SeqNote { Step = n.Step, Midi = n.Midi, Length = Math.Min(n.Length, Length - n.Step) })
        .OrderBy(n => n.Step).ThenBy(n => n.Midi);

    /// <summary>A demo line to start from.</summary>
    public static MelodySequence Default()
    {
        var s = new MelodySequence { Bpm = 118, Root = 57, Scale = AcidScale.Minor };
        int[] steps = { 0, 3, 6, 8, 10, 12, 14 };
        int[] notes = { 57, 60, 64, 62, 60, 64, 67 };
        int[] lengths = { 3, 3, 2, 2, 2, 2, 2 };
        for (int i = 0; i < steps.Length; i++) s.Notes.Add(new SeqNote { Step = steps[i], Midi = notes[i], Length = lengths[i] });
        return s;
    }
}

/// <summary>A drum-machine sequence: per drum (by its short name, like "bd") a velocity per step.</summary>
public sealed class BeatSequence : SequenceBase
{
    /// <summary>0 = off, 1 = soft (ghost), 2 = normal, 3 = accent.</summary>
    public const int Off = 0, Soft = 1, Normal = 2, Accent = 3;

    /// <summary>Gain sent to SuperDirt for each velocity.</summary>
    public static double Gain(int velocity) => velocity switch { Soft => 0.85, Accent => 1.1, _ => 1 };

    public Dictionary<string, int[]> Rows { get; set; } = new();
    /// <summary>Drums left out of the preview and the code.</summary>
    public HashSet<string> Muted { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static BeatSequence FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<BeatSequence>(json, JsonOptions) ?? new BeatSequence();
        s.Repair();
        return s;
    }

    public BeatSequence Clone() => FromJson(ToJson());

    public void Repair()
    {
        RepairBase();
        Rows ??= new();
        Muted ??= new();
        foreach (var key in Rows.Keys.ToList())
        {
            var row = Rows[key] ?? Array.Empty<int>();
            if (row.Length != MaxSteps) Array.Resize(ref row, MaxSteps);
            for (int i = 0; i < row.Length; i++) row[i] = Math.Clamp(row[i], Off, Accent);
            Rows[key] = row;
        }
    }

    /// <summary>The row of a drum (created empty when missing).</summary>
    public int[] Row(string drum)
    {
        if (!Rows.TryGetValue(drum, out var row)) Rows[drum] = row = new int[MaxSteps];
        return row;
    }

    public bool HasHits(string drum) => Rows.TryGetValue(drum, out var row) && row.Take(Length).Any(v => v > Off);

    /// <summary>A plain four-on-the-floor beat for whichever of bd, sd/cp, hh and oh exist.</summary>
    public static BeatSequence Default(IEnumerable<string> drums)
    {
        var s = new BeatSequence { Bpm = 120 };
        var set = drums.ToHashSet();
        for (int i = 0; i < 16; i++)
        {
            if (set.Contains("bd") && i % 4 == 0) s.Row("bd")[i] = Normal;
            if (i is 4 or 12)
            {
                if (set.Contains("sd")) s.Row("sd")[i] = Normal;
                else if (set.Contains("cp")) s.Row("cp")[i] = Normal;
            }
            if (set.Contains("hh") && i % 2 == 0) s.Row("hh")[i] = i % 4 == 2 ? Accent : Soft;
            if (set.Contains("oh") && i % 4 == 2) { s.Row("oh")[i] = Normal; if (set.Contains("hh")) s.Row("hh")[i] = Off; }
        }
        return s;
    }
}

/// <summary>Random and mutated melodies and beats, to experiment from.</summary>
public static class SequenceIdeas
{
    /// <summary>Pitch of a scale degree above the root (degrees past the scale continue into the next octaves).</summary>
    public static int DegreeToSemitones(int[] intervals, int degree)
    {
        int octave = (int)Math.Floor(degree / (double)intervals.Length);
        return octave * 12 + intervals[degree - octave * intervals.Length];
    }

    /// <summary>The scale degree closest to a pitch.</summary>
    public static int NearestDegree(int[] intervals, int semitones)
    {
        int best = 0, bestDistance = int.MaxValue;
        for (int d = -3 * intervals.Length; d <= 4 * intervals.Length; d++)
        {
            int distance = Math.Abs(DegreeToSemitones(intervals, d) - semitones);
            if (distance < bestDistance) { best = d; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>A new monophonic line in the key and scale: mostly steps, some leaps, some rests, varied lengths.</summary>
    public static void RandomMelody(MelodySequence s, Random r)
    {
        var iv = AcidLines.Intervals(s.Scale);
        s.Notes.Clear();
        int bar = Math.Min(16, s.Length);
        int degree = 0, step = 0;
        var phrase = new List<SeqNote>();
        while (step < bar)
        {
            if (step > 0 && r.NextDouble() < 0.22) { step += 1 + r.Next(2); continue; }
            int length = Math.Min(new[] { 1, 1, 2, 2, 2, 3, 4 }[r.Next(7)], bar - step);
            if (step > 0) degree = Math.Clamp(degree + (r.NextDouble() < 0.75 ? r.Next(-2, 3) : r.Next(-4, 5)), -2, iv.Length + 2);
            phrase.Add(new SeqNote { Step = step, Length = length, Midi = s.Root + DegreeToSemitones(iv, degree) });
            step += length;
        }
        // Longer sequences repeat the phrase, changing a few notes each time (call and answer).
        for (int start = 0; start < s.Length; start += bar)
            foreach (var n in phrase)
            {
                if (start + n.Step >= s.Length) continue;
                var copy = n.Clone();
                copy.Step += start;
                if (start > 0 && r.NextDouble() < 0.3)
                    copy.Midi = s.Root + DegreeToSemitones(iv, NearestDegree(iv, n.Midi - s.Root) + r.Next(-2, 3));
                s.Notes.Add(copy);
            }
    }

    /// <summary>Changes a few notes: a step up or down the scale, a different length, one left out or one added.</summary>
    public static void Mutate(MelodySequence s, Random r)
    {
        var iv = AcidLines.Intervals(s.Scale);
        if (s.Notes.Count == 0) { RandomMelody(s, r); return; }
        int changes = 1 + r.Next(Math.Max(1, s.Notes.Count / 4) + 1);
        for (int i = 0; i < changes; i++)
        {
            var n = s.Notes[r.Next(s.Notes.Count)];
            switch (r.Next(6))
            {
                case 0 or 1 or 2:
                    n.Midi = Math.Clamp(s.Root + DegreeToSemitones(iv, NearestDegree(iv, n.Midi - s.Root) + (r.Next(2) == 0 ? -1 : 1)), 0, 127);
                    break;
                case 3:
                    n.Length = Math.Clamp(n.Length + (r.Next(2) == 0 ? -1 : 1), 1, SequenceBase.MaxSteps - n.Step);
                    break;
                case 4 when s.Notes.Count > 2:
                    s.Notes.Remove(n);
                    break;
                default:
                    int step = r.Next(s.Length);
                    if (!s.Notes.Any(m => step >= m.Step && step < m.Step + m.Length))
                        s.Notes.Add(new SeqNote { Step = step, Length = 1, Midi = n.Midi });
                    break;
            }
        }
    }

    /// <summary>A random beat that still sounds like one: kick on the one, snare or clap on 2 and 4, hats moving.</summary>
    public static void RandomBeat(BeatSequence s, IReadOnlyList<(string Drum, DrumModel Model)> drums, Random r)
    {
        foreach (var (drum, _) in drums) Array.Clear(s.Row(drum));
        int hatStyle = r.Next(3);
        bool snareTaken = false;
        foreach (var (drum, model) in drums)
        {
            var row = new int[16];
            switch (model)
            {
                case DrumModel.Kick:
                    row[0] = BeatSequence.Normal;
                    for (int i = 2; i < 16; i += 2)
                        if (r.NextDouble() < (i % 4 == 0 ? 0.55 : 0.2)) row[i] = BeatSequence.Normal;
                    if (r.NextDouble() < 0.3) row[r.Next(8, 16) | 1] = BeatSequence.Soft;
                    break;
                case DrumModel.Snare or DrumModel.Clap when !snareTaken:
                    snareTaken = true;
                    row[4] = row[12] = BeatSequence.Normal;
                    for (int i = 1; i < 16; i += 2)
                        if (r.NextDouble() < 0.12) row[i] = BeatSequence.Soft;
                    break;
                case DrumModel.ClosedHat:
                    for (int i = 0; i < 16; i++)
                        row[i] = hatStyle switch
                        {
                            0 => i % 2 == 0 ? BeatSequence.Normal : 0,
                            1 => i % 4 == 2 ? BeatSequence.Accent : BeatSequence.Soft,
                            _ => i % 4 == 2 ? BeatSequence.Normal : r.NextDouble() < 0.3 ? BeatSequence.Soft : 0,
                        };
                    break;
                case DrumModel.OpenHat:
                    if (r.NextDouble() < 0.6)
                        foreach (int i in new[] { 2, 6, 10, 14 }) if (r.NextDouble() < 0.6) row[i] = BeatSequence.Normal;
                    break;
                case DrumModel.Cymbal:
                    if (r.NextDouble() < 0.3) row[0] = BeatSequence.Normal;
                    break;
                default:
                    // Only some of the other percussion joins in, so the beat stays a beat.
                    if (r.NextDouble() < 0.3)
                        for (int i = 0; i < 16; i++)
                            if (r.NextDouble() < 0.12) row[i] = r.NextDouble() < 0.5 ? BeatSequence.Soft : BeatSequence.Normal;
                    break;
            }
            var target = s.Row(drum);
            for (int i = 0; i < s.Length; i++) target[i] = row[i % 16];
            // A small fill in the last bar of longer beats.
            if (s.Length >= 32 && model is DrumModel.Snare or DrumModel.Clap or DrumModel.Tom && r.NextDouble() < 0.7)
                for (int i = s.Length - 3; i < s.Length; i++) if (r.NextDouble() < 0.6) target[i] = BeatSequence.Soft + r.Next(2);
        }
        // Open and closed hat don't play at the same time.
        foreach (var (open, _) in drums.Where(d => d.Model == DrumModel.OpenHat))
            foreach (var (closed, _) in drums.Where(d => d.Model == DrumModel.ClosedHat))
                for (int i = 0; i < SequenceBase.MaxSteps; i++) if (s.Row(open)[i] > 0) s.Row(closed)[i] = 0;
    }
}

/// <summary>Turns sequences into Tidal code that plays the saved samples like the sequencer's preview.</summary>
public static class SequenceCode
{
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The rendered sample closest in pitch to <paramref name="midi"/> (index into <paramref name="sampleMidis"/>).</summary>
    public static int SampleFor(int midi, IReadOnlyList<int> sampleMidis)
    {
        if (sampleMidis.Count == 0) return 0;
        int best = 0;
        for (int i = 1; i < sampleMidis.Count; i++)
            if (Math.Abs(sampleMidis[i] - midi) < Math.Abs(sampleMidis[best] - midi)) best = i;
        return best;
    }

    /// <summary>
    /// Tidal's note value as a name: 0 = "c", 4 = "e", 13 = "cs6", -3 = "a4". Octave 5 is Tidal's default, so it is left out.
    /// </summary>
    public static string NoteToken(int semitones)
    {
        int midi = Math.Clamp(60 + semitones, 0, 127);
        string name = SynthPatch.NoteName(midi);
        return name.Length >= 2 && name[^1] == '5' && char.IsLetter(name[^2]) ? name[..^1] : name;
    }

    /// <summary>setcps for a tempo, assuming 4 beats per cycle.</summary>
    public static string Cps(double bpm) => $"setcps ({Num(bpm)}/60/4)";

    /// <summary>"slow 2 $ " / "fast 2 $ " / "" so a sequence of <paramref name="length"/> sixteenths keeps 16 per cycle.</summary>
    public static string Speed(int length)
    {
        const int c = SequenceBase.StepsPerCycle;
        if (length == c) return "";
        if (length % c == 0) return $"slow {length / c} $ ";
        if (c % length == 0) return $"fast {c / length} $ ";
        return $"slow ({length}/{c}) $ ";
    }

    /// <summary>
    /// swingBy x 8 cuts each cycle into eighths and delays the second half of each (the odd sixteenths) by x of an eighth,
    /// so a delay of <paramref name="swing"/> sixteenths is x = swing / 2.
    /// </summary>
    private static string SwingPrefix(double swing) =>
        swing > 0.001 ? $"swingBy {Num(Math.Round(swing / 2, 4))} 8 $ " : "";

    /// <summary>
    /// The notes grouped as they are played: notes that start together and last as long form one unit, which uses one
    /// sample — the one closest to its lowest note — so it stays one token in the code ("[c,e,g]"). The preview and the
    /// code both use this, so they play the same thing.
    /// </summary>
    public static List<SeqUnit> Units(MelodySequence seq, IReadOnlyList<int> sampleMidis)
    {
        var mids = sampleMidis.Count > 0 ? sampleMidis : new[] { 60 };
        return seq.Playing
            .GroupBy(n => (n.Step, n.Length))
            .Select(g =>
            {
                var midis = g.Select(n => n.Midi).Distinct().OrderBy(m => m).ToArray();
                return new SeqUnit(g.Key.Step, g.Key.Length, SampleFor(midis[0], mids), midis);
            })
            .OrderBy(u => u.Step).ThenBy(u => u.Midis[0])
            .ToList();
    }

    /// <summary>A unit as a mini-notation token: "e", or "[c,e,g]" for notes played together.</summary>
    public static string UnitToken(SeqUnit unit, int sampleMidi)
    {
        var offsets = unit.Midis.Select(m => m - sampleMidi).ToArray();
        if (offsets.Length == 1) return NoteToken(offsets[0]);
        return $"[{string.Join(',', offsets.Select(NoteToken))}]";
    }

    /// <summary>Something to write at a step: a token lasting some steps.</summary>
    private readonly record struct Cell(int Step, int Length, string Token);

    /// <summary>Splits cells into lanes that don't overlap, so each lane is one mini-notation sequence.</summary>
    private static List<List<Cell>> Lanes(IEnumerable<Cell> cells)
    {
        var lanes = new List<List<Cell>>();
        foreach (var c in cells.OrderBy(c => c.Step))
        {
            var lane = lanes.FirstOrDefault(l => l[^1].Step + l[^1].Length <= c.Step);
            if (lane is null) lanes.Add(lane = new List<Cell>());
            lane.Add(c);
        }
        return lanes;
    }

    /// <summary>
    /// Cells from <paramref name="from"/> for <paramref name="span"/> steps as a plain sequence: @ for longer notes, ~ for
    /// rests (long runs of rests as ~@n when <paramref name="mergeRests"/>). A single cell filling the span is just its token.
    /// </summary>
    private static string Flat(IReadOnlyList<Cell> cells, int from, int span, bool mergeRests)
    {
        if (cells.Count == 0) return "~";
        if (cells.Count == 1 && cells[0].Step == from && cells[0].Length >= span) return cells[0].Token;
        var parts = new List<string>();
        void Rest(int k)
        {
            if (k <= 0) return;
            if (mergeRests && k >= 3) parts.Add($"~@{k}");
            else for (int i = 0; i < k; i++) parts.Add("~");
        }
        int at = from;
        foreach (var c in cells)
        {
            Rest(c.Step - at);
            int len = Math.Min(c.Length, from + span - c.Step);
            parts.Add(len > 1 ? $"{c.Token}@{len}" : c.Token);
            at = c.Step + len;
        }
        Rest(from + span - at);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// A stretch of the pattern grouped by beats ("c ~ e ~ . g@2 a b . ~ . c") or half bars when no note crosses them —
    /// Tidal's "." splits a sequence into equal parts — else as one plain sequence.
    /// </summary>
    private static string Grouped(IReadOnlyList<Cell> cells, int from, int span)
    {
        foreach (int g in new[] { 4, 8 })
        {
            if (span <= g || span % g != 0) continue;
            if (cells.Any(c => (c.Step - from) / g != (c.Step - from + c.Length - 1) / g)) continue;
            return string.Join(" . ", Enumerable.Range(0, span / g)
                .Select(k => Flat(cells.Where(c => (c.Step - from) / g == k).ToList(), from + k * g, g, mergeRests: g > 4)));
        }
        return Flat(cells, from, span, mergeRests: true);
    }

    /// <summary>
    /// True when the pattern can be written bar by bar ("&lt;[bar 1] [bar 2]&gt;", a bar per cycle) instead of slowing down
    /// one long sequence: it is two or more whole bars and no note continues into the next bar.
    /// </summary>
    private static bool BarByBar(int length, IEnumerable<Cell> cells) =>
        length > StepsPerCycle && length % StepsPerCycle == 0 &&
        cells.All(c => c.Step / StepsPerCycle == (c.Step + c.Length - 1) / StepsPerCycle);

    private const int StepsPerCycle = SequenceBase.StepsPerCycle;

    /// <summary>One lane: bar by bar ("&lt;…&gt;", or one bar when every bar is the same) or as one stretch of the pattern.</summary>
    private static string LaneText(IReadOnlyList<Cell> lane, int length, bool barByBar, Func<IReadOnlyList<Cell>, int, int, string> write)
    {
        if (!barByBar) return write(lane, 0, length);
        var bars = Enumerable.Range(0, length / StepsPerCycle)
            .Select(b => write(lane.Where(c => c.Step / StepsPerCycle == b).ToList(), b * StepsPerCycle, StepsPerCycle))
            .ToList();
        if (bars.All(b => b == bars[0])) return bars[0];
        return "<" + string.Join(' ', bars.Select(b => HasTopLevelSpace(b) ? $"[{b}]" : b)) + ">";
    }

    /// <summary>True when a sequence has more than one element ("c e", not "[c e]*2"), so it needs brackets to be one step.</summary>
    private static bool HasTopLevelSpace(string text)
    {
        int depth = 0;
        foreach (char ch in text)
        {
            if (ch is '[' or '<') depth++;
            else if (ch is ']' or '>') depth--;
            else if (ch == ' ' && depth == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Code for a melody played with the saved sound <paramref name="sound"/>, whose samples were rendered at
    /// <paramref name="sampleMidis"/> (n 0, n 1, …). Each note uses the closest sample (n) and shifts it with note.
    /// </summary>
    public static string Melody(MelodySequence seq, string sound, IReadOnlyList<int> sampleMidis)
    {
        int orbit = seq.Orbit;
        var mids = sampleMidis.Count > 0 ? sampleMidis : new[] { 60 };
        var units = Units(seq, mids);
        string header = $"{sound}: {seq.Length} steps, {Num(seq.Bpm)} bpm";
        if (units.Count == 0) return Block(header, seq.Bpm, $"d{orbit} $ silence");
        string end = seq.End switch { NoteEnd.Legato => " # legato 1", NoteEnd.Cut => " # cut 1", _ => "" };

        // One layer per sample (n can't change within a stacked step), each split into lanes that don't overlap.
        var layers = units.GroupBy(u => u.Sample).OrderBy(g => g.Key)
            .Select(g => (Sample: g.Key, Lanes: Lanes(g.Select(u => new Cell(u.Step, u.Length, UnitToken(u, mids[g.Key]))))))
            .ToList();
        bool barByBar = BarByBar(seq.Length, layers.SelectMany(l => l.Lanes.SelectMany(x => x)));
        string prefix = $"d{orbit} $ {SwingPrefix(seq.Swing)}{(barByBar ? "" : Speed(seq.Length))}";
        string Pattern(List<List<Cell>> lanes) => string.Join(", ", lanes.Select(l => LaneText(l, seq.Length, barByBar, Grouped)));

        var sb = new StringBuilder();
        if (layers.Count == 1)
        {
            var layer = layers[0];
            string n = mids.Count > 1 || layer.Sample != 0 ? $" # n {layer.Sample}" : "";
            sb.Append(prefix).Append($"note \"{Pattern(layer.Lanes)}\" # s \"{sound}\"{n}{end}");
        }
        else
        {
            sb.Append(prefix).Append("stack\n");
            for (int i = 0; i < layers.Count; i++)
                sb.Append(i == 0 ? "  [ " : "  , ").Append($"note \"{Pattern(layers[i].Lanes)}\" # n {layers[i].Sample}\n");
            sb.Append($"  ] # s \"{sound}\"{end}");
        }
        return Block(header, seq.Bpm, sb.ToString());
    }

    /// <summary>Writes a row of tokens compactly: "a ~ a ~ a ~ a ~" becomes "[a ~]*4", "a a a a" becomes "a*4".</summary>
    public static string Compress(IReadOnlyList<string> tokens)
    {
        int n = tokens.Count;
        for (int p = 1; p < n; p++)
        {
            if (n % p != 0) continue;
            bool repeats = true;
            for (int i = p; i < n && repeats; i++) repeats = tokens[i] == tokens[i - p];
            if (!repeats) continue;
            int k = n / p;
            return p == 1 ? $"{tokens[0]}*{k}" : $"[{string.Join(' ', tokens.Take(p))}]*{k}";
        }
        return string.Join(' ', tokens);
    }

    /// <summary>A drum row (one-step hits): repeating rows as "[bd ~ ~ ~]*4", others grouped by beat.</summary>
    private static string DrumRow(IReadOnlyList<Cell> cells, int from, int span)
    {
        if (cells.Count == 0) return "~";
        var tokens = Enumerable.Range(from, span).Select(i => cells.FirstOrDefault(c => c.Step == i).Token ?? "~").ToList();
        string compressed = Compress(tokens);
        return compressed.Contains('*') ? compressed : Grouped(cells, from, span);
    }

    /// <summary>Code for a beat: one line per drum in a stack, with gain where hits are soft or accented.</summary>
    public static string Beat(BeatSequence seq, IReadOnlyList<(string Drum, string Sound)> drums)
    {
        int orbit = seq.Orbit;
        bool barByBar = seq.Length > StepsPerCycle && seq.Length % StepsPerCycle == 0;
        var lines = new List<string>();
        foreach (var (drum, sound) in drums)
        {
            if (seq.Muted.Contains(drum) || !seq.HasHits(drum)) continue;
            var row = seq.Row(drum).Take(seq.Length).ToList();
            var hits = Enumerable.Range(0, row.Count).Where(i => row[i] > BeatSequence.Off).ToList();
            string line = $"s \"{LaneText(hits.Select(i => new Cell(i, 1, sound)).ToList(), seq.Length, barByBar, DrumRow)}\"";
            if (hits.Any(i => row[i] is BeatSequence.Soft or BeatSequence.Accent))
            {
                // gain takes its values at the hits (the structure comes from s), so rests can stay rests.
                var gains = hits.Select(i => new Cell(i, 1, Num(BeatSequence.Gain(row[i])))).ToList();
                line += $" # gain \"{LaneText(gains, seq.Length, barByBar, DrumRow)}\"";
            }
            lines.Add(line);
        }
        string prefix = $"d{orbit} $ {SwingPrefix(seq.Swing)}{(barByBar ? "" : Speed(seq.Length))}";
        var sb = new StringBuilder();
        if (lines.Count == 0) sb.Append($"d{orbit} $ silence");
        else if (lines.Count == 1) sb.Append(prefix).Append(lines[0]);
        else
        {
            sb.Append(prefix).Append("stack\n");
            for (int i = 0; i < lines.Count; i++) sb.Append(i == 0 ? "  [ " : "  , ").Append(lines[i]).Append('\n');
            sb.Append("  ]");
        }
        return Block($"beat: {seq.Length} steps, {Num(seq.Bpm)} bpm", seq.Bpm, sb.ToString());
    }

    /// <summary>
    /// One block that Tidal runs as a whole (Ctrl+Enter): a comment, then a do with the tempo and the pattern. Lines directly
    /// below each other are one expression for Tidal, so setcps and d1 need the do.
    /// </summary>
    private static string Block(string header, double bpm, string play) =>
        $"-- {header}\ndo\n  {Cps(bpm)}\n" + string.Join("\n", play.Split('\n').Select(l => "  " + l));
}

/// <summary>
/// Renders sequences to a seamless loop the way SuperDirt plays them: the saved samples, pitched by resampling (note),
/// cut at the note's length (legato) or at the next note (cut), gain as SuperDirt applies it (gain⁴). Tails wrap
/// around, so the loop sounds like the pattern playing on and on.
/// </summary>
public static class SequenceRenderer
{
    /// <param name="samples">The rendered samples, in the order of n (each knows the MIDI note it was rendered at).</param>
    public static RenderedNote Melody(MelodySequence seq, IReadOnlyList<RenderedNote> samples, int sampleRate)
    {
        int sr = sampleRate;
        int loop = seq.LoopSamples(sr);
        var left = new float[loop];
        var right = new float[loop];
        if (samples.Count == 0) return new RenderedNote(60, left, right, sr);
        var mids = samples.Select(s => s.Midi).ToList();
        var notes = SequenceCode.Units(seq, mids)
            .SelectMany(u => u.Midis.Select(m => (Unit: u, Midi: m)))
            .ToList();
        if (notes.Count == 0) return new RenderedNote(60, left, right, sr);
        var starts = notes.Select(n => seq.StepStart(n.Unit.Step, sr)).Distinct().OrderBy(s => s).ToList();
        int fade = (int)(0.008 * sr);

        foreach (var (unit, midi) in notes)
        {
            var n = new SeqNote { Step = unit.Step, Length = unit.Length, Midi = midi };
            var sample = samples[unit.Sample];
            double rate = Math.Pow(2, (n.Midi - sample.Midi) / 12.0) * sample.SampleRate / sr;
            int start = seq.StepStart(n.Step, sr);
            int full = (int)((sample.Left.Length - 1) / rate);
            int length = seq.End switch
            {
                NoteEnd.Legato => (int)Math.Round(n.Length * seq.StepSeconds * sr),
                NoteEnd.Cut => NextStart(starts, start, loop) - start,
                _ => full,
            };
            length = Math.Min(length, full);
            for (int j = 0; j < length; j++)
            {
                double pos = j * rate;
                int i0 = (int)pos;
                float f = (float)(pos - i0);
                float l = sample.Left[i0] + (sample.Left[i0 + 1] - sample.Left[i0]) * f;
                float r = sample.Right[i0] + (sample.Right[i0 + 1] - sample.Right[i0]) * f;
                if (length < full && j > length - fade)
                {
                    float g = (length - j) / (float)fade;
                    l *= g; r *= g;
                }
                int o = (start + j) % loop;
                left[o] += l;
                right[o] += r;
            }
        }
        Limit(left, right);
        return new RenderedNote(60, left, right, sr);
    }

    /// <summary>The next note start after <paramref name="start"/>, wrapping into the next pass of the loop.</summary>
    private static int NextStart(List<int> starts, int start, int loop)
    {
        foreach (var s in starts) if (s > start) return s;
        return starts[0] + loop;
    }

    /// <param name="hits">The rendered sample of each drum, by its short name.</param>
    public static RenderedNote Beat(BeatSequence seq, IReadOnlyDictionary<string, RenderedNote> hits, int sampleRate)
    {
        int sr = sampleRate;
        int loop = seq.LoopSamples(sr);
        var left = new float[loop];
        var right = new float[loop];
        foreach (var (drum, sample) in hits)
        {
            if (seq.Muted.Contains(drum) || !seq.Rows.TryGetValue(drum, out var row)) continue;
            double rate = sample.SampleRate / (double)sr;
            int length = (int)((sample.Left.Length - 1) / rate);
            for (int step = 0; step < seq.Length; step++)
            {
                if (row[step] <= BeatSequence.Off) continue;
                float gain = (float)Math.Pow(BeatSequence.Gain(row[step]), 4);
                int start = seq.StepStart(step, sr);
                for (int j = 0; j < length; j++)
                {
                    int i0 = (int)(j * rate);
                    int o = (start + j) % loop;
                    left[o] += sample.Left[i0] * gain;
                    right[o] += sample.Right[i0] * gain;
                }
            }
        }
        Limit(left, right);
        return new RenderedNote(60, left, right, sr);
    }

    /// <summary>Scales the loop down when it would clip (several notes at once add up).</summary>
    private static void Limit(float[] left, float[] right)
    {
        float peak = 0;
        for (int i = 0; i < left.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        if (peak <= 0.95f) return;
        float g = 0.95f / peak;
        for (int i = 0; i < left.Length; i++) { left[i] *= g; right[i] *= g; }
    }
}
