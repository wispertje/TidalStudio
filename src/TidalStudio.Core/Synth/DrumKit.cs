using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TidalStudio.Core.Synth;

/// <summary>The kind of drum a voice started from (sets up its layers; every setting can be changed after).</summary>
public enum DrumModel { Kick, Snare, Tom, Clap, ClosedHat, OpenHat, Cymbal, Rim, Cowbell, Zap, Shaker }

public enum NoiseColor { White, Pink, Brown }
public enum DriveMode { Soft, Hard, Fold, Crush }

/// <summary>
/// Envelope of a drum layer: rise in <see cref="Attack"/>, stay at full level for <see cref="Hold"/>, then fall to
/// silence over <see cref="Decay"/>. <see cref="Curve"/> shapes the fall: 0 = straight line, 1 = exponential (fast
/// at first, long tail — like most real drums).
/// </summary>
public sealed class DrumEnvelope
{
    public double Attack { get; set; } = 0.001;
    public double Hold { get; set; }
    public double Decay { get; set; } = 0.4;
    public double Curve { get; set; } = 0.8;

    private static readonly double ExpEnd = Math.Exp(-6.9);

    [JsonIgnore]
    public double Length => Attack + Hold + Decay;

    public double At(double t)
    {
        if (t < 0) return 0;
        if (t < Attack) return Attack <= 0 ? 1 : t / Attack;
        t -= Attack + Hold;
        if (t < 0) return 1;
        if (Decay <= 0 || t >= Decay) return 0;
        double x = t / Decay;
        double c = Math.Clamp(Curve, 0, 1);
        // Exponential to -60 dB, shifted so it reaches exactly 0 at the end of the decay.
        double exp = (Math.Exp(-6.9 * x) - ExpEnd) / (1 - ExpEnd);
        return (1 - c) * (1 - x) + c * exp;
    }

    public DrumEnvelope Clone() => (DrumEnvelope)MemberwiseClone();
}

/// <summary>An oscillator layer (the drum's body): a wavetable oscillator with a pitch drop at the start.</summary>
public sealed class DrumOscillator
{
    public bool Enabled { get; set; } = true;
    /// <summary>Wavetable (see <see cref="Wavetables.Available"/>; "Basic" goes sine → triangle → saw → square).</summary>
    public string Table { get; set; } = "Basic";
    public double Position { get; set; }
    /// <summary>Oscillator 1: pitch in Hz. Oscillator 2: ratio to oscillator 1's (current) pitch.</summary>
    public double Frequency { get; set; } = 50;
    /// <summary>Semitones above <see cref="Frequency"/> the pitch starts at, falling to it over <see cref="PitchTime"/>.</summary>
    public double PitchDrop { get; set; }
    public double PitchTime { get; set; } = 0.05;
    /// <summary>0 = straight fall, 1 = fast exponential fall.</summary>
    public double PitchCurve { get; set; } = 0.8;
    public double Level { get; set; } = 1;
    /// <summary>Oscillator 2 only: how much it frequency-modulates oscillator 1 (0..1).</summary>
    public double Fm { get; set; }
    public DrumEnvelope Envelope { get; set; } = new();

    public DrumOscillator Clone()
    {
        var c = (DrumOscillator)MemberwiseClone();
        c.Envelope = Envelope.Clone();
        return c;
    }
}

/// <summary>Filtered noise: snare wires, claps, breath, shakers.</summary>
public sealed class DrumNoise
{
    public bool Enabled { get; set; }
    public NoiseColor Color { get; set; }
    public FilterMode Filter { get; set; } = FilterMode.HighPass;
    public double Cutoff { get; set; } = 2000;
    public double Resonance { get; set; } = 0.1;
    public double Level { get; set; } = 0.6;
    public DrumEnvelope Envelope { get; set; } = new() { Decay = 0.2 };
    /// <summary>Number of hits (a clap is several hands a few ms apart). 1 = a single hit.</summary>
    public int Bursts { get; set; } = 1;
    public double BurstSpacing { get; set; } = 0.011;

