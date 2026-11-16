using TidalStudio.Core.Events;

namespace TidalStudio.Core.Vj;

public enum VjCodeMode { Off, Panel, Overlay }

/// <summary>Everything the performer can set on the VJ screen (saved in the settings).</summary>
public sealed class VjOptions
{
    public int Scene { get; set; }
    /// <summary>Switch scenes by itself every <see cref="AutoCycles"/> cycles.</summary>
    public bool Auto { get; set; } = true;
    public int AutoCycles { get; set; } = 8;
    /// <summary>Index into <see cref="VjDirector.Transitions"/>, or -1 for a random one each time.</summary>
    public int Transition { get; set; } = -1;
    public double TransitionSeconds { get; set; } = 2;
    public VjSource Source { get; set; } = VjSource.Auto;
    public VjCodeMode Code { get; set; } = VjCodeMode.Panel;
    /// <summary>Video feedback: how much of the previous frame stays (0..0.97).</summary>
    public double Trails { get; set; } = 0.55;
    /// <summary>How hard the music pushes the visuals, 0..2.</summary>
    public double Intensity { get; set; } = 1;
    public bool RgbSplit { get; set; } = true;
    /// <summary>Kaleidoscope segments (0 = off).</summary>
    public int Kaleido { get; set; }
    public bool Mirror { get; set; }
    public bool Pixelate { get; set; }
    public bool Scanlines { get; set; }
    public bool BeatFlash { get; set; } = true;
    public bool Particles { get; set; } = true;
    /// <summary>Sound names pop up with the hits.</summary>
    public bool Labels { get; set; } = true;
    /// <summary>Kaleidoscope / mirror / pixelate change by themselves with the scenes.</summary>
    public bool AutoFx { get; set; } = true;
    public int Palette { get; set; }
    public bool AutoPalette { get; set; } = true;
    /// <summary>0 = 640, 1 = 960, 2 = 1280 px wide, 3 = full resolution.</summary>
    public int Quality { get; set; } = 1;
    /// <summary>Scene and transition names fade in at a change.</summary>
    public bool Hud { get; set; } = true;

    public void Repair()
    {
        Scene = Math.Clamp(Scene, 0, VjDirector.Scenes.Count - 1);
        AutoCycles = Math.Clamp(AutoCycles, 1, 64);
        Transition = Math.Clamp(Transition, -1, VjDirector.Transitions.Count - 1);
        TransitionSeconds = Math.Clamp(TransitionSeconds, 0.1, 10);
        Trails = Math.Clamp(Trails, 0, 0.97);
        Intensity = Math.Clamp(Intensity, 0, 2);
        Kaleido = Math.Clamp(Kaleido, 0, 16);
        Palette = Math.Clamp(Palette, 0, VjPalette.All.Count - 1);
        Quality = Math.Clamp(Quality, 0, 3);
    }
}

/// <summary>Three colours the scenes are tinted with (r, g, b in 0..1).</summary>
public sealed record VjPalette(string Name, (double R, double G, double B) A, (double R, double G, double B) B, (double R, double G, double B) C)
{
    public static readonly IReadOnlyList<VjPalette> All = new[]
    {
        new VjPalette("Neon", (0.05, 0.9, 0.95), (0.95, 0.1, 0.75), (1.0, 0.85, 0.2)),
        new VjPalette("Synthwave", (0.35, 0.05, 0.6), (1.0, 0.2, 0.55), (1.0, 0.75, 0.25)),
        new VjPalette("Acid", (0.1, 0.1, 0.05), (0.6, 1.0, 0.1), (1.0, 1.0, 0.6)),
        new VjPalette("Ice", (0.02, 0.08, 0.3), (0.2, 0.6, 1.0), (0.9, 0.97, 1.0)),
        new VjPalette("Fire", (0.25, 0.0, 0.02), (1.0, 0.35, 0.0), (1.0, 0.92, 0.5)),
        new VjPalette("Tide", (0.0, 0.2, 0.3), (0.3, 0.8, 0.77), (1.0, 0.82, 0.4)),
        new VjPalette("Mono", (0.05, 0.05, 0.06), (0.55, 0.55, 0.6), (1.0, 1.0, 1.0)),
    };

    public static (double, double, double)[] Blend(VjPalette a, VjPalette b, double t)
    {
        static (double, double, double) M((double R, double G, double B) x, (double R, double G, double B) y, double t) =>
            (x.R + (y.R - x.R) * t, x.G + (y.G - x.G) * t, x.B + (y.B - x.B) * t);
        return new[] { M(a.A, b.A, t), M(a.B, b.B, t), M(a.C, b.C, t) };
    }
}

/// <summary>Post-processing for one frame.</summary>
public readonly record struct VjPost(
    double Trails, double Zoom, double Rotate, double RgbShift, int Kaleido, bool Mirror, double Pixel,
    double Invert, bool Scanlines, double Flash, double Vignette);

