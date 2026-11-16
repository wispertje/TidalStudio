using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TidalStudio.App.Services;
using TidalStudio.Core.Vj;

namespace TidalStudio.App.Vj;

/// <summary>
/// The VJ screen: a full-screen visual show for a performance, driven by the music (audio and Tidal events), with
/// scenes, transitions, effects and the code that is running. Move the mouse for the controls; F11 for full screen.
/// </summary>
public sealed class VjWindow : Window
{
    private readonly AppServices _services;
    private readonly VjCanvas _canvas;
    private readonly Border _bar;
    private readonly TextBlock _hint;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _uiTimer;
    private readonly List<ToggleButton> _sceneButtons = new();
    private readonly MeterView _meter;
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#AAB2C3")), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Action> _sync = new();
    private bool _startedCapture;
    private bool _pointerOverBar;

    public VjWindow(AppServices services)
    {
        _services = services;
        _canvas = new VjCanvas(services);
        _meter = new MeterView(_canvas.Signals) { Width = 92, Height = 26, VerticalAlignment = VerticalAlignment.Center };

        Title = "TidalStudio — VJ screen";
        Width = 1280;
        Height = 760;
        MinWidth = 480;
        MinHeight = 300;
        Background = Brushes.Black;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TidalStudio/Assets/TidalStudio.ico"))); }
        catch (Exception) { }

        _bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 14, 16, 22)),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2F3A")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 6),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = BuildControls(),
        };
        _bar.PointerEntered += (_, _) => _pointerOverBar = true;
        _bar.PointerExited += (_, _) => _pointerOverBar = false;
        _hint = new TextBlock
        {
            Text = "Move the mouse for the controls · F11 full screen · Space next scene · 1–9 scenes · C code · H hide · ? all keys",
            Foreground = new SolidColorBrush(Color.FromArgb(170, 220, 225, 235)),
            FontSize = 12,
            Margin = new Thickness(14, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_hint);
        root.Children.Add(_bar);
        Content = root;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideTimer.Tick += (_, _) =>
        {
            if (_pointerOverBar) return;
            _hideTimer.Stop();
            ShowControls(false);
        };
        _hideTimer.Start();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => UpdateStatus();
        _uiTimer.Start();

        PointerMoved += (_, _) => { ShowControls(true); _hideTimer.Stop(); _hideTimer.Start(); };
        KeyDown += OnKeyDown;
        Opened += (_, _) => StartAudio();
        Closed += (_, _) =>
        {
            _uiTimer.Stop();
            _hideTimer.Stop();
            if (_startedCapture && !_services.Settings.CaptureAudio) _services.AudioCapture.Stop();
        };
    }

    /// <summary>For the self-test: the canvas, to step and screenshot.</summary>
    internal VjCanvas Canvas => _canvas;

    private void StartAudio()
    {
        if (_canvas.Options.Source == VjSource.Events || _services.AudioCapture.IsRunning) return;
        _services.AudioCapture.Start();
        _startedCapture = _services.AudioCapture.IsRunning;
    }

    internal void ShowControls(bool show)
    {
        _bar.IsVisible = show;
        _hint.IsVisible = show;
        Cursor = show ? Cursor.Default : new Cursor(StandardCursorType.None);
    }

    // ------------------------------------------------------------------ controls

    private Control BuildControls()
    {
        var o = _canvas.Options;
        var d = _canvas.Director;
        double now() => _services.Time.Now;

        // Row 1: scenes.
        var scenes = new WrapPanel { Orientation = Orientation.Horizontal };
        scenes.Children.Add(Label("SCENE"));
        for (int i = 0; i < VjDirector.Scenes.Count; i++)
        {
            int index = i;
            var b = new ToggleButton { Content = $"{i + 1} {VjDirector.Scenes[i]}", FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
            b.Click += (_, _) => d.Go(index, now());
            _sceneButtons.Add(b);
            scenes.Children.Add(b);
        }
        var next = Button("Next ▶", "Go to a random other scene (Space)", () => d.GoRandom(now()));
        scenes.Children.Add(next);
        _sync.Add(() =>
        {
            for (int i = 0; i < _sceneButtons.Count; i++) _sceneButtons[i].IsChecked = d.Next >= 0 ? i == d.Next : i == d.Scene;
        });

        // Row 2: show control.
        var row2 = new WrapPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(Check("Auto", "Change scene by itself every few cycles (A)", () => o.Auto, v => o.Auto = v));
        row2.Children.Add(Combo("every", new[] { 1, 2, 4, 8, 16, 32 }.Select(n => $"{n} cycles").ToList(),
            () => new[] { 1, 2, 4, 8, 16, 32 }.ToList().IndexOf(o.AutoCycles), i => o.AutoCycles = new[] { 1, 2, 4, 8, 16, 32 }[Math.Max(0, i)]));
        var transitions = new List<string> { "Random" };
        transitions.AddRange(VjDirector.Transitions);
        row2.Children.Add(Combo("Transition", transitions, () => o.Transition + 1, i => o.Transition = i - 1));
        row2.Children.Add(Slider("Time", 0.2, 6, () => o.TransitionSeconds, v => o.TransitionSeconds = v, v => $"{v:0.0}s"));
        row2.Children.Add(Combo("Colours", VjPalette.All.Select(p => p.Name).ToList(), () => o.Palette, i => { o.Palette = i; d.NextPalette(now()); }));
        row2.Children.Add(Check("Auto colours", "Change palette with the scenes", () => o.AutoPalette, v => o.AutoPalette = v));
        row2.Children.Add(Combo("Code", new List<string> { "Off", "Panel", "Overlay" }, () => (int)o.Code, i => o.Code = (VjCodeMode)i,
            "The code that is running, with the sounding mini-notation lit up (C)"));
        row2.Children.Add(Combo("Driven by", new List<string> { "Audio + events", "Audio only", "Tidal events only" }, () => (int)o.Source,
            i => { o.Source = (VjSource)i; StartAudio(); },
            "Audio: what Windows plays (bass, beats). Events: every Tidal event as a hit, works without audio capture."));

        // Row 3: effects.
        var row3 = new WrapPanel { Orientation = Orientation.Horizontal };
        row3.Children.Add(Label("FX"));
        row3.Children.Add(Slider("Trails", 0, 0.97, () => o.Trails, v => o.Trails = v, v => $"{v * 100:0}%"));
        row3.Children.Add(Slider("Intensity", 0, 2, () => o.Intensity, v => o.Intensity = v, v => $"{v * 100:0}%"));
        row3.Children.Add(Toggle("RGB split", "R", () => o.RgbSplit, v => o.RgbSplit = v));
        row3.Children.Add(Combo("Kaleido", new List<string> { "off", "4", "6", "8", "12" }, () => new[] { 0, 4, 6, 8, 12 }.ToList().IndexOf(o.Kaleido),
            i => o.Kaleido = new[] { 0, 4, 6, 8, 12 }[Math.Max(0, i)], "Kaleidoscope segments (K)"));
        row3.Children.Add(Toggle("Mirror", "M", () => o.Mirror, v => o.Mirror = v));
        row3.Children.Add(Toggle("Pixelate", "P", () => o.Pixelate, v => o.Pixelate = v));
        row3.Children.Add(Toggle("Scanlines", "S", () => o.Scanlines, v => o.Scanlines = v));
        row3.Children.Add(Toggle("Beat flash", "B", () => o.BeatFlash, v => o.BeatFlash = v));
        row3.Children.Add(Toggle("Hits", "X", () => o.Particles, v => o.Particles = v));
        row3.Children.Add(Toggle("Names", "N", () => o.Labels, v => o.Labels = v));
        row3.Children.Add(Toggle("Auto FX", "", () => o.AutoFx, v => o.AutoFx = v));
        row3.Children.Add(Combo("Quality", new List<string> { "Low", "Medium", "High", "Full" }, () => o.Quality, i => o.Quality = i,
            "Resolution the effects are rendered at (lower = faster)"));
        var full = Button("⛶ Full screen", "F11 / F · Esc to leave", ToggleFullScreen);
        row3.Children.Add(full);
        row3.Children.Add(_meter);
        row3.Children.Add(_status);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(scenes);
        stack.Children.Add(row2);
        stack.Children.Add(row3);
        Sync();
        return stack;
    }

    private void Sync()
    {
        foreach (var s in _sync) s();
    }

    private void UpdateStatus()
    {
        Sync();
        var capture = _services.AudioCapture;
        string audio = _canvas.Options.Source == VjSource.Events ? "events only"
            : !capture.IsRunning ? "audio off" + (capture.Error is { } e ? $" ({e})" : "")
            : _canvas.Signals.AudioActive ? "audio ✓" : "audio: silence";
        bool playing = _services.Clock.IsPlaying(_services.Time.Now);
        _status.Text = $"{audio} · {(playing ? $"cycle {_services.Clock.CycleAt(_services.Time.Now):0}" : "Tidal idle")} · {_canvas.Fps:0} fps";
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#4ECDC4")),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4),
    };

    private static Button Button(string text, string tip, Action click)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private Control Check(string text, string tip, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Content = text, FontSize = 12, Margin = new Thickness(0, 0, 10, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(c, tip);
        c.IsCheckedChanged += (_, _) => set(c.IsChecked == true);
        _sync.Add(() => { if (c.IsChecked != get()) c.IsChecked = get(); });
        return c;
    }

    private Control Toggle(string text, string key, Func<bool> get, Action<bool> set)
    {
        var t = new ToggleButton { Content = text, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4), VerticalAlignment = VerticalAlignment.Center };
        if (key.Length > 0) ToolTip.SetTip(t, $"Key: {key}");
        t.IsCheckedChanged += (_, _) => set(t.IsChecked == true);
        _sync.Add(() => { if (t.IsChecked != get()) t.IsChecked = get(); });
        return t;
    }

    private Control Combo(string label, List<string> items, Func<int> get, Action<int> set, string? tip = null)
    {
        var c = new ComboBox { ItemsSource = items, FontSize = 12, MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
        if (tip is not null) ToolTip.SetTip(c, tip);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0 && c.SelectedIndex != get()) set(c.SelectedIndex); };
        _sync.Add(() => { if (c.SelectedIndex != get() && !c.IsDropDownOpen) c.SelectedIndex = get(); });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 10, 4) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8A93A6")), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(c);
        return panel;
    }

    private Control Slider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var s = new Slider { Minimum = min, Maximum = max, Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -6, 0, -6) };
        var value = new TextBlock { FontSize = 11, Width = 34, Foreground = new SolidColorBrush(Color.Parse("#C9CFDA")), VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, _) => { set(s.Value); value.Text = format(s.Value); };
        _sync.Add(() => { if (Math.Abs(s.Value - get()) > 1e-6) s.Value = get(); value.Text = format(get()); });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 10, 4) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8A93A6")), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(s);
        panel.Children.Add(value);
        return panel;
    }

    // ------------------------------------------------------------------ keys

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        var o = _canvas.Options;
        var d = _canvas.Director;
        double now = _services.Time.Now;
        bool handled = true;
        switch (e.Key)
        {
            case Key.F11:
            case Key.F: ToggleFullScreen(); break;
            case Key.Escape when WindowState == WindowState.FullScreen: WindowState = WindowState.Normal; break;
            case Key.Space:
            case Key.Right: d.GoRandom(now); break;
            case Key.Left: d.Go(d.Scene - 1, now); break;
            case >= Key.D1 and <= Key.D9: d.Go(e.Key - Key.D1, now); break;
            case >= Key.NumPad1 and <= Key.NumPad9: d.Go(e.Key - Key.NumPad1, now); break;
            case Key.A: o.Auto = !o.Auto; break;
            case Key.C: o.Code = (VjCodeMode)(((int)o.Code + 1) % 3); break;
            case Key.T: o.Transition = o.Transition + 1 >= VjDirector.Transitions.Count ? -1 : o.Transition + 1; break;
            case Key.Q: d.NextPalette(now); break;
            case Key.R: o.RgbSplit = !o.RgbSplit; break;
            case Key.K: o.Kaleido = o.Kaleido switch { 0 => 4, 4 => 6, 6 => 8, 8 => 12, _ => 0 }; break;
            case Key.M: o.Mirror = !o.Mirror; break;
            case Key.P: o.Pixelate = !o.Pixelate; break;
            case Key.S: o.Scanlines = !o.Scanlines; break;
            case Key.B: o.BeatFlash = !o.BeatFlash; break;
            case Key.X: o.Particles = !o.Particles; break;
            case Key.N: o.Labels = !o.Labels; break;
            case Key.L: o.Hud = !o.Hud; break;
            case Key.Up: o.Intensity = Math.Min(2, o.Intensity + 0.1); break;
            case Key.Down: o.Intensity = Math.Max(0, o.Intensity - 0.1); break;
            case Key.OemCloseBrackets: o.Trails = Math.Min(0.97, o.Trails + 0.05); break;
            case Key.OemOpenBrackets: o.Trails = Math.Max(0, o.Trails - 0.05); break;
            case Key.H: ShowControls(!_bar.IsVisible); break;
            case Key.OemQuestion:
                _hint.Text = "Space/→ next · ← previous · 1–9 scene · A auto · T transition · Q colours · C code · R rgb · K kaleido · M mirror · " +
                             "P pixelate · S scanlines · B flash · X hits · N names · L scene names · ↑↓ intensity · [ ] trails · F11 full screen · H hide";
                ShowControls(true);
                break;
            default: handled = false; break;
        }
        if (handled)
        {
            e.Handled = true;
            Sync();
        }
    }

    /// <summary>Small live meters: bass, mid, high and the beat.</summary>
    private sealed class MeterView(VjSignals signals) : Control
    {
        private DispatcherTimer? _timer;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _timer.Tick += (_, _) => InvalidateVisual();
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer?.Stop();
        }

        public override void Render(DrawingContext dc)
        {
            var values = new[] { signals.Bass, signals.Mid, signals.High, signals.Beat };
            var colors = new[] { "#FF6B6B", "#FFD166", "#4ECDC4", "#FFFFFF" };
            double w = Bounds.Width / values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                double h = Math.Clamp(values[i], 0, 1) * (Bounds.Height - 2);
                dc.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Rect(i * w + 1, 0, w - 3, Bounds.Height));
                dc.FillRectangle(new SolidColorBrush(Color.Parse(colors[i])), new Rect(i * w + 1, Bounds.Height - h, w - 3, h));
            }
        }
    }
}