    public DrumNoise Clone()
    {
        var c = (DrumNoise)MemberwiseClone();
        c.Envelope = Envelope.Clone();
        return c;
    }
}

/// <summary>Six detuned square waves through a band-pass: the metallic sound of analog hats, cymbals and cowbells.</summary>
public sealed class DrumMetal
{
    public bool Enabled { get; set; }
    /// <summary>Pitch of the square-wave cluster in semitones (0 = the classic 205–800 Hz set).</summary>
    public double Tune { get; set; }
    /// <summary>How many of the six squares sound (2 = cowbell-like, 6 = hats and cymbals).</summary>
    public int Voices { get; set; } = 6;
    public double Cutoff { get; set; } = 8000;
    public double Resonance { get; set; } = 0.3;
    public double Level { get; set; } = 0.8;
    public DrumEnvelope Envelope { get; set; } = new() { Decay = 0.08 };

    public DrumMetal Clone()
    {
        var c = (DrumMetal)MemberwiseClone();
        c.Envelope = Envelope.Clone();
        return c;
    }
}

/// <summary>The attack transient: an impulse plus a very short burst of bright noise.</summary>
public sealed class DrumClick
{
    public bool Enabled { get; set; }
    public double Level { get; set; } = 0.4;
    /// <summary>Length of the burst, seconds.</summary>
    public double Length { get; set; } = 0.004;
    /// <summary>High-pass frequency of the burst, Hz.</summary>
    public double Tone { get; set; } = 3000;

    public DrumClick Clone() => (DrumClick)MemberwiseClone();
}

/// <summary>Filter on the whole drum, with its own decaying envelope.</summary>
public sealed class DrumFilter
{
    public FilterMode Mode { get; set; } = FilterMode.Off;
    public double Cutoff { get; set; } = 8000;
    public double Resonance { get; set; } = 0.1;
    /// <summary>Octaves the cutoff starts above (or below) <see cref="Cutoff"/>, returning over <see cref="EnvDecay"/>.</summary>
    public double EnvAmount { get; set; }
    public double EnvDecay { get; set; } = 0.15;

    public DrumFilter Clone() => (DrumFilter)MemberwiseClone();
}

/// <summary>One drum sound in a kit, saved as its own Tidal sound "&lt;kit&gt;&lt;abbreviation&gt;" (like "mykitbd").</summary>
public sealed class DrumVoice
{
    public bool Enabled { get; set; } = true;
    /// <summary>Short name used in Tidal, as in the tidal-drum-machines kits: bd, sd, hh, oh, cp, …</summary>
    public string Abbreviation { get; set; } = "bd";
    /// <summary>What the voice was started from (shown on its pad).</summary>
    public DrumModel Model { get; set; } = DrumModel.Kick;

    public DrumOscillator Osc1 { get; set; } = new();
    public DrumOscillator Osc2 { get; set; } = new() { Enabled = false, Frequency = 1.5, Level = 0.5 };
    public DrumNoise Noise { get; set; } = new();
    public DrumMetal Metal { get; set; } = new();
    public DrumClick Click { get; set; } = new();
    public DrumFilter Filter { get; set; } = new();

    /// <summary>Moves every pitch (oscillators and metal), in semitones.</summary>
    public double Tune { get; set; }
    public DriveMode DriveMode { get; set; }
    /// <summary>Saturation, 0..1.</summary>
    public double Drive { get; set; }
    /// <summary>Room reverb, 0..1.</summary>
    public double Room { get; set; }
    public double RoomSize { get; set; } = 0.35;
    /// <summary>Loudness of the saved sample, 0..1 (after normalising).</summary>
    public double Level { get; set; } = 0.9;
    /// <summary>-1 (left) .. 1 (right).</summary>
    public double Pan { get; set; }
    /// <summary>Longest the sample may be, seconds.</summary>
    public double MaxLength { get; set; } = 4;
    /// <summary>How many versions to save (n 0, n 1, …); version 0 is exactly these settings.</summary>
    public int Variations { get; set; } = 1;
    /// <summary>How different the versions are, 0..1.</summary>
    public double Spread { get; set; } = 0.3;

