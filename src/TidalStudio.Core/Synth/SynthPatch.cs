using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidalStudio.Core.Synth;

public enum WarpMode { None, Bend, Sync, Fold, Crush }
public enum FilterMode { Off, LowPass, HighPass, BandPass, Notch }
public enum LfoShape { Sine, Triangle, Saw, Square, SampleHold }
public enum ModTarget { None, Pitch, Position, Cutoff, Amp, Pan, Warp }

/// <summary>One wavetable oscillator ("engine").</summary>
public sealed class OscSettings
{
    public bool Enabled { get; set; } = true;
    public string Table { get; set; } = "Basic";
    /// <summary>Position in the wavetable, 0..1 (morphs between its frames).</summary>
    public double Position { get; set; }
    public int Octave { get; set; }
    public int Semi { get; set; }
    /// <summary>Fine tune in cents.</summary>
    public double Fine { get; set; }
    public double Level { get; set; } = 0.8;
    /// <summary>-1 (left) .. 1 (right).</summary>
    public double Pan { get; set; }
    public int Unison { get; set; } = 1;
    /// <summary>Spread of the unison voices in cents.</summary>
    public double Detune { get; set; } = 15;
    /// <summary>Stereo width of the unison voices, 0..1.</summary>
    public double Spread { get; set; } = 0.6;
    public WarpMode Warp { get; set; }
    public double WarpAmount { get; set; }
}

/// <summary>ADSR in seconds (sustain is a level 0..1).</summary>
public sealed class EnvelopeSettings
{
    public double Attack { get; set; } = 0.005;
    public double Decay { get; set; } = 0.3;
    public double Sustain { get; set; } = 0.7;
    public double Release { get; set; } = 0.3;
}

public sealed class ModEnvelopeSettings
{
    public EnvelopeSettings Envelope { get; set; } = new() { Attack = 0, Decay = 0.6, Sustain = 0, Release = 0.3 };
    public ModTarget Target { get; set; } = ModTarget.Position;
    /// <summary>-1..1.</summary>
    public double Amount { get; set; }
}

public sealed class FilterSettings
{
    public FilterMode Mode { get; set; } = FilterMode.LowPass;
    public double Cutoff { get; set; } = 6000;
    /// <summary>0..1.</summary>
    public double Resonance { get; set; } = 0.15;
    /// <summary>Saturation before the filter, 0..1.</summary>
    public double Drive { get; set; }
    /// <summary>Filter envelope depth in octaves (-8..8).</summary>
    public double EnvAmount { get; set; }
    /// <summary>How much the cutoff follows the note, 0..1.</summary>
    public double KeyTrack { get; set; } = 0.5;
}

public sealed class LfoSettings
{
    public LfoShape Shape { get; set; } = LfoShape.Sine;
    public double Rate { get; set; } = 3;
    /// <summary>0..1.</summary>
    public double Amount { get; set; }
    public ModTarget Target { get; set; } = ModTarget.None;
    /// <summary>Delay before the LFO fades in, seconds.</summary>
    public double FadeIn { get; set; }
}

public sealed class FxSettings
{
    /// <summary>Saturation, 0..1.</summary>
    public double Drive { get; set; }
    public double ChorusMix { get; set; }
    public double DelayMix { get; set; }
    public double DelayTime { get; set; } = 0.25;
    public double DelayFeedback { get; set; } = 0.35;
    public double ReverbMix { get; set; }
    public double ReverbSize { get; set; } = 0.7;
}

/// <summary>Everything that defines a sound, plus how to render it to samples.</summary>
public sealed class SynthPatch
{
    /// <summary>Sound name in Tidal (the sample folder): s "name".</summary>
    public string Name { get; set; } = "mysynth";
    public List<OscSettings> Oscillators { get; set; } = new()
    {
        new OscSettings(),
        new OscSettings { Enabled = false, Table = "PWM", Octave = -1, Level = 0.6 },
        new OscSettings { Enabled = false, Table = "Harmonics", Level = 0.5 },
    };
    public FilterSettings Filter { get; set; } = new();
    public EnvelopeSettings AmpEnvelope { get; set; } = new();
    public EnvelopeSettings FilterEnvelope { get; set; } = new() { Attack = 0.002, Decay = 0.4, Sustain = 0.2, Release = 0.3 };
    public ModEnvelopeSettings ModEnvelope { get; set; } = new();
    public LfoSettings Lfo1 { get; set; } = new();
    public LfoSettings Lfo2 { get; set; } = new() { Rate = 0.25 };
    public FxSettings Fx { get; set; } = new();
    public double Volume { get; set; } = 0.8;

    // Rendering
    /// <summary>Notes to render, Tidal style: "c5" (= note 0, MIDI 60), "c2 c3 c4", or numbers ("0 7 12").</summary>
    public string Notes { get; set; } = "c5";
    /// <summary>How long the key is held, in seconds; the release (and effect tails) come after.</summary>
    public double Hold { get; set; } = 1;
    /// <summary>Longest a sample may be, in seconds (trailing silence is trimmed).</summary>
    public double MaxLength { get; set; } = 6;
    public bool Normalize { get; set; } = true;
    public int SampleRate { get; set; } = 48000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SynthPatch FromJson(string json)
    {
        var patch = JsonSerializer.Deserialize<SynthPatch>(json, JsonOptions) ?? new SynthPatch();
        while (patch.Oscillators.Count < 3) patch.Oscillators.Add(new OscSettings { Enabled = false });
        return patch;
    }

    public SynthPatch Clone() => FromJson(ToJson());

    /// <summary>
    /// Parses <see cref="Notes"/> into MIDI note numbers. Accepts Tidal note names (c5 = MIDI 60, cs4 / c#4, ef3)
    /// and numbers relative to c5 (0 = MIDI 60, like Tidal's note).
    /// </summary>
    public static List<int> ParseNotes(string text)
    {
        var result = new List<int>();
        foreach (var token in text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                result.Add(60 + (int)Math.Round(n));
                continue;
            }
            if (TryParseNoteName(token, out int midi)) result.Add(midi);
        }
        return result.Distinct().ToList();
    }

    /// <summary>Tidal note name: letter, optional sharps (s, #) / flats (f), optional octave (default 5).</summary>
    public static bool TryParseNoteName(string token, out int midi)
    {
        midi = 0;
        token = token.Trim().ToLowerInvariant();
        if (token.Length == 0) return false;
        int pc = token[0] switch { 'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5, 'g' => 7, 'a' => 9, 'b' => 11, _ => -1 };
        if (pc < 0) return false;
        int i = 1;
        while (i < token.Length && token[i] is 's' or '#' or 'f')
        {
            pc += token[i] == 'f' ? -1 : 1;
            i++;
        }
        int octave = 5;
        if (i < token.Length && !int.TryParse(token[i..], NumberStyles.Integer, CultureInfo.InvariantCulture, out octave)) return false;
        midi = 60 + pc + (octave - 5) * 12;
        return midi is >= 0 and <= 127;
    }

    /// <summary>Tidal-style name for a MIDI note: 60 → "c5", 61 → "cs5".</summary>
    public static string NoteName(int midi)
    {
        string[] names = { "c", "cs", "d", "ds", "e", "f", "fs", "g", "gs", "a", "as", "b" };
        return names[((midi % 12) + 12) % 12] + (Math.Floor((midi - 60) / 12.0) + 5).ToString(CultureInfo.InvariantCulture);
    }
}
