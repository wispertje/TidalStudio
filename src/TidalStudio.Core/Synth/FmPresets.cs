namespace TidalStudio.Core.Synth;

/// <summary>Factory presets for Tidepool FM. Sound names start with "fm" so they never clash with other sounds.</summary>
public static class FmPresets
{
    public static readonly IReadOnlyList<(string Category, IReadOnlyList<(string Name, Func<FmPatch> Create)> Presets)> Groups =
        new (string, IReadOnlyList<(string, Func<FmPatch>)>)[]
        {
            ("Keys & mallets", new (string, Func<FmPatch>)[]
            {
                ("Init", () => new FmPatch()),
                ("Tine piano", TinePiano),
                ("Glass keys", GlassKeys),
                ("Marimba", Marimba),
                ("Tubular bell", TubularBell),
                ("Funky clav", FunkyClav),
                ("Drawbar organ", DrawbarOrgan),
            }),
            ("Bass", new (string, Func<FmPatch>)[]
            {
                ("Solid bass", SolidBass),
                ("Rubber bass", RubberBass),
                ("Slap bass", SlapBass),
                ("Metal bass", MetalBass),
            }),
            ("Brass & leads", new (string, Func<FmPatch>)[]
            {
                ("Stab brass", StabBrass),
                ("Digital lead", DigitalLead),
                ("Breathy flute", BreathyFlute),
            }),
            ("Pads & bells", new (string, Func<FmPatch>)[]
            {
                ("Crystal pad", CrystalPad),
                ("Gamelan", Gamelan),
            }),
            ("Percussion", new (string, Func<FmPatch>)[]
            {
                ("FM kick", Kick),
                ("Metal hit", MetalHit),
                ("Wood block", WoodBlock),
            }),
            ("Synth pop", new (string, Func<FmPatch>)[]
            {
                ("Pop EP chords", PopEp),
                ("Pop FM bass", PopBass),
                ("Pop brass stab", PopBrass),
                ("Pop bell lead", PopBellLead),
                ("Pop mallet arp", PopMalletArp),
            }),
            ("Synthwave", new (string, Func<FmPatch>)[]
            {
                ("Chorus EP (ballad)", WaveEp),
                ("Sunset bells", WaveBells),
                ("FM sax", Sax),
                ("Night bass", WaveBass),
                ("Tape glass pad", WavePad),
            }),
        };

    public static readonly IReadOnlyList<(string Name, Func<FmPatch> Create)> All = Groups.SelectMany(g => g.Presets).ToList();

    public static FmPatch Create(string name) =>
        (All.FirstOrDefault(p => p.Name == name).Create ?? (() => new FmPatch()))();

    // ------------------------------------------------------------------ helpers

    private static FmOperator Op(double ratio, double level, double a, double d, double s, double r, double detune = 0, double keyScale = 0) =>
        new FmOperator
        {
            Level = level, Detune = detune, KeyScale = keyScale,
            Envelope = new EnvelopeSettings { Attack = a, Decay = d, Sustain = s, Release = r },
        }.WithRatio(ratio);

    private static FmOperator FixedOp(double hz, double level, double a, double d, double s, double r)
    {
        var op = Op(1, level, a, d, s, r);
        op.Fixed = true;
        op.FixedHz = hz;
        return op;
    }

    private static FmOperator Off => new() { Enabled = false, Level = 0.5 };

    /// <param name="algorithm">1-based, as shown in the window.</param>
    private static FmPatch P(string name, int algorithm, double feedback, FmOperator[] ops, Action<FmPatch>? setup = null)
    {
        var p = new FmPatch { Name = name, Algorithm = algorithm - 1, Feedback = feedback, Operators = ops.ToList() };
        setup?.Invoke(p);
        p.Repair();
        return p;
    }

    // ------------------------------------------------------------------ keys & mallets

