using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidalStudio.Core.Synth;

public enum AcidWave { Saw, Square }

public enum AcidScale { Minor, Phrygian, Dorian, MinorPentatonic, Major, Chromatic }

/// <summary>One step of an acid line: a note (or a rest), with accent and slide like a classic bassline box.</summary>
public sealed class AcidStep
{
    /// <summary>False = rest.</summary>
    public bool Gate { get; set; }
    /// <summary>Semitones above the root key, 0..12.</summary>
    public int Note { get; set; }
    /// <summary>-1 (down), 0 or +1 (up).</summary>
    public int Octave { get; set; }
    /// <summary>Louder, brighter and snappier note.</summary>
    public bool Accent { get; set; }
    /// <summary>Hold this note into the next one and glide to its pitch (the next note is not re-triggered).</summary>
    public bool Slide { get; set; }

    [JsonIgnore]
    public int Semitones => Note + 12 * Octave;

    public AcidStep Clone() => (AcidStep)MemberwiseClone();
}

/// <summary>A pattern of up to 32 steps (sixteenth notes). Saved as one loop sample.</summary>
public sealed class AcidPattern
{
    public const int MaxSteps = 32;

    /// <summary>Number of steps that play, 1..32 (16 = one bar = one Tidal cycle at the box's tempo).</summary>
    public int Length { get; set; } = 16;
    public List<AcidStep> Steps { get; set; } = NewSteps();

    private static List<AcidStep> NewSteps() => Enumerable.Range(0, MaxSteps).Select(_ => new AcidStep()).ToList();

    [JsonIgnore]
    public bool IsEmpty => Steps.Take(Length).All(s => !s.Gate);

    public AcidPattern Clone() => new() { Length = Length, Steps = Steps.Select(s => s.Clone()).ToList() };

    /// <summary>Makes a loaded pattern safe: 32 steps, values in range.</summary>
    public void Repair()
    {
        Steps ??= new();
        while (Steps.Count < MaxSteps) Steps.Add(new AcidStep());
        if (Steps.Count > MaxSteps) Steps.RemoveRange(MaxSteps, Steps.Count - MaxSteps);
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i] ??= new AcidStep();
            Steps[i].Note = Math.Clamp(Steps[i].Note, 0, 12);
            Steps[i].Octave = Math.Clamp(Steps[i].Octave, -1, 1);
        }
        Length = Math.Clamp(Length, 1, MaxSteps);
    }

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B", "C'" };

    /// <summary>
    /// Reads a pattern written as space-separated steps: a note name relative to the root (C, C#, D … B, and C' for the
    /// octave), "-" for a rest; prefix ^ (octave up) or _ (octave down); suffix ! (accent) and/or ~ (slide).
    /// For example "C! C ^C C~ D# - C".
    /// </summary>
    public static AcidPattern Parse(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var p = new AcidPattern { Length = Math.Clamp(tokens.Length, 1, MaxSteps) };
        for (int i = 0; i < Math.Min(tokens.Length, MaxSteps); i++)
        {
            string t = tokens[i];
            var s = p.Steps[i];
            if (t is "-" or "." or "~") continue;
            int pos = 0;
            if (t[pos] == '^') { s.Octave = 1; pos++; }
            else if (t[pos] == '_') { s.Octave = -1; pos++; }
            int end = t.Length;
            while (end > pos && t[end - 1] is '!' or '~')
            {
                if (t[end - 1] == '!') s.Accent = true; else s.Slide = true;
                end--;
            }
            string name = t[pos..end].ToUpperInvariant();
            if (name.EndsWith('B') && name.Length == 2) name = Names[(Array.IndexOf(Names, name[..1]) + 11) % 12]; // Eb → D#
            int note = Array.IndexOf(Names, name);
            if (note < 0) throw new FormatException($"Unknown step \"{t}\" (use C, C#, D … B, C', or - for a rest)");
            s.Note = note;
            s.Gate = true;
        }
        return p;
    }

    /// <summary>The pattern in the format <see cref="Parse"/> reads.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var s = Steps[i];
            if (!s.Gate) { sb.Append('-'); continue; }
            if (s.Octave > 0) sb.Append('^'); else if (s.Octave < 0) sb.Append('_');
            sb.Append(Names[s.Note]);
            if (s.Accent) sb.Append('!');
            if (s.Slide) sb.Append('~');
        }
        return sb.ToString();
    }
}

