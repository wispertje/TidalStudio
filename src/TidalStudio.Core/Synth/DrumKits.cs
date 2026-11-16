namespace TidalStudio.Core.Synth;

/// <summary>Factory drum kits and the starting point of every drum model.</summary>
public static class DrumKits
{
    public static readonly IReadOnlyList<(string Name, Func<DrumKit> Create)> All = new (string, Func<DrumKit>)[]
    {
        ("Analog boom", AnalogBoom),
        ("Punchy machine", PunchyMachine),
        ("Hard techno", HardTechno),
        ("Acid house", AcidHouse),
        ("Lo-fi dusty", LofiDusty),
        ("Electro zaps", ElectroZaps),
        ("Acid techno · warehouse", AcidWarehouse),
        ("Acid techno · rumble", AcidRumble),
        ("Acid techno · 303 era", AcidEra),
        ("Synth pop · gated 80s", GatedEighties),
        ("Synth pop · electro-pop", ElectroPop),
        ("Synth pop · minimal wave", MinimalWave),
    };

    public static DrumKit Create(string name) =>
        (All.FirstOrDefault(k => k.Name == name).Create ?? AnalogBoom)();

    /// <summary>Drum names and what they are, for tooltips.</summary>
    public static readonly IReadOnlyDictionary<string, string> Abbreviations = new Dictionary<string, string>
    {
        ["bd"] = "bass drum (kick)", ["sd"] = "snare drum", ["cp"] = "clap", ["rim"] = "rim shot",
        ["hh"] = "closed hi-hat", ["oh"] = "open hi-hat", ["cr"] = "crash cymbal", ["rd"] = "ride cymbal",
        ["lt"] = "low tom", ["mt"] = "mid tom", ["ht"] = "high tom", ["cb"] = "cowbell",
        ["sh"] = "shaker", ["perc"] = "percussion", ["fx"] = "effect", ["rb"] = "rumble (long, low kick tail)",
    };

    private static DrumEnvelope Env(double decay, double curve = 0.85, double attack = 0.0005, double hold = 0) =>
        new() { Attack = attack, Hold = hold, Decay = decay, Curve = curve };

    /// <summary>The layers a drum model starts from.</summary>
    public static DrumVoice Default(DrumModel model, string abbreviation)
    {
        var v = new DrumVoice { Abbreviation = abbreviation, Model = model };
        v.Osc1.Enabled = false;
        switch (model)
        {
            case DrumModel.Kick:
                v.Osc1 = new() { Frequency = 50, PitchDrop = 24, PitchTime = 0.06, PitchCurve = 0.85, Envelope = Env(0.55, 0.8, hold: 0.02) };
                v.Click = new() { Enabled = true, Level = 0.3, Length = 0.004, Tone = 3000 };
                v.Drive = 0.15;
                break;
            case DrumModel.Snare:
                v.Osc1 = new() { Frequency = 185, PitchDrop = 5, PitchTime = 0.03, Envelope = Env(0.15) };
                v.Osc2 = new() { Enabled = true, Frequency = 1.62, Level = 0.55, Envelope = Env(0.12) };
                v.Noise = new() { Enabled = true, Filter = FilterMode.HighPass, Cutoff = 1800, Level = 0.7, Envelope = Env(0.22, 0.7) };
                break;
            case DrumModel.Tom:
                v.Osc1 = new() { Frequency = 110, PitchDrop = 7, PitchTime = 0.15, PitchCurve = 0.6, Envelope = Env(0.4) };
                v.Noise = new() { Enabled = true, Filter = FilterMode.LowPass, Cutoff = 3000, Level = 0.15, Envelope = Env(0.05) };
                break;
            case DrumModel.Clap:
                v.Noise = new() { Enabled = true, Filter = FilterMode.BandPass, Cutoff = 1200, Resonance = 0.45, Level = 1, Bursts = 4, BurstSpacing = 0.0105, Envelope = Env(0.18, 0.9) };
                v.Room = 0.1;
                break;
            case DrumModel.ClosedHat:
            case DrumModel.OpenHat:
                double decay = model == DrumModel.ClosedHat ? 0.06 : 0.45;
                v.Metal = new() { Enabled = true, Cutoff = 9000, Resonance = 0.3, Level = 0.9, Envelope = Env(decay, 0.9) };
                v.Noise = new() { Enabled = true, Filter = FilterMode.HighPass, Cutoff = 7000, Level = 0.25, Envelope = Env(decay * 0.8, 0.9) };
                v.Level = 0.75;
                break;
            case DrumModel.Cymbal:
                v.Metal = new() { Enabled = true, Tune = -3, Cutoff = 6000, Resonance = 0.25, Level = 0.8, Envelope = Env(1.6, 0.9, attack: 0.002) };
                v.Noise = new() { Enabled = true, Filter = FilterMode.HighPass, Cutoff = 5000, Level = 0.4, Envelope = Env(1.4, 0.9, attack: 0.002) };
                v.Level = 0.7;
                break;
            case DrumModel.Rim:
                v.Osc1 = new() { Position = 1 / 3.0, Frequency = 1700, Envelope = Env(0.035, 0.9, attack: 0) };
                v.Filter = new() { Mode = FilterMode.BandPass, Cutoff = 1700, Resonance = 0.5 };
                v.Click = new() { Enabled = true, Level = 0.3, Length = 0.002, Tone = 4000 };
                break;
            case DrumModel.Cowbell:
                v.Metal = new() { Enabled = true, Voices = 2, Cutoff = 1400, Resonance = 0.55, Level = 0.9, Envelope = Env(0.3, 0.9) };
                v.Level = 0.75;
                break;
            case DrumModel.Zap:
                v.Osc1 = new() { Frequency = 160, PitchDrop = 40, PitchTime = 0.08, PitchCurve = 0.9, Envelope = Env(0.25) };
                break;
            case DrumModel.Shaker:
                v.Noise = new() { Enabled = true, Filter = FilterMode.HighPass, Cutoff = 6000, Level = 0.9, Envelope = Env(0.09, 0.6, attack: 0.012) };
                v.Level = 0.7;
                break;
        }
        return v;
    }

