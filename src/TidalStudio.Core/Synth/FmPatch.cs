using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidalStudio.Core.Synth;

/// <summary>One FM operator: a sine oscillator with its own envelope that is either heard (a carrier) or bends the
/// phase of another operator (a modulator), depending on the algorithm.</summary>
public sealed class FmOperator
{
    public bool Enabled { get; set; } = true;
    /// <summary>Frequency ratio to the note: 0 = ×0.5, 1 = ×1, 2 = ×2, … 16.</summary>
    public int Coarse { get; set; } = 1;
    /// <summary>Adds to the ratio: ratio × (1 + Fine), 0..0.99 (inharmonic, bell-like ratios).</summary>
    public double Fine { get; set; }
    /// <summary>Small detune in cents (-50..50), for chorus-like beating between operators.</summary>
    public double Detune { get; set; }
    /// <summary>Ignore the note and play <see cref="FixedHz"/> (metallic and drum sounds).</summary>
    public bool Fixed { get; set; }
    public double FixedHz { get; set; } = 440;
    /// <summary>0..1. A carrier's loudness, or a modulator's depth (how bright it makes its target).</summary>
    public double Level { get; set; } = 0.8;
    /// <summary>0..1: how much quieter / less bright the operator gets for higher notes (and louder for lower ones).</summary>
    public double KeyScale { get; set; }
    public EnvelopeSettings Envelope { get; set; } = new() { Attack = 0.002, Decay = 1, Sustain = 0.6, Release = 0.4 };

    [JsonIgnore]
    public double Ratio => (Coarse <= 0 ? 0.5 : Coarse) * (1 + Math.Clamp(Fine, 0, 0.99));

    /// <summary>Sets <see cref="Coarse"/> and <see cref="Fine"/> to the nearest representation of a ratio.</summary>
    public FmOperator WithRatio(double ratio)
    {
        if (ratio < 1)
        {
            Coarse = 0;
            Fine = Math.Clamp(ratio / 0.5 - 1, 0, 0.99);
        }
        else
        {
            Coarse = Math.Clamp((int)Math.Floor(ratio), 1, 16);
            Fine = Math.Clamp(ratio / Coarse - 1, 0, 0.99);
        }
        return this;
    }
}

/// <summary>
/// How the six operators are wired: "6>5" means operator 6 modulates operator 5. Operators that modulate nothing
/// are carriers (they are what you hear). Operator 6 always has the feedback loop.
/// </summary>
public sealed class FmAlgorithm
{
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<(int From, int To)> Links { get; }

    public FmAlgorithm(string name, string description, string links)
    {
        Name = name;
        Description = description;
        Links = links.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('>'))
            .Select(p => (int.Parse(p[0]), int.Parse(p[1])))
            .ToList();
    }

    /// <summary>Operators (1..6) that modulate nothing: they are heard.</summary>
    public IReadOnlyList<int> Carriers => Enumerable.Range(1, FmPatch.OperatorCount).Where(IsCarrier).ToList();

    public bool IsCarrier(int op) => Links.All(l => l.From != op);

    /// <summary>Operators that modulate <paramref name="op"/>.</summary>
    public IEnumerable<int> ModulatorsOf(int op) => Links.Where(l => l.To == op).Select(l => l.From);

    public override string ToString() => Name;
}

/// <summary>A six-operator FM sound, plus how to render it to samples.</summary>
public sealed class FmPatch
{
    public const int OperatorCount = 6;

