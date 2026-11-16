namespace TidalStudio.Core.Synth;

/// <summary>
/// Acid techno: a single saw or square into a very resonant, driven low-pass with a short filter envelope.
/// They are rendered with a short hold, so they work for 16th-note lines; use # legato 1 or # cut 1 when playing them.
/// </summary>
internal static class AcidPresets
{
    public static readonly IReadOnlyList<(string Name, Func<SynthPatch> Create)> All = new (string, Func<SynthPatch>)[]
    {
        ("Acid saw (303 style)", AcidSaw),
        ("Acid square", AcidSquare),
        ("Acid accent (screamer)", AcidAccent),
        ("Acid squelch (long)", AcidSquelch),
        ("Distorted acid", DistortedAcid),
        ("Acid sweep (for chop)", AcidSweep),
        ("Acid lead (dub delay)", AcidLead),
        ("Techno stab (minor)", TechnoStab),
        ("Hypnotic bleep", HypnoticBleep),
        ("Acid rubber (slow squelch)", AcidRubber),
        ("Acid sub (square + sub)", AcidSub),
        ("Acid sync scream", AcidSync),
        ("Acid fifths stab", AcidFifths),
        ("Acid ringing (self-osc)", AcidRinging),
    };

    /// <summary>The shared acid voice: mono oscillator, resonant low-pass, plucky filter envelope.</summary>
    private static SynthPatch Acid(string name, double position, Action<SynthPatch> tweak) => SynthPresets.Base(name, p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Basic"; o.Position = position; o.Level = 0.9;
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 300, Resonance = 0.86, Drive = 0.35, EnvAmount = 4.5, KeyTrack = 0.35 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.22, Sustain = 0, Release = 0.08 };
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.5, Sustain = 0.75, Release = 0.06 };
        p.Fx.Drive = 0.25;
        p.Notes = "c2 c3";
        p.Hold = 0.3;
        p.MaxLength = 1.5;
        tweak(p);
    });

    private static SynthPatch AcidSaw() => Acid("acidsaw", 2 / 3.0, _ => { });

    private static SynthPatch AcidSquare() => Acid("acidsquare", 1, p =>
    {
        p.Filter.Cutoff = 240; p.Filter.EnvAmount = 4; p.Filter.Resonance = 0.82;
    });

    private static SynthPatch AcidAccent() => Acid("acidaccent", 2 / 3.0, p =>
    {
        p.Filter.Resonance = 0.93; p.Filter.EnvAmount = 6; p.Filter.Drive = 0.6;
        p.FilterEnvelope.Decay = 0.13;
        p.AmpEnvelope.Sustain = 0.9;
        p.Fx.Drive = 0.5;
    });

    private static SynthPatch AcidSquelch() => Acid("acidsquelch", 2 / 3.0, p =>
    {
        p.Filter.Cutoff = 200; p.Filter.Resonance = 0.9; p.Filter.EnvAmount = 5;
        p.FilterEnvelope = new() { Attack = 0.02, Decay = 0.6, Sustain = 0.1, Release = 0.2 };
        p.Hold = 0.6;
        p.Fx.DelayMix = 0.2; p.Fx.DelayTime = 0.345; p.Fx.DelayFeedback = 0.35;
        p.MaxLength = 2.5;
    });

    private static SynthPatch DistortedAcid() => Acid("aciddist", 2 / 3.0, p =>
    {
        p.Oscillators[0].Warp = WarpMode.Fold; p.Oscillators[0].WarpAmount = 0.18;
        p.Filter.Cutoff = 380; p.Filter.Resonance = 0.85; p.Filter.Drive = 0.8;
        p.Fx.Drive = 0.7;
    });

    /// <summary>One long note with the cutoff slowly opening and closing: slice it with chop / striate.</summary>
    private static SynthPatch AcidSweep() => Acid("acidsweep", 2 / 3.0, p =>
    {
        p.Filter.Cutoff = 180; p.Filter.EnvAmount = 2.5;
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.12, Sustain = 0, Release = 0.1 };
        p.Lfo1 = new() { Shape = LfoShape.Triangle, Rate = 0.5, Amount = 0.75, Target = ModTarget.Cutoff };
        p.Lfo2 = new() { Shape = LfoShape.Square, Rate = 8.67, Amount = 0.15, Target = ModTarget.Cutoff }; // 16ths at ~130 bpm
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.2, Sustain = 1, Release = 0.1 };
        p.Notes = "c2";
        p.Hold = 4;
        p.MaxLength = 4.5;
    });

    private static SynthPatch AcidLead() => Acid("acidlead", 2 / 3.0, p =>
    {
        var o = p.Oscillators[0];
        o.Unison = 2; o.Detune = 8; o.Spread = 0.5;
        p.Filter.Cutoff = 500; p.Filter.Resonance = 0.75; p.Filter.EnvAmount = 3.5;
        p.Notes = "c4 c5";
        p.Hold = 0.25;
        p.Fx.DelayMix = 0.35; p.Fx.DelayTime = 0.345; p.Fx.DelayFeedback = 0.5;
        p.Fx.ReverbMix = 0.15;
        p.MaxLength = 4;
    });

    /// <summary>A minor chord (root, minor third, fifth) in one sample: play it on the off-beats.</summary>
    private static SynthPatch TechnoStab() => SynthPresets.Base("technostab", p =>
    {
        int[] semis = { 0, 3, 7 };
        for (int i = 0; i < 3; i++)
        {
            var o = p.Oscillators[i];
            o.Enabled = true; o.Table = "Basic"; o.Position = 2 / 3.0; o.Semi = semis[i]; o.Level = 0.6;
            o.Unison = 2; o.Detune = 10; o.Spread = 0.7;
        }
        p.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 550, Resonance = 0.3, Drive = 0.2, EnvAmount = 3, KeyTrack = 0.5 };
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.18, Sustain = 0, Release = 0.1 };
        p.AmpEnvelope = new() { Attack = 0.002, Decay = 0.3, Sustain = 0, Release = 0.1 };
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.75;
        p.Fx.DelayMix = 0.3; p.Fx.DelayTime = 0.345; p.Fx.DelayFeedback = 0.45;
        p.Notes = "c4";
        p.Hold = 0.2;
        p.MaxLength = 4;
    });

    private static SynthPatch HypnoticBleep() => SynthPresets.Base("bleep", p =>
    {
        var o = p.Oscillators[0];
        o.Table = "Basic"; o.Position = 0.1; // nearly a sine
        o.Warp = WarpMode.Fold; o.WarpAmount = 0.25;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.12, Sustain = 0, Release = 0.05 }, Target = ModTarget.Warp, Amount = 0.5 };
        p.Filter.Mode = FilterMode.Off;
        p.AmpEnvelope = new() { Attack = 0.001, Decay = 0.15, Sustain = 0, Release = 0.05 };
        p.Fx.DelayMix = 0.3; p.Fx.DelayTime = 0.23; p.Fx.DelayFeedback = 0.55;
        p.Fx.ReverbMix = 0.2;
        p.Notes = "c5 c6";
        p.Hold = 0.12;
        p.MaxLength = 3;
    });

    /// <summary>A rounder, slower squelch: the filter takes longer to close, so each note "talks".</summary>
    private static SynthPatch AcidRubber() => Acid("acidrubber", 2 / 3.0, p =>
    {
        p.Filter.Cutoff = 220; p.Filter.Resonance = 0.8; p.Filter.EnvAmount = 5;
        p.FilterEnvelope = new() { Attack = 0.01, Decay = 0.42, Sustain = 0.05, Release = 0.12 };
        p.Hold = 0.4;
        p.Fx.Drive = 0.3;
    });

    /// <summary>Square acid line with a sine an octave below: fuller low end for big systems.</summary>
    private static SynthPatch AcidSub() => Acid("acidsub", 1, p =>
    {
        var sub = p.Oscillators[1];
        sub.Enabled = true; sub.Table = "Basic"; sub.Position = 0; sub.Octave = -1; sub.Level = 0.55;
        p.Filter.Cutoff = 260; p.Filter.Resonance = 0.84; p.Filter.EnvAmount = 4.2;
        p.Notes = "c1 c2 c3";
    });

    /// <summary>Hard-sync saw whose sync amount follows the filter envelope: a harsh, screaming acid.</summary>
    private static SynthPatch AcidSync() => Acid("acidsync", 2 / 3.0, p =>
    {
        var o = p.Oscillators[0];
        o.Warp = WarpMode.Sync; o.WarpAmount = 0.1;
        p.ModEnvelope = new() { Envelope = new() { Attack = 0, Decay = 0.2, Sustain = 0, Release = 0.05 }, Target = ModTarget.Warp, Amount = 0.45 };
        p.Filter.Cutoff = 420; p.Filter.Resonance = 0.82; p.Filter.Drive = 0.55;
        p.Fx.Drive = 0.45;
        p.Notes = "c2 c3 c4";
    });

    /// <summary>Root + fifth + octave through the acid filter: a rave-era chord stab that still squelches.</summary>
    private static SynthPatch AcidFifths() => Acid("acidfifths", 2 / 3.0, p =>
    {
        int[] semis = { 0, 7, 12 };
        for (int i = 0; i < 3; i++)
        {
            var o = p.Oscillators[i];
            o.Enabled = true; o.Table = "Basic"; o.Position = 2 / 3.0; o.Semi = semis[i]; o.Level = 0.6; o.Fine = i * 4 - 4;
        }
        p.Filter.Cutoff = 380; p.Filter.Resonance = 0.78; p.Filter.EnvAmount = 4;
        p.FilterEnvelope.Decay = 0.25;
        p.Fx.DelayMix = 0.25; p.Fx.DelayTime = 0.345; p.Fx.DelayFeedback = 0.4; p.Fx.ReverbMix = 0.2;
        p.Notes = "c3 c4";
        p.MaxLength = 3;
    });

    /// <summary>Resonance near self-oscillation and a long filter envelope: the filter rings like a sine on every note.</summary>
    private static SynthPatch AcidRinging() => Acid("acidring", 2 / 3.0, p =>
    {
        p.Filter.Cutoff = 300; p.Filter.Resonance = 0.97; p.Filter.EnvAmount = 3.5; p.Filter.Drive = 0.2;
        p.FilterEnvelope = new() { Attack = 0, Decay = 0.35, Sustain = 0.15, Release = 0.1 };
        p.Fx.Drive = 0.35; p.Fx.DelayMix = 0.15; p.Fx.DelayTime = 0.23;
        p.MaxLength = 2.5;
    });
}