    /// <summary>A random variation on a voice, staying close to what its model is.</summary>
    public static void Randomise(DrumVoice v, Random r)
    {
        double J(double amount) => (r.NextDouble() * 2 - 1) * amount;
        double Oct(double amount) => Math.Pow(2, J(amount));
        v.Tune = Math.Clamp(v.Tune + J(4), -24, 24);
        foreach (var o in new[] { v.Osc1, v.Osc2 })
        {
            o.Envelope.Decay = Math.Clamp(o.Envelope.Decay * Oct(0.8), 0.01, 4);
            o.PitchDrop = Math.Clamp(o.PitchDrop * Oct(0.6), 0, 60);
            o.PitchTime = Math.Clamp(o.PitchTime * Oct(0.8), 0.003, 1);
            if (r.NextDouble() < 0.3) o.Position = r.NextDouble() * 0.7;
        }
        if (v.Osc2.Enabled) v.Osc2.Fm = Math.Clamp(v.Osc2.Fm + J(0.3), 0, 1);
        v.Noise.Cutoff = Math.Clamp(v.Noise.Cutoff * Oct(1), 100, 16000);
        v.Noise.Envelope.Decay = Math.Clamp(v.Noise.Envelope.Decay * Oct(0.8), 0.01, 4);
        if (r.NextDouble() < 0.3) v.Noise.Color = (NoiseColor)r.Next(3);
        v.Metal.Cutoff = Math.Clamp(v.Metal.Cutoff * Oct(0.6), 500, 16000);
        v.Metal.Envelope.Decay = Math.Clamp(v.Metal.Envelope.Decay * Oct(0.8), 0.01, 4);
        v.Metal.Tune = Math.Clamp(v.Metal.Tune + J(5), -24, 24);
        v.Click.Level = Math.Clamp(v.Click.Level + J(0.2), 0, 1);
        v.Drive = Math.Clamp(v.Drive + J(0.25), 0, 1);
        if (r.NextDouble() < 0.25) v.DriveMode = (DriveMode)r.Next(4);
        v.Room = Math.Clamp(v.Room + J(0.15), 0, 0.6);
    }