    private static FmPatch TinePiano() => P("fmepiano", 3, 0.15, new[]
    {
        Op(1, 0.9, 0.001, 2.8, 0, 0.5),
        Op(14, 0.28, 0.001, 0.18, 0, 0.2, keyScale: 0.6), // the metallic "tine" at the start
        Op(1, 0.85, 0.001, 3.2, 0, 0.5, detune: 4),
        Op(1, 0.42, 0.001, 1.4, 0.12, 0.4, keyScale: 0.4),
        Op(1, 0.5, 0.001, 2.2, 0, 0.5, detune: -4),
        Op(1, 0.3, 0.001, 0.9, 0, 0.3),
    }, p =>
    {
        p.KeyRate = 0.35;
        p.Notes = "c3 c4 c5 c6";
        p.Hold = 1.5;
        p.MaxLength = 5;
        p.Fx.ChorusMix = 0.4;
        p.Fx.ReverbMix = 0.15;
    });

    private static FmPatch GlassKeys() => P("fmglass", 3, 0, new[]
    {
        Op(1, 0.9, 0.001, 2.5, 0.1, 0.6),
        Op(1, 0.45, 0.001, 1.2, 0.2, 0.5),
        Op(2, 0.4, 0.001, 1.5, 0, 0.5, detune: 3),
        Op(7, 0.25, 0.001, 0.4, 0, 0.3, keyScale: 0.5),
        Op(1, 0.6, 0.001, 3, 0, 0.6, detune: -3),
        Op(3, 0.3, 0.001, 0.8, 0, 0.4),
    }, p =>
    {
        p.KeyRate = 0.3;
        p.Notes = "c4 c5 c6";
        p.Hold = 1.2;
        p.Fx.ChorusMix = 0.3;
        p.Fx.ReverbMix = 0.25;
    });

    private static FmPatch Marimba() => P("fmmarimba", 3, 0, new[]
    {
        Op(1, 0.9, 0.001, 0.55, 0, 0.2),
        Op(4, 0.35, 0.001, 0.06, 0, 0.05),
        Op(4, 0.25, 0.001, 0.15, 0, 0.1),
        Op(1, 0.2, 0.001, 0.05, 0, 0.05),
        Off,
        Off,
    }, p =>
    {
        p.KeyRate = 0.5;
        p.Notes = "c4 c5 c6";
        p.Hold = 0.4;
        p.MaxLength = 2;
        p.Fx.ReverbMix = 0.12;
    });

    private static FmPatch TubularBell() => P("fmbell", 3, 0, new[]
    {
        Op(1, 0.9, 0.001, 5, 0, 3),
        Op(3.5, 0.45, 0.001, 4, 0, 3),
        Op(1, 0.6, 0.001, 4, 0, 3, detune: 6),
        Op(3.5, 0.4, 0.001, 2.5, 0, 2),
        Op(2, 0.35, 0.001, 1.5, 0, 1.5),
        Op(5.19, 0.3, 0.001, 0.5, 0, 0.5),
    }, p =>
    {
        p.Notes = "c4 c5";
        p.Hold = 0.5;
        p.MaxLength = 8;
        p.Fx.ReverbMix = 0.3;
    });

    private static FmPatch FunkyClav() => P("fmclav", 5, 0.4, new[]
    {
        Op(1, 0.9, 0.001, 0.6, 0.35, 0.06),
        Op(3, 0.5, 0.001, 0.35, 0.3, 0.06),
        Op(1, 0.35, 0.001, 0.1, 0, 0.05),
        Op(2, 0.5, 0.001, 0.4, 0.2, 0.05),
        Op(5, 0.3, 0.001, 0.2, 0.1, 0.05),
        Op(1, 0.4, 0.001, 0.3, 0.2, 0.05),
    }, p =>
    {
        p.Notes = "c4 c5";
        p.Hold = 0.3;
        p.MaxLength = 2;
        p.Fx.Drive = 0.1;
    });

