namespace TidalStudio.Core.Synth;

/// <summary>
/// Synth pop: bright detuned stabs, plucks, octave basses, glassy keys, lush pads and choirs.
/// Rendered on c notes, so Tidal's note names line up: s "popbass" # n 1 # note "&lt;c a f g&gt;".
/// </summary>
internal static class PopPresets
{
    public static readonly IReadOnlyList<(string Name, Func<SynthPatch> Create)> All = new (string, Func<SynthPatch>)[]
    {
        ("Pop brass stab", BrassStab),
        ("Pop chord stab (major)", ChordStab),
        ("Bright pop pluck", Pluck),
        ("Pop arp bleep", ArpBleep),
        ("Clav pluck", Clav),
        ("Pop octave bass", OctaveBass),
        ("Square sub bass", SquareSub),
        ("Glass bell keys", BellKeys),
        ("Lush pop pad", LushPad),
        ("Synth choir", Choir),
        ("Vibrato pop lead", Lead),
    };

    /// <summary>Two detuned saws, filter opening with a little attack, chorus: the classic synth-pop brass hit.</summary>
    private static SynthPatch BrassStab() => SynthPresets.Base("popbrass", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 3; o.Detune = 14; o.Spread = 0.7;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Fine = -8; o2.Level = 0.6;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 900, Resonance = 0.12, EnvAmount = 3, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0.03, Decay = 0.35, Sustain = 0.35, Release = 0.2 };
        p.AmpEnvelope = new() { Attack = 0.008, Decay = 0.3, Sustain = 0.7, Release = 0.25 };
        p.Fx.ChorusMix = 0.4; p.Fx.ReverbMix = 0.3; p.Fx.ReverbSize = 0.6;
        p.Notes = "c4 c5";
        p.Hold = 0.4;
        p.MaxLength = 3;
    });

    /// <summary>A major triad (root, third, fifth) in one sample: play it on the beat or in rhythms.</summary>
    private static SynthPatch ChordStab() => SynthPresets.Base("popchord", p =>
    {
        int[] semis = { 0, 4, 7 };
        for (int i = 0; i < 3; i++)
        {
            var o = p.Oscillators[i];
            o.Enabled = true; o.Table = "Basic"; o.Position = 0.6; o.Semi = semis[i]; o.Level = 0.55;
            o.Unison = 2; o.Detune = 12; o.Spread = 0.8;
        }
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 1500, Resonance = 0.15, EnvAmount = 2.2, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.3, Sustain = 0.2, Release = 0.2 };
        p.AmpEnvelope = new() { Attack = 0.003, Decay = 0.45, Sustain = 0.2, Release = 0.25 };
        p.Fx.ChorusMix = 0.45; p.Fx.ReverbMix = 0.3; p.Fx.DelayMix = 0.15; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.3;
        p.Notes = "c4";
        p.Hold = 0.3;
        p.MaxLength = 3;
    });

    private static SynthPatch Pluck() => SynthPresets.Base("poppluck", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 2; o.Detune = 9;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "PWM"; o2.Position = 0.35; o2.Octave = 1; o2.Level = 0.35;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 600, Resonance = 0.25, EnvAmount = 4, KeyTrack = 0.7 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.18, Sustain = 0, Release = 0.15 };
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.35, Sustain = 0, Release = 0.2 };
        p.Fx.ChorusMix = 0.3; p.Fx.DelayMix = 0.3; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.4; p.Fx.ReverbMix = 0.25;
        p.Notes = "c4 c5";
        p.Hold = 0.3;
        p.MaxLength = 3;
    });

    /// <summary>Short square blip for fast arpeggios (arp "up" $ note "&lt;c'min a'maj&gt;").</summary>
    private static SynthPatch ArpBleep() => SynthPresets.Base("poparp", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = 0.2; o.Level = 0.7;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2200, Resonance = 0.3, EnvAmount = 2, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.1, Sustain = 0, Release = 0.08 };
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.16, Sustain = 0, Release = 0.08 };
        p.Fx.DelayMix = 0.25; p.Fx.DelayTime = 0.25; p.Fx.DelayFeedback = 0.35; p.Fx.ReverbMix = 0.2;
        p.Notes = "c4 c5 c6";
        p.Hold = 0.15;
        p.MaxLength = 2;
    });

    /// <summary>Narrow pulse through a resonant band-pass: a funky, clavinet-like pluck.</summary>
    private static SynthPatch Clav() => SynthPresets.Base("popclav", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = 0.8; o.Level = 0.8;
        p.Filter = new() { Mode = FilterMode.BandPass, Cutoff = 1200, Resonance = 0.45, EnvAmount = 2, KeyTrack = 0.8 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.12, Sustain = 0.1, Release = 0.1 };
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.25, Sustain = 0.1, Release = 0.08 };
        p.Fx.ChorusMix = 0.2; p.Fx.ReverbMix = 0.15;
        p.Notes = "c3 c4";
        p.Hold = 0.2;
        p.MaxLength = 1.5;
    });

    /// <summary>Punchy saw + square for octave-jumping eighth-note basslines.</summary>
    private static SynthPatch OctaveBass() => SynthPresets.Base("popbass", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Level = 0.7;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 1; o2.Level = 0.5; o2.Fine = 5;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 650, Resonance = 0.22, Drive = 0.2, EnvAmount = 2.5, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.16, Sustain = 0.15, Release = 0.1 };
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.3, Sustain = 0.6, Release = 0.07 };
        p.Notes = "c1 c2 c3";
        p.Hold = 0.25;
        p.MaxLength = 1.5;
    });

    private static SynthPatch SquareSub() => SynthPresets.Base("popsub", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 1; o.Level = 0.6;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0; o2.Octave = -1; o2.Level = 0.7;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 420, Resonance = 0.1, EnvAmount = 1.2, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.2, Sustain = 0.3, Release = 0.1 };
        p.AmpEnvelope = new() { Attack = 0.003, Decay = 0.4, Sustain = 0.85, Release = 0.1 };
        p.Notes = "c1 c2 c3";
        p.Hold = 0.6;
        p.MaxLength = 2;
    });

    /// <summary>Digital wavetable + sine with a bright, decaying attack: glassy 80s keys and bells.</summary>
    private static SynthPatch BellKeys() => SynthPresets.Base("popbell", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Digital"; o.Position = 0.25; o.Level = 0.5;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0; o2.Level = 0.7;
        var o3 = p.Oscillators[2];
        o3.Enabled = true; o3.Table = "Basic"; o3.Position = 0; o3.Octave = 2; o3.Semi = 7; o3.Level = 0.12; // a glassy partial
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.6, Sustain = 0, Release = 0.3 }, Target = ModTarget.Position, Amount = 0.35 };
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 1.8, Sustain = 0.1, Release = 0.8 };
        p.Fx.ChorusMix = 0.4; p.Fx.ReverbMix = 0.3; p.Fx.ReverbSize = 0.65;
        p.Notes = "c4 c5";
        p.Hold = 1;
        p.MaxLength = 5;
    });

    private static SynthPatch LushPad() => SynthPresets.Base("poppad", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.6; o.Unison = 5; o.Detune = 18; o.Spread = 1; o.Level = 0.6;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "PWM"; o2.Position = 0.4; o2.Octave = 1; o2.Unison = 3; o2.Detune = 12; o2.Level = 0.3;
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.5, Amount = 0.35, Target = ModTarget.Position };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2400, Resonance = 0.1, EnvAmount = 0.8, KeyTrack = 0.4 };
        p.FilterEnvelope = new() { Attack = 0.6, Decay = 1.2, Sustain = 0.6, Release = 1 };
        p.AmpEnvelope = new() { Attack = 0.45, Decay = 1, Sustain = 0.9, Release = 1.4 };
        p.Fx.ChorusMix = 0.6; p.Fx.ReverbMix = 0.45; p.Fx.ReverbSize = 0.85;
        p.Notes = "c4";
        p.Hold = 2.5;
        p.MaxLength = 8;
    });

    /// <summary>Formant wavetable swept slowly between vowels, with chorus: an "aah" synth choir.</summary>
    private static SynthPatch Choir() => SynthPresets.Base("popchoir", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Formant"; o.Position = 0.3; o.Unison = 4; o.Detune = 12; o.Spread = 0.9;
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 0.25, Amount = 0.2, Target = ModTarget.Position };
        p.Lfo2 = new() { Shape = LfoShape.Sine, Rate = 5, Amount = 0.006, Target = ModTarget.Pitch, FadeIn = 0.6 };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 3500, Resonance = 0.05, KeyTrack = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.35, Decay = 0.8, Sustain = 0.9, Release = 1.2 };
        p.Fx.ChorusMix = 0.55; p.Fx.ReverbMix = 0.5; p.Fx.ReverbSize = 0.85;
        p.Notes = "c4 c5";
        p.Hold = 2;
        p.MaxLength = 7;
    });

    /// <summary>Square lead with delayed vibrato and a dotted-eighth echo.</summary>
    private static SynthPatch Lead() => SynthPresets.Base("poplead", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = 0.1; o.Unison = 2; o.Detune = 6; o.Level = 0.7;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Octave = -1; o2.Level = 0.3;
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 5.5, Amount = 0.018, Target = ModTarget.Pitch, FadeIn = 0.35 };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 3000, Resonance = 0.15, EnvAmount = 1.2, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0.01, Decay = 0.4, Sustain = 0.5, Release = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.01, Decay = 0.3, Sustain = 0.85, Release = 0.3 };
        p.Fx.DelayMix = 0.3; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.4; p.Fx.ReverbMix = 0.25;
        p.Notes = "c4 c5";
        p.Hold = 1;
        p.MaxLength = 4;
    });
}
