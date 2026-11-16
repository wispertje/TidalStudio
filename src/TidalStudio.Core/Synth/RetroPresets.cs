namespace TidalStudio.Core.Synth;

/// <summary>Retro sounds: 8-bit consoles, 80s polysynths, rave and lo-fi.</summary>
internal static class RetroPresets
{
    public static readonly IReadOnlyList<(string Name, Func<SynthPatch> Create)> All = new (string, Func<SynthPatch>)[]
    {
        ("Chiptune square", ChipSquare),
        ("Chiptune pulse lead", ChipPulse),
        ("Chiptune arp bleep", ChipBleep),
        ("80s poly brass", PolyBrass),
        ("80s PWM strings", PwmStrings),
        ("Dreamy polysynth", Polysynth),
        ("Analog bass", AnalogBass),
        ("FM electric piano", ElectricPiano),
        ("Rave hoover", Hoover),
        ("Rave organ stab", RaveOrgan),
        ("Sci-fi sweep", SciFi),
        ("Lo-fi tape keys", LofiKeys),
    };

    // PWM table: position f gives a pulse of width 0.5 - 0.45 f.
    private static double PulseWidth(double width) => (0.5 - width) / 0.45;

    /// <summary>Unfiltered pulse, slightly bit-crushed, with the snappy envelope of a sound chip.</summary>
    private static SynthPatch Chip(string name, double width, Action<SynthPatch> tweak) => SynthPresets.Base(name, p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = PulseWidth(width); o.Level = 0.7;
        o.Warp = WarpMode.Crush; o.WarpAmount = 0.55;
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.1, Sustain = 0.8, Release = 0.04 };
        p.Notes = "c4 c5 c6";
        p.Hold = 0.4;
        p.MaxLength = 1.5;
        tweak(p);
    });

    private static SynthPatch ChipSquare() => Chip("chipsquare", 0.5, _ => { });

    private static SynthPatch ChipPulse() => Chip("chippulse", 0.125, p =>
    {
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 6, Amount = 0.02, Target = ModTarget.Pitch, FadeIn = 0.25 };
        p.Hold = 0.8;
        p.AmpEnvelope.Release = 0.08;
    });

    private static SynthPatch ChipBleep() => Chip("chipbleep", 0.25, p =>
    {
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.09, Sustain = 0, Release = 0.03 };
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.03, Sustain = 0, Release = 0.01 }, Target = ModTarget.Pitch, Amount = 0.5 };
        p.Hold = 0.12;
        p.Notes = "c5 c6";
    });

    private static SynthPatch PolyBrass() => SynthPresets.Base("brass80s", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 2; o.Detune = 12; o.Spread = 0.5;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Fine = 7; o2.Level = 0.6;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 800, Resonance = 0.15, EnvAmount = 2.6, KeyTrack = 0.6 };
        p.FilterEnvelope = new() { Attack = 0.08, Decay = 0.5, Sustain = 0.45, Release = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.04, Decay = 0.3, Sustain = 0.85, Release = 0.3 };
        p.Fx.ChorusMix = 0.3; p.Fx.ReverbMix = 0.25;
        p.Notes = "c4 c5";
        p.Hold = 1;
    });

    private static SynthPatch PwmStrings() => SynthPresets.Base("strings80s", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "PWM"; o.Position = 0.5; o.Unison = 3; o.Detune = 14; o.Spread = 0.9; o.Level = 0.7;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 2 / 3.0; o2.Octave = 1; o2.Unison = 2; o2.Detune = 10; o2.Level = 0.35;
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.6, Amount = 0.55, Target = ModTarget.Position };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 3200, Resonance = 0.05, KeyTrack = 0.4 };
        p.AmpEnvelope = new() { Attack = 0.6, Decay = 1, Sustain = 0.9, Release = 1.2 };
        p.Fx.ChorusMix = 0.65; p.Fx.ReverbMix = 0.4; p.Fx.ReverbSize = 0.8;
        p.Notes = "c4 c5";
        p.Hold = 2;
        p.MaxLength = 8;
    });

    private static SynthPatch Polysynth() => SynthPresets.Base("polysynth", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 3; o.Detune = 16;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "PWM"; o2.Position = 0.3; o2.Level = 0.5;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 1400, Resonance = 0.2, EnvAmount = 2, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0.005, Decay = 0.6, Sustain = 0.35, Release = 0.6 };
        p.AmpEnvelope = new() { Attack = 0.01, Decay = 0.8, Sustain = 0.6, Release = 0.7 };
        p.Fx.ChorusMix = 0.5; p.Fx.DelayMix = 0.25; p.Fx.DelayTime = 0.375; p.Fx.ReverbMix = 0.4;
        p.Notes = "c4 c5";
        p.Hold = 1;
        p.MaxLength = 6;
    });

    private static SynthPatch AnalogBass() => SynthPresets.Base("analogbass", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 1; o2.Octave = -1; o2.Level = 0.6;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 380, Resonance = 0.35, Drive = 0.3, EnvAmount = 3, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.3, Sustain = 0.2, Release = 0.15 };
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.4, Sustain = 0.8, Release = 0.1 };
        p.Notes = "c1 c2 c3";
        p.Hold = 0.6;
    });

    private static SynthPatch ElectricPiano() => SynthPresets.Base("epiano", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Digital"; o.Position = 0.15; o.Level = 0.5;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0; o2.Level = 0.8;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.4, Sustain = 0, Release = 0.2 }, Target = ModTarget.Position, Amount = 0.25 };
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 1.6, Sustain = 0.15, Release = 0.5 };
        p.Lfo2 = new() { Shape = LfoShape.Sine, Rate = 4.5, Amount = 0.15, Target = ModTarget.Amp }; // tremolo
        p.Fx.ChorusMix = 0.35; p.Fx.ReverbMix = 0.2;
        p.Notes = "c4 c5";
        p.Hold = 1.2;
    });

    /// <summary>Detuned saw stack with a pitch swoop at the start: the classic early-90s rave lead.</summary>
    private static SynthPatch Hoover() => SynthPresets.Base("hoover", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Unison = 7; o.Detune = 60; o.Spread = 1;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "PWM"; o2.Position = 0.6; o2.Octave = -1; o2.Unison = 3; o2.Detune = 30; o2.Level = 0.6;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.3, Sustain = 0, Release = 0.2 }, Target = ModTarget.Pitch, Amount = -0.12 };
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 5, Amount = 0.01, Target = ModTarget.Pitch, FadeIn = 0.5 };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 4500, Resonance = 0.1, KeyTrack = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.01, Decay = 0.5, Sustain = 0.85, Release = 0.4 };
        p.Fx.ChorusMix = 0.6; p.Fx.Drive = 0.15; p.Fx.ReverbMix = 0.25;
        p.Notes = "c3 c4";
        p.Hold = 1.2;
    });

    private static SynthPatch RaveOrgan() => SynthPresets.Base("raveorgan", p =>
    {
        int[] semis = { 0, 7, 12 };
        for (int i = 0; i < 3; i++)
        {
            var o = p.Oscillators[i];
            o.Enabled = true; o.Table = "Harmonics"; o.Position = 0.45; o.Semi = semis[i]; o.Level = i == 0 ? 0.7 : 0.45;
        }
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2500, Resonance = 0.1, EnvAmount = 1.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.25, Sustain = 0.3, Release = 0.2 };
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.35, Sustain = 0.3, Release = 0.2 };
        p.Fx.ReverbMix = 0.3; p.Fx.Drive = 0.1;
        p.Notes = "c4 c5";
        p.Hold = 0.35;
        p.MaxLength = 4;
    });

    private static SynthPatch SciFi() => SynthPresets.Base("scifi", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 2 / 3.0; o.Warp = WarpMode.Sync; o.WarpAmount = 0.3;
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.7, Amount = 0.7, Target = ModTarget.Warp };
        p.Lfo2 = new() { Shape = LfoShape.SampleHold, Rate = 8, Amount = 0.35, Target = ModTarget.Cutoff };
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2000, Resonance = 0.6, KeyTrack = 0.4 };
        p.AmpEnvelope = new() { Attack = 0.05, Decay = 0.5, Sustain = 0.8, Release = 0.6 };
        p.Fx.DelayMix = 0.35; p.Fx.DelayTime = 0.28; p.Fx.DelayFeedback = 0.55; p.Fx.ReverbMix = 0.35;
        p.Hold = 1.5;
        p.MaxLength = 6;
    });

    private static SynthPatch LofiKeys() => SynthPresets.Base("lofikeys", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 1 / 3.0; o.Warp = WarpMode.Crush; o.WarpAmount = 0.5; // triangle, crushed
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Harmonics"; o2.Position = 0.2; o2.Octave = 1; o2.Level = 0.3;
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 0.6, Amount = 0.005, Target = ModTarget.Pitch }; // tape wow
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 2200, Resonance = 0.1, KeyTrack = 0.3 };
        p.AmpEnvelope = new() { Attack = 0.003, Decay = 1.2, Sustain = 0.25, Release = 0.5 };
        p.Fx.ChorusMix = 0.25; p.Fx.ReverbMix = 0.25; p.Fx.ReverbSize = 0.5;
        p.Notes = "c4 c5";
        p.Hold = 1;
    });
}