    /// <summary>The standard set of drums, ready to tweak per kit.</summary>
    private static DrumKit Standard(string name, Action<Dictionary<string, DrumVoice>> tweak)
    {
        var voices = new List<DrumVoice>
        {
            Default(DrumModel.Kick, "bd"),
            Default(DrumModel.Snare, "sd"),
            Default(DrumModel.Clap, "cp"),
            Default(DrumModel.Rim, "rim"),
            Default(DrumModel.ClosedHat, "hh"),
            Default(DrumModel.OpenHat, "oh"),
            Default(DrumModel.Cymbal, "cr"),
            Default(DrumModel.Cymbal, "rd"),
            Default(DrumModel.Tom, "lt"),
            Default(DrumModel.Tom, "mt"),
            Default(DrumModel.Tom, "ht"),
            Default(DrumModel.Cowbell, "cb"),
            Default(DrumModel.Shaker, "sh"),
            Default(DrumModel.Zap, "perc"),
        };
        var d = voices.ToDictionary(v => v.Abbreviation);
        // Ride: shorter and more ping than the crash. Toms: three pitches.
        d["rd"].Metal.Envelope.Decay = 1.1; d["rd"].Metal.Cutoff = 8000; d["rd"].Noise.Level = 0.15; d["rd"].Tune = 5;
        d["lt"].Tune = -4; d["mt"].Tune = 1; d["ht"].Tune = 6;
        // Hats and shakers sound more natural with a few versions to alternate between.
        foreach (var key in new[] { "hh", "oh", "sh" }) d[key].Variations = 4;
        tweak(d);
        return new DrumKit { Name = name, Voices = voices };
    }