    private static FmPatch DrawbarOrgan() => P("fmorgan", 9, 0, new[]
    {
        Op(0.5, 0.8, 0.005, 0.1, 1, 0.08),
        Op(1, 0.9, 0.005, 0.1, 1, 0.08),
        Op(2, 0.6, 0.005, 0.1, 1, 0.08),
        Op(3, 0.45, 0.005, 0.1, 1, 0.08),
        Op(4, 0.4, 0.005, 0.1, 1, 0.08),
        Op(8, 0.3, 0.005, 0.1, 1, 0.08),
    }, p =>
    {
        p.Tremolo = 0.15;
        p.LfoRate = 6;
        p.LfoDelay = 0;
        p.Notes = "c3 c4 c5";
        p.Hold = 1.5;
        p.Fx.ChorusMix = 0.5;
    });

    // ------------------------------------------------------------------ bass

    private static FmPatch SolidBass() => P("fmsolid", 5, 0.55, new[]
    {
        Op(1, 1, 0.001, 1.2, 0.6, 0.08),
        Op(1, 0.6, 0.001, 0.3, 0.25, 0.08),
        Op(1, 0.3, 0.001, 0.05, 0, 0.05), // click
        Op(0.5, 0.6, 0.001, 1, 0.6, 0.08),
        Op(0.5, 0.35, 0.001, 0.4, 0.2, 0.08),
        Op(0.5, 0.3, 0.001, 0.6, 0.3, 0.08),
    }, p =>
    {
        p.Notes = "c2 c3";
        p.Hold = 0.5;
        p.MaxLength = 2;
    });

    private static FmPatch RubberBass() => P("fmrubber", 3, 0, new[]
    {
        Op(1, 1, 0.001, 0.8, 0.4, 0.1),
        Op(1, 0.5, 0.001, 0.15, 0.08, 0.1),
        Op(0.5, 0.7, 0.001, 0.9, 0.5, 0.1),
        Op(0.5, 0.45, 0.001, 0.12, 0, 0.1),
        Off,
        Off,
    }, p =>
    {
        p.PitchEnvAmount = 2;
        p.PitchEnvDecay = 0.05;
        p.Notes = "c2 c3";
        p.Hold = 0.5;
        p.MaxLength = 2;
    });

    private static FmPatch SlapBass() => P("fmslap", 5, 0.3, new[]
    {
        Op(1, 1, 0.001, 0.9, 0.3, 0.1),
        Op(3, 0.55, 0.001, 0.06, 0, 0.05), // the slap
        Op(1, 0.45, 0.001, 0.25, 0.15, 0.1),
        Op(2, 0.35, 0.001, 0.15, 0, 0.05),
        Op(7, 0.4, 0.001, 0.04, 0, 0.03),
        Op(1, 0.3, 0.001, 0.1, 0, 0.05),
    }, p =>
    {
        p.Notes = "c2 c3";
        p.Hold = 0.4;
        p.MaxLength = 2;
    });

    private static FmPatch MetalBass() => P("fmmetal", 1, 0.4, new[]
    {
        Op(1, 1, 0.001, 1, 0.7, 0.1),
        Op(1, 0.4, 0.001, 0.5, 0.3, 0.1),
        Op(2, 0.3, 0.001, 0.4, 0.2, 0.1),
        Op(1, 0.3, 0.001, 0.3, 0.2, 0.1),
        Op(3, 0.25, 0.001, 0.2, 0.1, 0.1),
        Op(1, 0.3, 0.001, 0.3, 0.2, 0.1),
    }, p =>
    {
        p.Notes = "c2 c3";
        p.Hold = 0.5;
        p.MaxLength = 2;
        p.Fx.Drive = 0.2;
    });

    // ------------------------------------------------------------------ brass & leads

