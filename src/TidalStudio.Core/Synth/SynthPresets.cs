namespace TidalStudio.Core.Synth;

/// <summary>Factory presets: starting points that show what the synth can do.</summary>
public static class SynthPresets
{
    /// <summary>Presets by category, in menu order.</summary>
    public static readonly IReadOnlyList<(string Category, IReadOnlyList<(string Name, Func<SynthPatch> Create)> Presets)> Groups =
        new (string, IReadOnlyList<(string, Func<SynthPatch>)>)[]
        {
            ("Basics", new (string, Func<SynthPatch>)[]
            {
                ("Init", () => new SynthPatch()),
                ("Supersaw lead", SupersawLead),
                ("Pluck", Pluck),
                ("Warm pad", WarmPad),
                ("Vowel pad", VowelPad),
                ("Sub bass", SubBass),
                ("Reese bass", ReeseBass),
                ("Wobble bass", WobbleBass),
                ("Sync lead", SyncLead),
                ("Glass bell", GlassBell),
                ("Digital keys", DigitalKeys),
                ("Kick", Kick),
                ("Hat", Hat),
            }),
            ("Acid techno", AcidPresets.All),
            ("Retro", RetroPresets.All),
            ("Synth pop", PopPresets.All),
        };

    /// <summary>Every preset, in menu order.</summary>
    public static readonly IReadOnlyList<(string Name, Func<SynthPatch> Create)> All =
        Groups.SelectMany(g => g.Presets).ToList();
    public static SynthPatch Create(string name) =>
        (All.FirstOrDefault(p => p.Name == name).Create ?? (() => new SynthPatch()))();

    internal static SynthPatch Base(string name, Action<SynthPatch> setup)
    {
        var p = new SynthPatch { Name = name };
        foreach (var o in p.Oscillators.Skip(1)) o.Enabled = false;
        setup(p);
        return p;
    }