/// <summary>
/// Decides what is on screen: the current scene, transitions to the next one (by hand or automatically every few
/// cycles), and the post effects driven by the signals.
/// </summary>
public sealed class VjDirector
{
    public static readonly IReadOnlyList<string> Scenes = new[]
    {
        "Liquid", "Tunnel", "Retro grid", "Kaleido bloom", "Plasma", "Warp stars", "Cells", "Spectrum ring", "Pattern rain",
    };

    public static readonly IReadOnlyList<string> Transitions = new[]
    {
        "Crossfade", "Wipe", "Iris", "Dissolve", "Glitch slices", "Zoom", "Luma key", "Flash cut",
    };

    private readonly Random _random;
    private long _lastBlock = long.MinValue;
    private double _lastSwitch = double.NegativeInfinity;
    private double _transitionStart;

    public VjDirector(VjOptions options, int seed = 0)
    {
        Options = options;
        Options.Repair();
        Scene = Options.Scene;
        _random = seed == 0 ? new Random() : new Random(seed);
    }

    public VjOptions Options { get; }
    public int Scene { get; private set; }
    /// <summary>The scene being faded in, or -1.</summary>
    public int Next { get; private set; } = -1;
    public int TransitionKind { get; private set; }
    public double Progress { get; private set; }
    /// <summary>When the last scene change started (for the name overlay).</summary>
    public double ChangedAt { get; private set; } = double.NegativeInfinity;
    public string ChangeLabel { get; private set; } = "";

    /// <summary>Kaleidoscope / mirror / pixelate chosen by Auto FX for the current scene.</summary>
    public int AutoKaleido { get; private set; }
    public bool AutoMirror { get; private set; }
    public bool AutoPixel { get; private set; }

    /// <summary>The palette pair and blend for automatic palettes.</summary>
    public int PaletteFrom { get; private set; }
    public int PaletteTo { get; private set; }
    public double PaletteBlend { get; private set; } = 1;
    private double _paletteStart;

    /// <summary>Start a transition to <paramref name="scene"/> (ignored while one is running or to the same scene).</summary>
    public void Go(int scene, double now, int? kind = null)
    {
        scene = ((scene % Scenes.Count) + Scenes.Count) % Scenes.Count;
        if (Next >= 0 || scene == Scene) return;
        Next = scene;
        TransitionKind = kind ?? (Options.Transition >= 0 ? Options.Transition : _random.Next(Transitions.Count));
        _transitionStart = now;
        Progress = 0;
        _lastSwitch = now;
        ChangedAt = now;
        ChangeLabel = $"{Scenes[scene]}  ·  {Transitions[TransitionKind]}";
        if (Options.AutoFx)
        {
            double r = _random.NextDouble();
            AutoKaleido = r < 0.25 ? new[] { 4, 6, 8 }[_random.Next(3)] : 0;
            AutoMirror = AutoKaleido == 0 && _random.NextDouble() < 0.2;
            AutoPixel = _random.NextDouble() < 0.12;
        }
    }

    /// <summary>Show <paramref name="scene"/> right away, without a transition.</summary>
    public void Jump(int scene)
    {
        Scene = ((scene % Scenes.Count) + Scenes.Count) % Scenes.Count;
        Options.Scene = Scene;
        Next = -1;
        Progress = 0;
    }

    public void GoRandom(double now)
    {
        int next = _random.Next(Scenes.Count - 1);
        Go(next >= Scene ? next + 1 : next, now);
    }

    public void NextPalette(double now)
    {
        PaletteFrom = CurrentPaletteIndex;
        PaletteTo = (PaletteTo + 1) % VjPalette.All.Count;
        Options.Palette = PaletteTo;
        _paletteStart = now;
        PaletteBlend = 0;
    }

    private int CurrentPaletteIndex => PaletteBlend >= 0.5 ? PaletteTo : PaletteFrom;

    /// <summary>The three colours to draw with now.</summary>
    public (double R, double G, double B)[] Colors()
    {
        var all = VjPalette.All;
        return VjPalette.Blend(all[Math.Clamp(PaletteFrom, 0, all.Count - 1)], all[Math.Clamp(PaletteTo, 0, all.Count - 1)], PaletteBlend);
    }

    /// <summary>Advance: finish transitions, and in auto mode switch every few cycles (or every few seconds without Tidal).</summary>
    public void Update(double now, double cycle, bool playing)
    {
        if (PaletteFrom == PaletteTo && PaletteTo != Options.Palette) { PaletteFrom = PaletteTo = Options.Palette; }
        PaletteBlend = Math.Clamp((now - _paletteStart) / 3, 0, 1);

        if (Next >= 0)
        {
            Progress = Math.Clamp((now - _transitionStart) / Options.TransitionSeconds, 0, 1);
            if (Progress >= 1)
            {
                Scene = Next;
                Options.Scene = Scene;
                Next = -1;
                Progress = 0;
            }
            return;
        }
        if (!Options.Auto) return;

        long block = playing ? (long)Math.Floor(cycle / Options.AutoCycles) : long.MinValue;
        if (playing)
        {
            if (_lastBlock == long.MinValue) _lastBlock = block;
            else if (block != _lastBlock)
            {
                _lastBlock = block;
                GoRandom(now);
                if (Options.AutoPalette && _random.NextDouble() < 0.5) NextPalette(now);
            }
        }
        else
        {
            _lastBlock = long.MinValue;
            // Not playing: drift slowly through the scenes (an idle screen before the set starts).
            if (now - _lastSwitch > Options.AutoCycles * 2.5)
            {
                if (double.IsNegativeInfinity(_lastSwitch)) _lastSwitch = now;
                else
                {
                    GoRandom(now);
                    if (Options.AutoPalette) NextPalette(now);
                }
            }
        }
    }