    private static FmPatch StabBrass() => P("fmbrass", 2, 0.45, new[]
    {
        Op(1, 0.9, 0.03, 0.4, 0.8, 0.15),
        Op(1, 0.5, 0.06, 0.5, 0.55, 0.15),
        Op(1, 0.3, 0.05, 0.3, 0.3, 0.15),
        Op(1, 0.85, 0.03, 0.4, 0.8, 0.15, detune: 7),
        Op(1, 0.5, 0.06, 0.5, 0.55, 0.15, detune: 7),
        Op(1, 0.35, 0.05, 0.4, 0.4, 0.15),
    }, p =>
    {
        p.PitchEnvAmount = -1;
        p.PitchEnvDecay = 0.06;
        p.Vibrato = 12;
        p.LfoRate = 5.5;
        p.LfoDelay = 0.4;
        p.Notes = "c4 c5";
        p.Hold = 0.6;
        p.Fx.ChorusMix = 0.3;
        p.Fx.ReverbMix = 0.15;
    });

    private static FmPatch DigitalLead() => P("fmlead", 4, 0.6, new[]
    {
        Op(1, 0.9, 0.005, 0.5, 0.8, 0.25),
        Op(2, 0.45, 0.005, 0.8, 0.5, 0.25),
        Op(1, 0.6, 0.005, 0.5, 0.8, 0.25, detune: 5),
        Op(1, 0.35, 0.005, 0.6, 0.5, 0.25),
        Op(3, 0.25, 0.005, 0.4, 0.3, 0.25),
        Op(1, 0.35, 0.005, 0.5, 0.4, 0.25),
    }, p =>
    {
        p.Vibrato = 20;
        p.LfoDelay = 0.3;
        p.Hold = 0.8;
        p.Fx.DelayMix = 0.25;
        p.Fx.DelayTime = 0.375;
    });

    private static FmPatch BreathyFlute() => P("fmflute", 3, 1, new[]
    {
        Op(1, 0.9, 0.06, 0.3, 0.85, 0.2),
        Op(1, 0.2, 0.05, 0.3, 0.15, 0.2),
        Op(2, 0.15, 0.06, 0.3, 0.6, 0.2),
        Off,
        Op(1, 0.3, 0.01, 0.15, 0.2, 0.1),
        Op(1, 0.8, 0.001, 0.2, 0.4, 0.1), // full feedback: breath noise
    }, p =>
    {
        p.Vibrato = 15;
        p.Notes = "c5 c6";
        p.Hold = 1;
        p.Fx.ReverbMix = 0.3;
    });

    // ------------------------------------------------------------------ pads & bells

    private static FmPatch CrystalPad() => P("fmpad", 3, 0.1, new[]
    {
        Op(1, 0.8, 0.6, 2, 0.8, 1.5),
        Op(1, 0.35, 0.8, 2, 0.5, 1.5),
        Op(1, 0.7, 0.7, 2, 0.8, 1.5, detune: 6),
        Op(3, 0.2, 1.2, 2, 0.6, 1.5),
        Op(2, 0.4, 0.9, 2, 0.7, 1.5, detune: -6),
        Op(7, 0.18, 0.5, 1.5, 0.4, 1.5),
    }, p =>
    {
        p.Notes = "c4 c5";
        p.Hold = 2;
        p.MaxLength = 7;
        p.Fx.ChorusMix = 0.5;
        p.Fx.ReverbMix = 0.4;
        p.Fx.ReverbSize = 0.85;
    });

    private static FmPatch Gamelan() => P("fmgamelan", 2, 0, new[]
    {
        Op(1, 0.9, 0.001, 3, 0, 2),
        Op(2.76, 0.4, 0.001, 2.5, 0, 2),
        Op(1.41, 0.3, 0.001, 1.5, 0, 1.5),
        Op(2, 0.5, 0.001, 2, 0, 1.5, detune: 8),
        Op(5.4, 0.3, 0.001, 1, 0, 1),
        Op(1, 0.2, 0.001, 0.5, 0, 0.5),
    }, p =>
    {
        p.Notes = "c4 c5";
        p.Hold = 0.4;
        p.Fx.ReverbMix = 0.3;
    });

    // ------------------------------------------------------------------ percussion