    /// <summary>The wirings to choose from. Every link goes from a higher operator to a lower one, so rendering
    /// from 6 down to 1 always has the modulator's output ready.</summary>
    public static readonly IReadOnlyList<FmAlgorithm> Algorithms = new[]
    {
        new FmAlgorithm("1 · Tower", "One chain 6→5→4→3→2→1: very rich, harsh and metallic", "6>5 5>4 4>3 3>2 2>1"),
        new FmAlgorithm("2 · Two towers", "Two chains of three (3→2→1 and 6→5→4): brass, strings, layered basses", "3>2 2>1 6>5 5>4"),
        new FmAlgorithm("3 · Three pairs", "Three modulator/carrier pairs: electric pianos, mallets, bells", "2>1 4>3 6>5"),
        new FmAlgorithm("4 · Pair + tower", "A pair (2→1) and a chain of four (6→5→4→3): a clean body plus a gritty layer", "2>1 6>5 5>4 4>3"),
        new FmAlgorithm("5 · Twin modulators", "Two modulators on carrier 1, and a chain 6→5→4: basses and clavs", "2>1 3>1 6>5 5>4"),
        new FmAlgorithm("6 · Fan out", "A pair (2→1) and operator 6 driving three carriers: organ-like brass and pads", "2>1 6>3 6>4 6>5"),
        new FmAlgorithm("7 · Deep tree", "A chain 6→5→4→3 and operator 2 both into carrier 1: dense leads and noises", "2>1 3>1 4>3 5>4 6>5"),
        new FmAlgorithm("8 · Stack + three", "A chain 3→2→1 and three plain carriers (4, 5, 6): bell over an organ", "3>2 2>1"),
        new FmAlgorithm("9 · Organ", "Six carriers, no modulation: additive drawbar organ (6 can still use feedback)", ""),
    };

    /// <summary>Sound name in Tidal (the sample folder): s "name".</summary>
    public string Name { get; set; } = "myfm";
    /// <summary>Index into <see cref="Algorithms"/>.</summary>
    public int Algorithm { get; set; } = 2;
    /// <summary>Operator 6 modulating itself, 0..1: from sine towards a buzzy saw (or noise when very high).</summary>
    public double Feedback { get; set; }
    /// <summary>Scales every modulator's depth, 0..2 (1 = as set): one knob for the whole sound's brightness.</summary>
    public double Brightness { get; set; } = 1;
    /// <summary>0..1: envelopes get shorter for higher notes (like a real piano or mallet).</summary>
    public double KeyRate { get; set; }
    public List<FmOperator> Operators { get; set; } = DefaultOperators();

    // Pitch envelope: starts at Amount semitones and falls back to the note.
    public double PitchEnvAmount { get; set; }
    public double PitchEnvDecay { get; set; } = 0.1;

    // LFO
    public double LfoRate { get; set; } = 5;
    /// <summary>Vibrato depth in cents.</summary>
    public double Vibrato { get; set; }
    /// <summary>Tremolo depth, 0..1.</summary>
    public double Tremolo { get; set; }
    /// <summary>Seconds before the LFO fades in.</summary>
    public double LfoDelay { get; set; } = 0.3;

    public FxSettings Fx { get; set; } = new();
    public double Volume { get; set; } = 0.8;

    // Rendering (same meaning as in SynthPatch)
    public string Notes { get; set; } = "c5";
    public double Hold { get; set; } = 1;
    public double MaxLength { get; set; } = 6;
    public bool Normalize { get; set; } = true;
    public int SampleRate { get; set; } = 48000;

    [JsonIgnore]
    public FmAlgorithm CurrentAlgorithm => Algorithms[Math.Clamp(Algorithm, 0, Algorithms.Count - 1)];

    public static List<FmOperator> DefaultOperators() => new()
    {
        new FmOperator { Level = 0.9 },
        new FmOperator { Level = 0.5, Envelope = new() { Attack = 0.001, Decay = 0.8, Sustain = 0.2, Release = 0.4 } },
        new FmOperator { Level = 0.8 },
        new FmOperator { Coarse = 2, Level = 0.3, Envelope = new() { Attack = 0.001, Decay = 0.5, Sustain = 0.1, Release = 0.4 } },
        new FmOperator { Enabled = false, Level = 0.6 },
        new FmOperator { Enabled = false, Level = 0.3 },
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static FmPatch FromJson(string json)
    {
        var patch = JsonSerializer.Deserialize<FmPatch>(json, JsonOptions) ?? new FmPatch();
        patch.Repair();
        return patch;
    }

    public FmPatch Clone() => FromJson(ToJson());

    /// <summary>Always exactly six operators and a valid algorithm.</summary>
    public void Repair()
    {
        Operators ??= DefaultOperators();
        while (Operators.Count < OperatorCount) Operators.Add(new FmOperator { Enabled = false });
        if (Operators.Count > OperatorCount) Operators.RemoveRange(OperatorCount, Operators.Count - OperatorCount);
        foreach (var op in Operators) op.Envelope ??= new EnvelopeSettings();
        Algorithm = Math.Clamp(Algorithm, 0, Algorithms.Count - 1);
        Fx ??= new FxSettings();
    }
}