    /// <summary>The post effects for this frame.</summary>
    public VjPost Post(VjSignals s)
    {
        var o = Options;
        double k = o.Intensity;
        int kaleido = o.Kaleido > 0 ? o.Kaleido : o.AutoFx ? AutoKaleido : 0;
        bool mirror = o.Mirror || (o.AutoFx && AutoMirror);
        bool pixel = o.Pixelate || (o.AutoFx && AutoPixel);
        return new VjPost(
            Trails: o.Trails,
            Zoom: 1.006 + 0.03 * s.Bass * k,
            Rotate: 0.004 * (s.Mid - 0.3) * k,
            RgbShift: o.RgbSplit ? (0.002 + 0.014 * Math.Max(s.Kick, s.Beat * 0.6)) * k : 0,
            Kaleido: kaleido,
            Mirror: mirror,
            Pixel: pixel ? 4 + 10 * (1 - s.Hat) : 0,
            Invert: o.BeatFlash && s.Beats % 16 == 0 && s.Beats > 0 ? s.Beat * 0.9 : 0,
            Scanlines: o.Scanlines,
            Flash: o.BeatFlash ? Math.Pow(s.Beat, 3) * 0.18 * k + s.Snare * 0.06 * k : 0,
            Vignette: 0.55);
    }
}

public enum ParticleKind { Ring, Spark, Label }

/// <summary>A hit drawn on screen (positions 0..1 of the frame).</summary>
public struct Particle
{
    public ParticleKind Kind;
    public double X, Y, Vx, Vy, Age, Life, Size;
    public uint Color;
    public string? Text;
}

/// <summary>Hits from Tidal events: rings for kicks and snares, sparks for hats, sound names for notes.</summary>
public sealed class VjParticles
{
    public const int Max = 700;
    private readonly Random _random = new(7);
    public List<Particle> Items { get; } = new();

    private static double Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return (h % 10000) / 10000.0;
    }

    public void Spawn(TidalEvent e, uint color, bool labels, double intensity)
    {
        string sound = e.Sound ?? "?";
        double g = Math.Clamp(e.Gain, 0.2, 1.5) * Math.Clamp(intensity, 0.2, 2);
        double x = Math.Clamp(e.Pan, 0, 1) * 0.6 + 0.2 + (Hash(sound) - 0.5) * 0.25;
        double y = 0.25 + Hash(sound + "y") * 0.5;
        switch (VjSignals.Classify(e.Sound, e.Midi()))
        {
            case HitKind.Kick:
                Add(new Particle { Kind = ParticleKind.Ring, X = 0.5, Y = 0.5, Life = 0.7, Size = 0.35 * g, Color = color });
                break;
            case HitKind.Snare:
                Add(new Particle { Kind = ParticleKind.Ring, X = x, Y = y, Life = 0.5, Size = 0.22 * g, Color = color });
                Burst(x, y, 14, 0.5, color, g);
                break;
            case HitKind.Hat:
                Burst(x, y, 5, 0.25, color, g * 0.6);
                break;
            default:
                Add(new Particle { Kind = ParticleKind.Ring, X = x, Y = y, Life = 0.9, Size = 0.12 * g, Color = color });
                break;
        }
        if (labels && _random.NextDouble() < 0.6)
        {
            string text = e.Note is { } n ? $"{sound} {n:+0;-0;0}" : e.N is { } nn && nn != 0 ? $"{sound}:{nn:0}" : sound;
            Add(new Particle { Kind = ParticleKind.Label, X = x, Y = y, Vy = -0.04, Life = 1.1, Size = 0.035 + 0.02 * g, Color = color, Text = text });
        }
    }

    private void Burst(double x, double y, int count, double speed, uint color, double g)
    {
        for (int i = 0; i < count; i++)
        {
            double a = _random.NextDouble() * Math.PI * 2, v = speed * (0.4 + _random.NextDouble()) * g;
            Add(new Particle { Kind = ParticleKind.Spark, X = x, Y = y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v, Life = 0.4 + _random.NextDouble() * 0.5, Size = 0.004 + 0.004 * g, Color = color });
        }
    }

    private void Add(Particle p)
    {
        if (Items.Count >= Max) Items.RemoveAt(0);
        Items.Add(p);
    }

    public void Step(double dt)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var p = Items[i];
            p.Age += dt;
            if (p.Age >= p.Life) { Items.RemoveAt(i); continue; }
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vx *= Math.Exp(-dt * 2);
            p.Vy = p.Vy * Math.Exp(-dt * 2) + (p.Kind == ParticleKind.Spark ? 0.15 * dt : 0);
            Items[i] = p;
        }
    }
}