    private static FmPatch Kick() => P("fmkick", 3, 0, new[]
    {
        Op(1, 1, 0.001, 0.45, 0, 0.1),
        Op(1, 0.35, 0.001, 0.04, 0, 0.03),
        Off, Off, Off, Off,
    }, p =>
    {
        p.PitchEnvAmount = 24;
        p.PitchEnvDecay = 0.08;
        p.Notes = "c2";
        p.Hold = 0.3;
        p.MaxLength = 1;
    });

    private static FmPatch MetalHit() => P("fmhit", 3, 0, new[]
    {
        FixedOp(587, 0.9, 0.001, 0.9, 0, 0.4),
        FixedOp(1173, 0.6, 0.001, 0.5, 0, 0.3),
        FixedOp(821, 0.7, 0.001, 0.6, 0, 0.3),
        FixedOp(2210, 0.5, 0.001, 0.3, 0, 0.2),
        Off, Off,
    }, p =>
    {
        p.Hold = 0.2;
        p.MaxLength = 2;
        p.Fx.ReverbMix = 0.1;
    });

    private static FmPatch WoodBlock() => P("fmwood", 3, 0, new[]
    {
        Op(1, 1, 0.001, 0.08, 0, 0.04),
        Op(2.5, 0.4, 0.001, 0.02, 0, 0.02),
        Off, Off, Off, Off,
    }, p =>
    {
        p.PitchEnvAmount = 3;
        p.PitchEnvDecay = 0.02;
        p.KeyRate = 0.5;
        p.Notes = "c5 c6";
        p.Hold = 0.1;
        p.MaxLength = 0.6;
    });

    // ------------------------------------------------------------------ synth pop

    private static FmPatch PopEp()
    {
        var p = TinePiano();
        p.Name = "fmpopep";
        p.Notes = "c4 c5";
        p.Fx.ChorusMix = 0.55;
        p.Fx.DelayMix = 0.15;
        p.Fx.DelayTime = 0.375;
        p.Fx.ReverbMix = 0.25;
        return p;
    }

    // ------------------------------------------------------------------ synthwave

    /// <summary>The tine piano drenched in a slow, deep chorus with a touch of tape: the 80s ballad electric piano.</summary>
    private static FmPatch WaveEp()
    {
        var p = TinePiano();
        p.Name = "fmwaveep";
        p.Fx.ChorusMix = 0.65; p.Fx.ChorusRate = 0.7; p.Fx.ChorusDepth = 5.5;
        p.Fx.DelayMix = 0.12; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.3;
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.85; p.Fx.Tape = 0.15;
        return p;
    }

    /// <summary>Bright bells with long tails, echo and a big room: sparkle over pads.</summary>
    private static FmPatch WaveBells() => P("fmwavebell", 3, 0, new[]
    {
        Op(1, 0.85, 0.001, 4, 0, 3),
        Op(3.5, 0.5, 0.001, 2.5, 0, 2),
        Op(2, 0.55, 0.001, 3, 0, 2.5, detune: 5),
        Op(5.19, 0.3, 0.001, 1.2, 0, 1),
        Op(1, 0.4, 0.001, 3.5, 0, 3, detune: -5),
        Op(14, 0.12, 0.001, 0.3, 0, 0.3),
    }, p =>
    {
        p.Notes = "c4 c5 c6";
        p.Hold = 0.5;
        p.MaxLength = 8;
        p.Fx.ChorusMix = 0.35;
        p.Fx.DelayMix = 0.25; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.4;
        p.Fx.ReverbMix = 0.5; p.Fx.ReverbSize = 0.92;
    });