    public DrumVoice Clone()
    {
        var c = (DrumVoice)MemberwiseClone();
        c.Osc1 = Osc1.Clone();
        c.Osc2 = Osc2.Clone();
        c.Noise = Noise.Clone();
        c.Metal = Metal.Clone();
        c.Click = Click.Clone();
        c.Filter = Filter.Clone();
        return c;
    }
}

/// <summary>A drum kit: a set of voices rendered to SuperDirt sample folders.</summary>
public sealed partial class DrumKit
{
    /// <summary>2 = layered voices. Kits saved by version 1 (one knob set per model) are converted when loaded.</summary>
    public int Version { get; set; } = 2;
    /// <summary>Kit name; each voice is saved as the sound &lt;name&gt;&lt;abbreviation&gt;.</summary>
    public string Name { get; set; } = "mykit";
    public List<DrumVoice> Voices { get; set; } = new();
    public bool Normalize { get; set; } = true;
    public int SampleRate { get; set; } = 48000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DrumKit FromJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject;
        if (node is not null && (node["Version"]?.GetValue<int>() ?? 1) < 2) return FromVersion1(node);
        return JsonSerializer.Deserialize<DrumKit>(json, JsonOptions) ?? new DrumKit();
    }

    /// <summary>Version 1 voices had one knob set per model: start from the model's layers and keep what carries over.</summary>
    private static DrumKit FromVersion1(JsonObject node)
    {
        var kit = new DrumKit
        {
            Name = node["Name"]?.GetValue<string>() ?? "mykit",
            Normalize = node["Normalize"]?.GetValue<bool>() ?? true,
            SampleRate = node["SampleRate"]?.GetValue<int>() ?? 48000,
        };
        foreach (var v in node["Voices"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var model = Enum.TryParse<DrumModel>(v["Model"]?.GetValue<string>(), out var m) ? m : DrumModel.Kick;
            var voice = DrumKits.Default(model, v["Abbreviation"]?.GetValue<string>() ?? "bd");
            voice.Enabled = v["Enabled"]?.GetValue<bool>() ?? true;
            voice.Tune = v["Tune"]?.GetValue<double>() ?? 0;
            voice.Drive = v["Drive"]?.GetValue<double>() ?? voice.Drive;
            voice.Room = v["Room"]?.GetValue<double>() ?? voice.Room;
            voice.Level = v["Level"]?.GetValue<double>() ?? voice.Level;
            voice.Pan = v["Pan"]?.GetValue<double>() ?? 0;
            voice.Variations = v["Variations"]?.GetValue<int>() ?? 1;
            voice.Spread = v["Spread"]?.GetValue<double>() ?? 0.3;
            kit.Voices.Add(voice);
        }
        return kit;
    }

    public DrumKit Clone() => FromJson(ToJson());

    /// <summary>The Tidal sound name of a voice: kit name + abbreviation ("mykit" + "bd" = "mykitbd").</summary>
    public string SoundName(DrumVoice voice) => Name + voice.Abbreviation;

    /// <summary>The voices that will be saved.</summary>
    [JsonIgnore]
    public IEnumerable<DrumVoice> ActiveVoices => Voices.Where(v => v.Enabled);

    /// <summary>Problems that stop the kit from being saved (empty when it can be saved).</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (!SampleExport.IsValidName(Name))
            problems.Add($"\"{Name}\" can't be used as a kit name: use lowercase letters, digits, - or _, starting with a letter.");
        var active = ActiveVoices.ToList();
        if (active.Count == 0) problems.Add("Turn on at least one drum.");
        foreach (var v in active.Where(v => !Abbreviation().IsMatch(v.Abbreviation)))
            problems.Add($"\"{v.Abbreviation}\" can't be used as a drum name: use lowercase letters and digits, starting with a letter.");
        foreach (var dup in active.GroupBy(v => v.Abbreviation).Where(g => g.Count() > 1))
            problems.Add($"Two drums are called \"{dup.Key}\"; every drum needs its own name.");
        return problems;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex Abbreviation();
}