    private static SynthPatch SupersawLead() => Base("sawlead", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.66; o.Unison = 7; o.Detune = 35; o.Spread = 0.9;
        p.Oscillators[1].Enabled = true; p.Oscillators[1].Table = "Basic"; p.Oscillators[1].Position = 0.66; p.Oscillators[1].Octave = -1; p.Oscillators[1].Level = 0.5;
        p.Filter.Cutoff = 4500; p.Filter.Resonance = 0.2; p.Filter.EnvAmount = 1.5;
        p.AmpEnvelope = new() { Attack = 0.01, Decay = 0.4, Sustain = 0.8, Release = 0.35 };
        p.Fx.ChorusMix = 0.3; p.Fx.ReverbMix = 0.25; p.Fx.DelayMix = 0.2; p.Fx.DelayTime = 0.375;
    });

    private static SynthPatch Pluck() => Base("pluck", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.55; o.Unison = 3; o.Detune = 12;
        p.Filter.Cutoff = 300; p.Filter.Resonance = 0.35; p.Filter.EnvAmount = 5; p.Filter.KeyTrack = 0.8;
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.25, Sustain = 0, Release = 0.2 };
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.5, Sustain = 0, Release = 0.3 };
        p.Hold = 0.6;
        p.Fx.DelayMix = 0.25; p.Fx.DelayTime = 0.1875; p.Fx.DelayFeedback = 0.4; p.Fx.ReverbMix = 0.2;
    });

    private static SynthPatch WarmPad() => Base("warmpad", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Harmonics"; o.Position = 0.3; o.Unison = 5; o.Detune = 20; o.Spread = 1;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "PWM"; o2.Position = 0.4; o2.Unison = 3; o2.Detune = 15; o2.Level = 0.4;
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.3, Amount = 0.4, Target = ModTarget.Position };
        p.Lfo2 = new() { Shape = LfoShape.Sine, Rate = 0.15, Amount = 0.3, Target = ModTarget.Cutoff };
        p.Filter.Cutoff = 1800; p.Filter.Resonance = 0.1;
        p.AmpEnvelope = new() { Attack = 0.8, Decay = 1, Sustain = 0.8, Release = 1.5 };
        p.Hold = 2.5; p.MaxLength = 8;
        p.Fx.ChorusMix = 0.5; p.Fx.ReverbMix = 0.45; p.Fx.ReverbSize = 0.85;
    });

    private static SynthPatch VowelPad() => Base("vowelpad", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Formant"; o.Unison = 3; o.Detune = 10;
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.4, Amount = 1, Target = ModTarget.Position };
        p.Oscillators[0].Position = 0.5;
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.4, Decay = 0.5, Sustain = 0.9, Release = 1 };
        p.Hold = 2; p.Fx.ReverbMix = 0.4; p.Fx.ChorusMix = 0.3;
    });

    private static SynthPatch SubBass() => Base("subbass", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.15; o.Warp = WarpMode.Fold; o.WarpAmount = 0.1;
        p.Filter.Cutoff = 400; p.Filter.KeyTrack = 1;
        p.AmpEnvelope = new() { Attack = 0.003, Decay = 0.3, Sustain = 0.9, Release = 0.15 };
        p.Notes = "c2 c3"; p.Hold = 0.8;
    });

    private static SynthPatch ReeseBass() => Base("reese", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.66; o.Unison = 2; o.Detune = 30; o.Spread = 0.3;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0.66; o2.Fine = -9; o2.Level = 0.8;
        p.Filter.Cutoff = 700; p.Filter.Resonance = 0.3; p.Filter.Drive = 0.4;
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 0.5, Amount = 0.25, Target = ModTarget.Cutoff };
        p.AmpEnvelope = new() { Attack = 0.01, Decay = 0.3, Sustain = 1, Release = 0.2 };
        p.Notes = "c2 c3"; p.Hold = 1.5; p.Fx.Drive = 0.2;
    });

    private static SynthPatch WobbleBass() => Base("wobble", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.8; o.Unison = 3; o.Detune = 18; o.Warp = WarpMode.Bend; o.WarpAmount = 0.3;
        p.Filter.Cutoff = 250; p.Filter.Resonance = 0.55; p.Filter.Drive = 0.5;
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 4, Amount = 0.9, Target = ModTarget.Cutoff };
        p.AmpEnvelope = new() { Attack = 0.005, Decay = 0.2, Sustain = 1, Release = 0.15 };
        p.Notes = "c2 c3"; p.Hold = 1; p.Fx.Drive = 0.35;
    });

    private static SynthPatch SyncLead() => Base("synclead", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0.66; o.Warp = WarpMode.Sync; o.WarpAmount = 0.2; o.Unison = 3; o.Detune = 10;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.5, Sustain = 0.1, Release = 0.2 }, Target = ModTarget.Warp, Amount = 0.6 };
        p.Filter.Cutoff = 6000; p.Filter.Resonance = 0.15;
        p.Lfo1 = new() { Shape = LfoShape.Sine, Rate = 5.5, Amount = 0.015, Target = ModTarget.Pitch, FadeIn = 0.4 };
        p.Fx.DelayMix = 0.2; p.Fx.ReverbMix = 0.2;
    });

    private static SynthPatch GlassBell() => Base("bell", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Digital"; o.Position = 0.35;
        var o2 = p.Oscillators[1];
        o2.Enabled = true; o2.Table = "Basic"; o2.Position = 0; o2.Octave = 1; o2.Level = 0.4;
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 1.5, Sustain = 0, Release = 1.2 };
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 1, Sustain = 0, Release = 0.5 }, Target = ModTarget.Position, Amount = 0.4 };
        p.Hold = 1.5; p.Fx.ReverbMix = 0.35;
    });

    private static SynthPatch DigitalKeys() => Base("dkeys", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Digital"; o.Position = 0.6; o.Warp = WarpMode.Crush; o.WarpAmount = 0.35;
        p.Filter.Cutoff = 3000; p.Filter.EnvAmount = 2;
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.8, Sustain = 0.3, Release = 0.4 };
        p.Fx.ChorusMix = 0.35; p.Fx.ReverbMix = 0.2;
    });

    private static SynthPatch Kick() => Base("mykick", p =>
    {
        var o = p.Oscillators[0];
        o.Position = 0; o.Octave = -2;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.12, Sustain = 0, Release = 0.1 }, Target = ModTarget.Pitch, Amount = 1 };
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.4, Sustain = 0, Release = 0.1 };
        p.Fx.Drive = 0.25;
        p.Notes = "c5"; p.Hold = 0.5; p.MaxLength = 1;
    });

    private static SynthPatch Hat() => Base("myhat", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Noise"; o.Position = 0; o.Octave = 2;
        p.Filter.Mode = FilterMode.HighPass; p.Filter.Cutoff = 7000; p.Filter.Resonance = 0.2; p.Filter.KeyTrack = 0;
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.06, Sustain = 0, Release = 0.04 };
        p.Notes = "c5"; p.Hold = 0.1; p.MaxLength = 0.5;
    });
}