/// <summary>The sound of the bassline box: one oscillator into a resonant 4-pole low-pass with an envelope.</summary>
public sealed class AcidVoice
{
    public AcidWave Wave { get; set; } = AcidWave.Saw;
    /// <summary>Semitones, -12..12.</summary>
    public double Tune { get; set; }
    /// <summary>Hz, where the filter sits between notes.</summary>
    public double Cutoff { get; set; } = 380;
    /// <summary>0..1; near 1 the filter rings by itself.</summary>
    public double Resonance { get; set; } = 0.78;
    /// <summary>How far each note opens the filter, 0..1.</summary>
    public double EnvMod { get; set; } = 0.55;
    /// <summary>How long the filter takes to close, seconds (accented notes always close quickly).</summary>
    public double Decay { get; set; } = 0.4;
    /// <summary>How much accented notes jump out (louder and brighter, building up over consecutive accents), 0..1.</summary>
    public double Accent { get; set; } = 0.6;
    /// <summary>Glide time for slides, seconds.</summary>
    public double SlideTime { get; set; } = 0.06;
    /// <summary>How long a note holds within its step, 0.1..1.</summary>
    public double Gate { get; set; } = 0.55;
    /// <summary>Distortion after the filter, 0..1.</summary>
    public double Drive { get; set; } = 0.15;
    /// <summary>Output level after normalising, 0..1.</summary>
    public double Level { get; set; } = 0.9;

    public AcidVoice Clone() => (AcidVoice)MemberwiseClone();
}

/// <summary>Tempo-synced effects of the bassline box.</summary>
public sealed class AcidFx
{
    public double DelayMix { get; set; }
    /// <summary>Delay time in sixteenth notes (3 = dotted eighth).</summary>
    public double DelaySteps { get; set; } = 3;
    public double DelayFeedback { get; set; } = 0.4;
    public double ReverbMix { get; set; }
    public double ReverbSize { get; set; } = 0.5;

    public AcidFx Clone() => (AcidFx)MemberwiseClone();
}

/// <summary>
/// Tidepool Acid: a bassline box with 8 pattern slots. Every slot is saved as one seamless loop in one SuperDirt
/// sound folder, so s "name" # n 0 is pattern A, n 1 pattern B, …
/// </summary>
public sealed class AcidBox
{
    public const int Slots = 8;
    public static readonly string SlotNames = "ABCDEFGH";

    public int Version { get; set; } = 1;
    /// <summary>The Tidal sound name.</summary>
    public string Name { get; set; } = "acidloop";
    public double Bpm { get; set; } = 130;
    /// <summary>MIDI note of the root key (36 = C2).</summary>
    public int Root { get; set; } = 36;
    /// <summary>Delays every second sixteenth, 0..0.5 of a step.</summary>
    public double Swing { get; set; }
    /// <summary>The scale Randomise and Mutate pick notes from.</summary>
    public AcidScale Scale { get; set; } = AcidScale.Minor;
    public AcidVoice Voice { get; set; } = new();
    public AcidFx Fx { get; set; } = new();
    public List<AcidPattern> Patterns { get; set; } = new();
    public bool Normalize { get; set; } = true;
    public int SampleRate { get; set; } = 48000;

    /// <summary>Seconds per sixteenth note.</summary>
    [JsonIgnore]
    public double StepSeconds => 60.0 / Math.Clamp(Bpm, 40, 300) / 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AcidBox FromJson(string json)
    {
        var box = JsonSerializer.Deserialize<AcidBox>(json, JsonOptions) ?? new AcidBox();
        box.Repair();
        return box;
    }

    public AcidBox Clone() => FromJson(ToJson());

    /// <summary>Makes sure there are 8 valid slots (also after loading older or hand-edited JSON).</summary>
    public void Repair()
    {
        Voice ??= new();
        Fx ??= new();
        Patterns ??= new();
        while (Patterns.Count < Slots) Patterns.Add(new AcidPattern());
        if (Patterns.Count > Slots) Patterns.RemoveRange(Slots, Patterns.Count - Slots);
        for (int i = 0; i < Patterns.Count; i++)
        {
            Patterns[i] ??= new AcidPattern();
            Patterns[i].Repair();
        }
        Root = Math.Clamp(Root, 12, 84);
    }

    /// <summary>The slots that are saved: every slot up to the last one with notes (so n keeps matching the slot letter).</summary>
    [JsonIgnore]
    public int SavedSlotCount
    {
        get
        {
            for (int i = Patterns.Count - 1; i >= 0; i--)
                if (!Patterns[i].IsEmpty) return i + 1;
            return 0;
        }
    }

    /// <summary>Problems that stop the box from being saved (empty when it can be saved).</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (!SampleExport.IsValidName(Name))
            problems.Add($"\"{Name}\" can't be used as a sound name: use lowercase letters, digits, - or _, starting with a letter.");
        if (SavedSlotCount == 0) problems.Add("Every pattern is empty: put some notes in a pattern first.");
        return problems;
    }

    /// <summary>Tidal's cycles per second for this tempo, assuming 4 beats per cycle.</summary>
    [JsonIgnore]
    public string CpsExpression => $"setcps ({Bpm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}/60/4)";

    /// <summary>How many Tidal cycles (bars of 16 steps) a pattern lasts.</summary>
    public static string Cycles(AcidPattern p) =>
        (p.Length / 16.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