    private static DrumKit AnalogBoom() => Standard("boom", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 1.2; bd.Osc1.PitchDrop = 16; bd.Osc1.PitchTime = 0.08; bd.Click.Level = 0.1; bd.Drive = 0.05;
        d["sd"].Tune = -1; d["sd"].Noise.Level = 0.55; d["sd"].Noise.Cutoff = 1400;
        d["cp"].Room = 0.25;
        d["cb"].Metal.Envelope.Decay = 0.4;
    });

    private static DrumKit PunchyMachine() => Standard("punch", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.45; bd.Osc1.PitchDrop = 30; bd.Osc1.PitchTime = 0.04; bd.Click.Level = 0.55; bd.Drive = 0.3;
        var sd = d["sd"];
        sd.Tune = 2; sd.Noise.Level = 0.85; sd.Noise.Cutoff = 2500; sd.Noise.Envelope.Decay = 0.25;
        sd.Click = new() { Enabled = true, Level = 0.2, Length = 0.003, Tone = 4000 };
        d["cp"].Noise.Cutoff = 1500; d["cp"].Room = 0.15;
        foreach (var key in new[] { "hh", "oh" }) { d[key].Metal.Cutoff = 10500; d[key].Noise.Level = 0.5; }
        d["cr"].Noise.Level = 0.7;
    });

    private static DrumKit HardTechno() => Standard("hard", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.7; bd.Osc1.PitchDrop = 28; bd.Osc1.PitchTime = 0.045; bd.Click.Level = 0.6;
        bd.Drive = 0.75; bd.Room = 0.12; bd.Tune = -1;
        bd.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 3000, Resonance = 0.2, EnvAmount = 2, EnvDecay = 0.08 };
        var sd = d["sd"]; sd.Noise.Level = 0.8; sd.Drive = 0.4; sd.Room = 0.2;
        var cp = d["cp"]; cp.Drive = 0.5; cp.Room = 0.3; cp.Noise.Envelope.Decay = 0.25;
        d["hh"].Metal.Envelope.Decay = 0.04; d["hh"].Metal.Cutoff = 11000; d["hh"].Drive = 0.2;
        d["oh"].Metal.Envelope.Decay = 0.3; d["oh"].Metal.Cutoff = 11000;
        d["rd"].Metal.Envelope.Decay = 1.4; d["rd"].Metal.Cutoff = 10000;
        d["perc"].Drive = 0.4; d["perc"].Room = 0.3;
    });

    private static DrumKit AcidHouse() => Standard("acidhouse", d =>
    {
        var bd = d["bd"]; bd.Osc1.Envelope.Decay = 0.5; bd.Osc1.PitchDrop = 26; bd.Click.Level = 0.45; bd.Drive = 0.2;
        d["cp"].Room = 0.35; d["cp"].Noise.Cutoff = 1300;
        d["oh"].Metal.Envelope.Decay = 0.3;
        d["cb"].Tune = 2;
        d["sh"].Noise.Envelope.Decay = 0.12;
    });

    private static DrumKit LofiDusty() => Standard("dusty", d =>
    {
        foreach (var v in d.Values)
        {
            v.DriveMode = DriveMode.Crush;
            v.Drive = Math.Max(v.Drive, 0.35);
            v.Room = Math.Max(v.Room, 0.12);
            v.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 6000, Resonance = 0.05 };
            v.Noise.Color = NoiseColor.Pink;
            v.Variations = Math.Max(v.Variations, 2);
        }
        var bd = d["bd"]; bd.Osc1.Envelope.Decay = 0.5; bd.Osc1.PitchDrop = 14; bd.Click.Level = 0.05;
        var sd = d["sd"]; sd.Tune = -3; sd.Noise.Envelope.Decay = 0.18;
    });

    private static DrumKit ElectroZaps() => Standard("electro", d =>
    {
        var bd = d["bd"]; bd.Osc1.Envelope.Decay = 0.9; bd.Osc1.PitchDrop = 36; bd.Osc1.PitchTime = 0.1;
        var sd = d["sd"]; sd.Osc1.PitchDrop = 14; sd.Osc1.PitchTime = 0.06; sd.Noise.Level = 0.5;
        sd.Osc2.Fm = 0.3;
        foreach (var key in new[] { "lt", "mt", "ht" }) { d[key].Osc1.PitchDrop = 24; d[key].Osc1.PitchTime = 0.18; d[key].Osc1.Envelope.Decay = 0.3; }
        var perc = d["perc"];
        perc.Osc1.PitchDrop = 48; perc.Osc1.PitchTime = 0.14; perc.Osc1.Envelope.Decay = 0.3; perc.Room = 0.2; perc.Variations = 4; perc.Spread = 0.6;
        perc.Osc2 = new() { Enabled = true, Frequency = 2.01, Level = 0, Fm = 0.35, Envelope = Env(0.2) };
        d["cb"].Tune = 7;
    });

    // ------------------------------------------------------------------ acid techno

    /// <summary>Driven, slightly roomy kick, big reverb clap, offbeat open hats and a long ride: four-to-the-floor acid.</summary>
    private static DrumKit AcidWarehouse() => Standard("acidwh", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.6; bd.Osc1.PitchDrop = 28; bd.Osc1.PitchTime = 0.045; bd.Click.Level = 0.5;
        bd.Drive = 0.6; bd.Room = 0.12; bd.RoomSize = 0.6;
        bd.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 4000, Resonance = 0.15, EnvAmount = 1.5, EnvDecay = 0.06 };
        var cp = d["cp"]; cp.Noise.Cutoff = 1400; cp.Room = 0.35; cp.RoomSize = 0.7; cp.Drive = 0.25; cp.Variations = 2;
        var sd = d["sd"]; sd.Noise.Level = 0.8; sd.Noise.Cutoff = 2200; sd.Room = 0.2; sd.Drive = 0.3;
        d["hh"].Metal.Envelope.Decay = 0.035; d["hh"].Metal.Cutoff = 10500; d["hh"].Spread = 0.45;
        d["oh"].Metal.Envelope.Decay = 0.25; d["oh"].Metal.Cutoff = 10000; d["oh"].Noise.Level = 0.35;
        d["rd"].Metal.Envelope.Decay = 1.2; d["rd"].Metal.Cutoff = 9500; d["rd"].Noise.Level = 0.2;
        d["rim"].Room = 0.25; d["rim"].Variations = 2;
        d["cb"].Tune = 2; d["cb"].Room = 0.2;
        var perc = d["perc"]; perc.Osc1.PitchDrop = 30; perc.Drive = 0.4; perc.DriveMode = DriveMode.Fold; perc.Room = 0.3; perc.Variations = 3; perc.Spread = 0.5;
    });

    /// <summary>Hard techno-style acid: distorted kick plus a separate rumble ("rb": a long, low, reverberant kick tail).</summary>
    private static DrumKit AcidRumble()
    {
        var kit = Standard("acidrumble", d =>
        {
            var bd = d["bd"];
            bd.Osc1.Envelope.Decay = 0.5; bd.Osc1.PitchDrop = 32; bd.Osc1.PitchTime = 0.035; bd.Click.Level = 0.65;
            bd.Drive = 0.8; bd.DriveMode = DriveMode.Hard; bd.Tune = -1;
            bd.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 3500, Resonance = 0.2, EnvAmount = 2, EnvDecay = 0.05 };
            var cp = d["cp"]; cp.Drive = 0.5; cp.Room = 0.4; cp.RoomSize = 0.75; cp.Noise.Envelope.Decay = 0.22;
            d["hh"].Metal.Envelope.Decay = 0.03; d["hh"].Metal.Cutoff = 11500; d["hh"].Drive = 0.3;
            d["oh"].Metal.Envelope.Decay = 0.22; d["oh"].Metal.Cutoff = 11000; d["oh"].Drive = 0.2;
            d["rd"].Metal.Envelope.Decay = 1.0; d["rd"].Metal.Cutoff = 10500;
            foreach (var key in new[] { "lt", "mt", "ht" }) { d[key].Drive = 0.5; d[key].Room = 0.25; }
            d["perc"].Drive = 0.6; d["perc"].DriveMode = DriveMode.Crush; d["perc"].Room = 0.3;
        });
        // The rumble: play it on the offbeats under the kick (s "~ acidrumblerb" or with # orbit and a sidechain).
        var rb = Default(DrumModel.Kick, "rb");
        rb.Osc1.Frequency = 45; rb.Osc1.PitchDrop = 12; rb.Osc1.PitchTime = 0.08;
        rb.Osc1.Envelope = new() { Attack = 0.002, Hold = 0.05, Decay = 1.2, Curve = 0.7 };
        rb.Click.Enabled = false;
        rb.Drive = 0.85; rb.DriveMode = DriveMode.Fold;
        rb.Filter = new() { Mode = FilterMode.LowPass, Cutoff = 260, Resonance = 0.3 };
        rb.Room = 0.6; rb.RoomSize = 0.85;
        rb.Level = 0.8; rb.MaxLength = 2.5; rb.Variations = 2; rb.Spread = 0.3;
        kit.Voices.Insert(1, rb);
        return kit;
    }

    /// <summary>Lighter, snappier late-80s acid: tight kick with a click, bright snare, cowbell and clave-like rim.</summary>
    private static DrumKit AcidEra() => Standard("acid88", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.42; bd.Osc1.PitchDrop = 30; bd.Osc1.PitchTime = 0.035; bd.Click.Level = 0.55; bd.Drive = 0.2;
        var sd = d["sd"];
        sd.Tune = 1; sd.Noise.Level = 0.85; sd.Noise.Cutoff = 2500; sd.Noise.Envelope.Decay = 0.24;
        sd.Click = new() { Enabled = true, Level = 0.25, Length = 0.003, Tone = 4000 };
        d["cp"].Noise.Cutoff = 1500; d["cp"].Room = 0.2;
        foreach (var key in new[] { "hh", "oh" }) { d[key].Metal.Cutoff = 10500; d[key].Noise.Level = 0.5; d[key].Spread = 0.4; }
        d["oh"].Metal.Envelope.Decay = 0.35;
        var rim = d["rim"]; rim.Osc1.Frequency = 2500; rim.Filter.Cutoff = 2500; rim.Osc1.Envelope.Decay = 0.05; // clave-like
        d["cb"].Tune = 0; d["cb"].Metal.Envelope.Decay = 0.35;
        d["sh"].Noise.Envelope.Decay = 0.1;
    });

    // ------------------------------------------------------------------ synth pop

    /// <summary>
    /// Big 80s production: a huge snare and clap with gated reverb (a big room cut off short by Max len),
    /// pitch-swept electronic toms and a round kick.
    /// </summary>
    private static DrumKit GatedEighties() => Standard("gated", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.5; bd.Osc1.PitchDrop = 20; bd.Osc1.PitchTime = 0.05; bd.Click.Level = 0.35; bd.Drive = 0.15;
        bd.Room = 0.15; bd.RoomSize = 0.5;
        var sd = d["sd"];
        sd.Tune = -2; sd.Noise.Level = 0.85; sd.Noise.Cutoff = 1500; sd.Noise.Envelope.Decay = 0.3;
        sd.Click = new() { Enabled = true, Level = 0.25, Length = 0.003, Tone = 3500 };
        sd.Room = 0.75; sd.RoomSize = 0.9; sd.MaxLength = 0.38; sd.Drive = 0.2; sd.Variations = 2; // the gate
        var cp = d["cp"]; cp.Room = 0.7; cp.RoomSize = 0.9; cp.MaxLength = 0.35; cp.Noise.Cutoff = 1300;
        foreach (var (key, tune) in new[] { ("lt", -5.0), ("mt", 0.0), ("ht", 5.0) })
        {
            var t = d[key];
            t.Tune = tune;
            t.Osc1.PitchDrop = 12; t.Osc1.PitchTime = 0.25; t.Osc1.PitchCurve = 0.5; t.Osc1.Envelope.Decay = 0.6;
            t.Noise.Level = 0.3; t.Noise.Envelope.Decay = 0.1;
            t.Room = 0.55; t.RoomSize = 0.85; t.MaxLength = 0.7;
        }
        d["hh"].Metal.Cutoff = 10000; d["hh"].Metal.Envelope.Decay = 0.05;
        d["oh"].Metal.Envelope.Decay = 0.35;
        d["cr"].Metal.Envelope.Decay = 2; d["cr"].Room = 0.3;
        var perc = d["perc"]; perc.Osc1.PitchDrop = 24; perc.Osc1.PitchTime = 0.2; perc.Room = 0.4; // syndrum
    });

    /// <summary>Tight, dry and clicky: a short punchy kick, snappy snare with an FM ring, bright claps and a tambourine-like shaker.</summary>
    private static DrumKit ElectroPop() => Standard("epop", d =>
    {
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.32; bd.Osc1.PitchDrop = 30; bd.Osc1.PitchTime = 0.03; bd.Click.Level = 0.6; bd.Drive = 0.25;
        var sd = d["sd"];
        sd.Tune = 3; sd.Osc2.Fm = 0.25; sd.Noise.Level = 0.75; sd.Noise.Cutoff = 3000; sd.Noise.Envelope.Decay = 0.17;
        sd.Click = new() { Enabled = true, Level = 0.3, Length = 0.002, Tone = 5000 };
        sd.Room = 0.12;
        var cp = d["cp"]; cp.Noise.Cutoff = 1700; cp.Noise.Envelope.Decay = 0.14; cp.Room = 0.12; cp.Variations = 2;
        foreach (var key in new[] { "hh", "oh" }) { d[key].Metal.Cutoff = 11000; d[key].Noise.Level = 0.45; }
        d["hh"].Metal.Envelope.Decay = 0.035;
        d["oh"].Metal.Envelope.Decay = 0.22;
        // Shaker → tambourine: jingly metal over the noise.
        var sh = d["sh"];
        sh.Metal = new() { Enabled = true, Tune = 12, Cutoff = 9000, Resonance = 0.35, Level = 0.5, Envelope = Env(0.12, 0.9, attack: 0.004) };
        sh.Noise.Envelope.Decay = 0.12;
        d["cb"].Tune = 5;
        foreach (var key in new[] { "lt", "mt", "ht" }) { d[key].Osc1.Envelope.Decay = 0.28; d[key].Osc1.PitchDrop = 9; }
    });

    /// <summary>Early, home-studio synth pop: soft analog kick, pink-noise snare, ticking hats and chirpy electronic toms.</summary>
    private static DrumKit MinimalWave() => Standard("minwave", d =>
    {
        foreach (var v in d.Values) v.Noise.Color = NoiseColor.Pink;
        var bd = d["bd"];
        bd.Osc1.Envelope.Decay = 0.7; bd.Osc1.PitchDrop = 12; bd.Osc1.PitchTime = 0.07; bd.Click.Level = 0.1; bd.Drive = 0.05;
        var sd = d["sd"];
        sd.Osc1.Envelope.Decay = 0.1; sd.Osc2.Enabled = false; sd.Noise.Level = 0.9; sd.Noise.Filter = FilterMode.BandPass;
        sd.Noise.Cutoff = 2200; sd.Noise.Resonance = 0.2; sd.Noise.Envelope.Decay = 0.2; sd.Room = 0.1;
        foreach (var key in new[] { "hh", "oh" })
        {
            d[key].Metal.Enabled = false;
            d[key].Noise.Color = NoiseColor.White; d[key].Noise.Level = 0.9; d[key].Noise.Cutoff = 8000; d[key].Noise.Resonance = 0.3;
        }
        d["hh"].Noise.Envelope.Decay = 0.03;
        d["oh"].Noise.Envelope.Decay = 0.25;
        foreach (var (key, tune) in new[] { ("lt", -3.0), ("mt", 2.0), ("ht", 7.0) })
        {
            var t = d[key];
            t.Tune = tune;
            t.Osc1.Position = 1 / 3.0; t.Osc1.PitchDrop = 18; t.Osc1.PitchTime = 0.3; t.Osc1.PitchCurve = 0.3; t.Osc1.Envelope.Decay = 0.35;
            t.Noise.Level = 0.05;
            t.Room = 0.2;
        }
        d["cb"].Tune = -2; d["cb"].Metal.Envelope.Decay = 0.2;
        d["rim"].Room = 0.15;
        foreach (var v in d.Values) v.Room = Math.Max(v.Room, 0.08);
    });
}