    /// <summary>
    /// A reed: the carrier's brightness swells in with the attack, a 3:1 modulator adds the nasal edge, and operator 6's
    /// feedback (noise) breathes through a quiet second carrier. Notes scoop up into pitch and the vibrato comes in late.
    /// </summary>
    private static FmPatch Sax() => P("fmsax", 5, 0.9, new[]
    {
        Op(1, 0.9, 0.04, 0.3, 0.85, 0.2),
        Op(1, 0.55, 0.08, 0.4, 0.45, 0.2, keyScale: 0.3),
        Op(3, 0.18, 0.05, 0.3, 0.15, 0.2),
        Op(1, 0.14, 0.03, 0.2, 0.3, 0.1),
        Op(1, 0.3, 0.01, 0.2, 0.3, 0.1),
        Op(1, 0.8, 0.001, 0.2, 0.4, 0.1), // full feedback: breath noise
    }, p =>
    {
        p.PitchEnvAmount = -0.6;
        p.PitchEnvDecay = 0.08;
        p.Vibrato = 14;
        p.LfoRate = 5.2;
        p.LfoDelay = 0.45;
        p.Notes = "c3 c4 c5";
        p.Hold = 1.2;
        p.MaxLength = 4;
        p.Fx.DelayMix = 0.2; p.Fx.DelayTime = 0.375; p.Fx.DelayFeedback = 0.35;
        p.Fx.ReverbMix = 0.35; p.Fx.ReverbSize = 0.85;
    });

    /// <summary>The solid FM bass, shorter, with a light chorus: driving eighth-note synthwave basslines.</summary>
    private static FmPatch WaveBass()
    {
        var p = SolidBass();
        p.Name = "fmwavebass";
        p.Notes = "c1 c2 c3";
        p.Hold = 0.25;
        foreach (var op in p.Operators) op.Envelope.Release = 0.06;
        p.Fx.ChorusMix = 0.3; p.Fx.ChorusRate = 0.7; p.Fx.ChorusDepth = 3;
        return p;
    }

    /// <summary>The crystal pad, slower and wider, worn by tape: a shimmering bed under chords.</summary>
    private static FmPatch WavePad()
    {
        var p = CrystalPad();
        p.Name = "fmwavepad";
        p.Notes = "c3 c4 c5";
        p.Hold = 2.5;
        p.MaxLength = 9;
        p.Fx.ChorusMix = 0.65; p.Fx.ChorusRate = 0.4; p.Fx.ChorusDepth = 7;
        p.Fx.DelayMix = 0.15; p.Fx.DelayTime = 0.5; p.Fx.DelayFeedback = 0.35;
        p.Fx.ReverbMix = 0.5; p.Fx.ReverbSize = 0.92; p.Fx.Tape = 0.3;
        return p;
    }

    private static FmPatch PopBass()
    {
        var p = SolidBass();
        p.Name = "fmpopbass";
        p.Hold = 0.3;
        foreach (var op in p.Operators) op.Envelope.Release = 0.05;
        p.Fx.Drive = 0.1;
        return p;
    }

    private static FmPatch PopBrass()
    {
        var p = StabBrass();
        p.Name = "fmpopbrass";
        p.Hold = 0.25;
        p.Brightness = 1.2;
        p.Vibrato = 0;
        p.Fx.ReverbMix = 0.2;
        return p;
    }

    private static FmPatch PopBellLead() => P("fmpopbell", 3, 0.1, new[]
    {
        Op(1, 0.9, 0.002, 1.5, 0.3, 0.6),
        Op(1, 0.4, 0.002, 1, 0.2, 0.6),
        Op(1, 0.5, 0.002, 1.2, 0, 0.6, detune: 5),
        Op(3.5, 0.35, 0.002, 0.8, 0, 0.5),
        Off,
        Off,
    }, p =>
    {
        p.Vibrato = 10;
        p.LfoDelay = 0.5;
        p.Notes = "c5 c6";
        p.Hold = 0.6;
        p.Fx.DelayMix = 0.3;
        p.Fx.DelayTime = 0.375;
        p.Fx.DelayFeedback = 0.4;
        p.Fx.ReverbMix = 0.25;
    });

    private static FmPatch PopMalletArp()
    {
        var p = Marimba();
        p.Name = "fmpopmallet";
        p.Fx.DelayMix = 0.3;
        p.Fx.DelayTime = 0.1875;
        p.Fx.DelayFeedback = 0.4;
        p.Fx.ReverbMix = 0.2;
        p.MaxLength = 3;
        return p;
    }
}
