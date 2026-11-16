using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using TidalStudio.App.Services;
using TidalStudio.Core.Events;
using TidalStudio.Core.Vj;

namespace TidalStudio.App.Vj;

/// <summary>
/// The VJ picture: runs its own frame loop, turns audio and Tidal events into signals and hits, lets the director
/// pick scenes and transitions, and hands each frame to <see cref="VjRenderer"/> through a Skia draw operation.
/// </summary>
internal sealed class VjCanvas : Control
{
    private readonly AppServices _services;
    private readonly VjRenderer _renderer = new();
    private readonly VjParticles _particles = new();
    private readonly double _start;
    private double _lastNow;
    private double _travel;
    private bool _running;
    private VjFrame? _frame;

    public VjCanvas(AppServices services)
    {
        _services = services;
        Options = services.Settings.Vj;
        Options.Repair();
        Director = new VjDirector(Options);
        _start = _lastNow = services.Time.Now;
        ClipToBounds = true;
    }

    public VjOptions Options { get; }
    public VjDirector Director { get; }
    public VjSignals Signals { get; } = new();

    /// <summary>Frames per second, measured.</summary>
    public double Fps { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _running = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _running = false;
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_running) return;
        // At most ~120 frames a second: some setups call back faster than the display refreshes.
        if (_services.Time.Now - _lastNow >= 1 / 120.0)
        {
            Step();
            InvalidateVisual();
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    /// <summary>Advances everything to now and captures the frame to draw.</summary>
    public void Step()
    {
        double now = _services.Time.Now;
        double dt = Math.Clamp(now - _lastNow, 0, 0.25);
        if (dt > 0) Fps += (1 / Math.Max(dt, 1e-3) - Fps) * 0.05;
        double from = Math.Max(_lastNow, now - 0.25);
        _lastNow = now;

        // Events that started since the last frame.
        var source = Options.Source;
        var fresh = _services.Events.Query(from, now + 0.0001).Where(e => e.Time > from && e.Time <= now).ToList();
        var colors = new Dictionary<string, uint>();
        foreach (var e in fresh)
        {
            Signals.OnEvent(e, source);
            if (Options.Particles) _particles.Spawn(e, EventColor(e, colors), Options.Labels, Options.Intensity);
        }
        Signals.Update(dt, _services.AudioCapture.IsRunning ? _services.Audio : null, source);
        _particles.Step(dt);

        var clock = _services.Clock;
        bool playing = clock.IsPlaying(now);
        double cycle = clock.HasReference ? clock.CycleAt(now) : (now - _start) * 0.5;
        Director.Update(now, cycle, playing);
        _travel += dt * (0.25 + 1.4 * Signals.Level * Options.Intensity + 0.6 * Signals.Beat);

        var pal = Director.Colors();
        var f = new VjFrame
        {
            Time = (float)((now - _start) % 3600),
            Cycle = (float)(cycle % 4096),
            Travel = (float)(_travel % 4096),
            Bass = (float)Signals.Bass,
            Mid = (float)Signals.Mid,
            High = (float)Signals.High,
            Level = (float)Signals.Level,
            Beat = (float)Signals.Beat,
            Intensity = (float)Options.Intensity,
            ColA = new[] { (float)pal[0].R, (float)pal[0].G, (float)pal[0].B },
            ColB = new[] { (float)pal[1].R, (float)pal[1].G, (float)pal[1].B },
            ColC = new[] { (float)pal[2].R, (float)pal[2].G, (float)pal[2].B },
            Scene = Director.Scene,
            Next = Director.Next,
            TransitionKind = Director.TransitionKind,
            Progress = (float)Director.Progress,
            Post = Director.Post(Signals),
            Quality = Options.Quality,
            Particles = _particles.Items.ToArray(),
            Spectrum = (float[])Signals.Spectrum.Clone(),
            Wave = (float[])Signals.Wave.Clone(),
            CodeMode = Options.Code,
        };

        // Pattern rain needs the recent events (only when it is on screen).
        if (IsShown("Pattern rain"))
        {
            foreach (var e in _services.Events.Query(now - 3.2, now + 0.0001))
            {
                if (e.Time > now) continue;
                string label = e.Label;
                f.Rain.Add(new RainDrop((float)(now - e.Time), (float)e.Duration, (float)Hash01(e.Sound ?? label), EventColor(e, colors), (float)e.Gain, label));
            }
        }

        if (Options.Code != VjCodeMode.Off)
        {
            var active = _services.Events.Active(now, 0.12);
            f.Code = CodeView.Layout(_services.CodeLog, active, now, Options.Code == VjCodeMode.Panel ? 30 : 16);
            foreach (var line in f.Code)
            {
                if (line.PatternId is { } id) f.PatternColors.TryAdd(id, ParseColor(_services.Settings.PatternColor(id)));
                foreach (var h in line.Highlights) f.PatternColors.TryAdd(h.PatternId, ParseColor(_services.Settings.PatternColor(h.PatternId)));
            }
        }

        if (Options.Hud && now - Director.ChangedAt < 3)
        {
            f.Hud = Director.ChangeLabel;
            double age = now - Director.ChangedAt;
            f.HudAlpha = (float)Math.Clamp(Math.Min(age * 4, (3 - age) / 0.8), 0, 1);
        }
        _frame = f;
    }

    private bool IsShown(string scene)
    {
        int i = VjDirector.Scenes.ToList().IndexOf(scene);
        return Director.Scene == i || Director.Next == i;
    }

    private static double Hash01(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return (h % 1000) / 1000.0;
    }

    private uint EventColor(TidalEvent e, Dictionary<string, uint> cache)
    {
        string key = e.Color ?? "#" + e.PatternId;
        if (cache.TryGetValue(key, out var c)) return c;
        string text = e.Color is { Length: > 0 } custom ? custom : _services.Settings.PatternColor(e.PatternId);
        c = Visualizers.VisualizerBase.TryParseColor(text, out var color) ? color.ToUInt32() : 0xFF4ECDC4;
        cache[key] = c;
        return c;
    }

    private static uint ParseColor(string text) =>
        Visualizers.VisualizerBase.TryParseColor(text, out var c) ? c.ToUInt32() : 0xFF8A93A6;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        _frame ??= BuildFirstFrame();
        context.Custom(new DrawOp(new Rect(Bounds.Size), _renderer, _frame));
    }

    private VjFrame BuildFirstFrame()
    {
        Step();
        return _frame!;
    }

    private sealed class DrawOp(Rect bounds, VjRenderer renderer, VjFrame frame) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            renderer.Render(lease.SkCanvas, lease.GrContext, frame, (float)bounds.Width, (float)bounds.Height);
        }
    }
}
