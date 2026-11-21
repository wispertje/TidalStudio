namespace TidalStudio.Core.Synth;

/// <summary>
/// Synthwave / 80s retro: warm, heavily chorused polysynth pads and strings, brassy chords, a sax-like lead, chorus basses
/// and tape-worn keys. Sound names start with "wave". Play chords from Tidal (note "c'maj7", "f'add9" …) — each sample is
/// one note, so any chord works.
/// </summary>
internal static class SynthwavePresets
{
    public static readonly IReadOnlyList<(string Name, Func<SynthPatch> Create)> All = new (string, Func<SynthPatch>)[]
    {
        ("Ensemble chorus pad", ChorusPad),
        ("Sunset pad (slow swell)", SunsetPad),
        ("String machine", Strings),
        ("Poly brass", PolyBrass),
        ("Reed lead (sax-like)", Sax),
        ("Neon lead", NeonLead),
        ("Chorus bass", ChorusBass),
        ("Night drive bass", DriveBass),
        ("Tape keys", TapeKeys),
        ("Dream arp", DreamArp),
        ("Glass pluck (echo)", GlassPluck),
    };

    /// <summary>Saw and a slowly moving pulse through a soft low-pass, drowned in a slow, deep ensemble chorus.</summary>
    private static SynthPatch ChorusPad() => SynthPresets.Base("wavepad", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 3; o.Detune = 9; o.Spread = 0.8; o.Level = 0.6;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "PWM"; o2.Position = 0.4; o2.Level = 0.5;
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.35, Amount = 0.3, Target = ModTarget.Position };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 1900, Resonance = 0.12, EnvAmount = 1, KeyTrack = 0.4 };
        p.FilterEnvelope = new() { Attack = 0.8, Decay = 1.5, Sustain = 0.6, Release = 1.2 };
        p.AmpEnvelope = new() { Attack = 0.5, Decay = 1, Sustain = 0.9, Release = 1.6 };
        p.Fx.ChorusMix = 0.7; p.Fx.ChorusRate = 0.45; p.Fx.ChorusDepth = 6.5;
        p.Fx.ReverbMix = 0.45; p.Fx.ReverbSize = 0.9; p.Fx.Tape = 0.2;
        p.Notes = "c3 c4 c5";
        p.Hold = 2.5;
        p.MaxLength = 8;
    });

    /// <summary>The filter opens over two seconds: a pad that blooms, for long chords under a slow progression.</summary>
    private static SynthPatch SunsetPad() => SynthPresets.Base("wavesunset", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 5; o.Detune = 14; o.Spread = 1; o.Level = 0.6;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 1 / 3.0; o2.Octave = 1; o2.Level = 0.25;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 500, Resonance = 0.2, EnvAmount = 2.6, KeyTrack = 0.4 };
        p.FilterEnvelope = new() { Attack = 2, Decay = 2, Sustain = 0.7, Release = 1.5 };
        p.AmpEnvelope = new() { Attack = 0.9, Decay = 1, Sustain = 0.95, Release = 2 };
        p.Fx.ChorusMix = 0.6; p.Fx.ChorusRate = 0.3; p.Fx.ChorusDepth = 7;
        p.Fx.DelayMix = 0.15; p.Fx.DelayTime = 0.5; p.Fx.DelayFeedback = 0.4;
        p.Fx.ReverbMix = 0.55; p.Fx.ReverbSize = 0.95; p.Fx.Tape = 0.3;
        p.Notes = "c3 c4 c5";
        p.Hold = 3.5;
        p.MaxLength = 10;
    });

    /// <summary>Bright detuned saws with a faster, shimmering chorus: the string-machine ensemble sound.</summary>
    private static SynthPatch Strings() => SynthPresets.Base("wavestrings", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 5; o.Detune = 16; o.Spread = 1; o.Level = 0.65;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 3600, Resonance = 0.05, KeyTrack = 0.5 };
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 5.5, Amount = 0.004, Target = ModTarget.Pitch, FadeIn = 0.5 };
        p.AmpEnvelope = new() { Attack = 0.35, Decay = 0.8, Sustain = 0.9, Release = 1.2 };
        p.Fx.ChorusMix = 0.8; p.Fx.ChorusRate = 0.9; p.Fx.ChorusDepth = 7.5;
        p.Fx.ReverbMix = 0.4; p.Fx.ReverbSize = 0.85; p.Fx.Tape = 0.15;
        p.Notes = "c3 c4 c5";
        p.Hold = 2;
        p.MaxLength = 7;
    });

    /// <summary>Two detuned saws with a filter that swells in: big brassy polysynth chords.</summary>
    private static SynthPatch PolyBrass() => SynthPresets.Base("wavebrass", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 2; o.Detune = 8; o.Spread = 0.7; o.Level = 0.6;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Fine = -9; o2.Level = 0.55;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 650, Resonance = 0.15, EnvAmount = 3.4, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0.12, Decay = 0.7, Sustain = 0.45, Release = 0.4 };
        p.AmpEnvelope = new() { Attack = 0.05, Decay = 0.5, Sustain = 0.85, Release = 0.45 };
        p.Fx.ChorusMix = 0.35; p.Fx.ChorusRate = 0.6; p.Fx.ChorusDepth = 4;
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.8;
        p.Notes = "c3 c4 c5";
        p.Hold = 0.8;
        p.MaxLength = 4;
    });

    /// <summary>
    /// A reedy, nasal tone (a vowel wavetable plus a saw, with a little breath noise) whose filter opens with each note
    /// and a vibrato that comes in late: a stand-in for the saxophone solos of the genre.
    /// </summary>
    private static SynthPatch Sax() => SynthPresets.Base("wavesax", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Formant"; o.Position = 0.55; o.Level = 0.7;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Level = 0.35; o2.Fine = 4;
        var o3 = p.Oscillators[2];
        o3.Enabled = true; o3.Table = "Noise"; o3.Position = 0.4; o3.Level = 0.05; // breath
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 1100, Resonance = 0.25, Drive = 0.2, EnvAmount = 2.2, KeyTrack = 0.7 };
        p.FilterEnvelope = new() { Attack = 0.07, Decay = 0.5, Sustain = 0.55, Release = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.035, Decay = 0.3, Sustain = 0.9, Release = 0.22 };
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 5.2, Amount = 0.014, Target = ModTarget.Pitch, FadeIn = 0.45 };
        p.Lfo2 = new() { Shape = LfoShape.Sine, Rate = 5.2, Amount = 0.1, Target = ModTarget.Cutoff, FadeIn = 0.45 };
        p.Fx.Drive = 0.1;
        p.Fx.DelayMix = 0.2; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.35;
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.8;
        p.Notes = "c3 c4 c5";
        p.Hold = 1.2;
        p.MaxLength = 4;
    });

    /// <summary>Detuned saws over a square an octave down, with late vibrato and a long dotted-eighth echo.</summary>
    private static SynthPatch NeonLead() => SynthPresets.Base("wavelead", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 3; o.Detune = 12; o.Spread = 0.6; o.Level = 0.6;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 1; o2.Octave = -1; o2.Level = 0.35;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2600, Resonance = 0.18, EnvAmount = 1.5, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0.01, Decay = 0.5, Sustain = 0.5, Release = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.01, Decay = 0.4, Sustain = 0.85, Release = 0.35 };
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 5.4, Amount = 0.012, Target = ModTarget.Pitch, FadeIn = 0.4 };
        p.Fx.ChorusMix = 0.25;
        p.Fx.DelayMix = 0.35; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.45;
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.85; p.Fx.Tape = 0.15;
        p.Notes = "c4 c5 c6";
        p.Hold = 1;
        p.MaxLength = 5;
    });

    /// <summary>A round pulse bass with a sub, given width by the chorus (it stays mono-safe: the chorus is light).</summary>
    private static SynthPatch ChorusBass() => SynthPresets.Base("wavebass", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = 0.3; o.Level = 0.65;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0; o2.Octave = -1; o2.Level = 0.6;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 520, Resonance = 0.2, EnvAmount = 2, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.25, Sustain = 0.3, Release = 0.12 };
        p.AmpEnvelope = new() { Attack = 0.003, Decay = 0.4, Sustain = 0.75, Release = 0.1 };
        p.Fx.ChorusMix = 0.35; p.Fx.ChorusRate = 0.7; p.Fx.ChorusDepth = 3;
        p.Notes = "c1 c2 c3";
        p.Hold = 0.4;
        p.MaxLength = 2;
    });

    /// <summary>A punchier saw bass for driving eighth notes (octave jumps with n, or note "c c6 c c6").</summary>
    private static SynthPatch DriveBass() => SynthPresets.Base("wavedrive", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 2; o.Detune = 7; o.Level = 0.65;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 1; o2.Octave = -1; o2.Level = 0.4;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 700, Resonance = 0.28, Drive = 0.25, EnvAmount = 2.6, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.14, Sustain = 0.2, Release = 0.08 };
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.25, Sustain = 0.6, Release = 0.06 };
        p.Notes = "c1 c2 c3";
        p.Hold = 0.2;
        p.MaxLength = 1.5;
    });

    /// <summary>Soft triangle keys with a sine an octave up, worn by tape wow and flutter.</summary>
    private static SynthPatch TapeKeys() => SynthPresets.Base("wavekeys", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 1 / 3.0; o.Level = 0.7;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0; o2.Octave = 1; o2.Level = 0.3;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2400, Resonance = 0.05, EnvAmount = 1.5, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.6, Sustain = 0.2, Release = 0.5 };
        p.AmpEnvelope = new() { Attack = 0.004, Decay = 1.6, Sustain = 0.2, Release = 0.7 };
        p.Fx.ChorusMix = 0.5; p.Fx.ChorusRate = 0.5; p.Fx.ChorusDepth = 5;
        p.Fx.ReverbMix = 0.3; p.Fx.ReverbSize = 0.7; p.Fx.Tape = 0.6;
        p.Notes = "c3 c4 c5";
        p.Hold = 1.2;
        p.MaxLength = 5;
    });

    /// <summary>A short square bleep for arpeggios, with ping-pong echo and a big room.</summary>
    private static SynthPatch DreamArp() => SynthPresets.Base("wavearp", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = 0.15; o.Level = 0.65;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Fine = 7; o2.Level = 0.3;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 1800, Resonance = 0.3, EnvAmount = 2.4, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.14, Sustain = 0.05, Release = 0.1 };
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.25, Sustain = 0, Release = 0.12 };
        p.Fx.ChorusMix = 0.3;
        p.Fx.DelayMix = 0.4; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.5;
        p.Fx.ReverbMix = 0.4; p.Fx.ReverbSize = 0.9;
        p.Notes = "c3 c4 c5";
        p.Hold = 0.15;
        p.MaxLength = 3;
    });

    /// <summary>A bright, glassy pluck (digital wavetable sweeping down) for counter-melodies, with a dotted echo.</summary>
    private static SynthPatch GlassPluck() => SynthPresets.Base("waveglass", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Digital"; o.Position = 0.3; o.Level = 0.5;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 1 / 3.0; o2.Level = 0.5;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.4, Sustain = 0, Release = 0.2 }, Target = ModTarget.Position, Amount = 0.4 };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 1500, Resonance = 0.2, EnvAmount = 3, KeyTrack = 0.7 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.3, Sustain = 0, Release = 0.2 };
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.7, Sustain = 0, Release = 0.3 };
        p.Fx.ChorusMix = 0.4;
        p.Fx.DelayMix = 0.35; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.45;
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.85;
        p.Notes = "c4 c5";
        p.Hold = 0.3;
        p.MaxLength = 4;
    });
}
