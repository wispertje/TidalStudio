using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TidalStudio.App.Services;
using TidalStudio.Core.Ml;

namespace TidalStudio.App.Muse;

/// <summary>
/// The live suggestion popup (Ctrl+Shift+G): pick what you want with one key, listen to the options in time with
/// the music, Enter pastes the code into your file.
/// </summary>
public sealed class SuggestPopup : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#171A21"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#20242E"));
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#25404A"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D7DCE5"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4ECDC4"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    private readonly MuseService _muse;
    private readonly AppServices _services;
    private readonly Func<string, bool, Task> _paste;
    private readonly TextBlock _context = new() { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Dictionary<SuggestKind, ToggleButton> _kindButtons = new();
    private readonly Dictionary<int, ToggleButton> _barButtons = new();
    private readonly Slider _surprise = new() { Minimum = 0, Maximum = 1, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _density = new() { Minimum = -1, Maximum = 4, Width = 110, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _densityLabel = new() { Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 54 };
    private readonly ComboBox _sound = new() { MinWidth = 150, FontSize = 12 };
    private List<Suggestion> _items = new();
    private int _selected;
    private int _auditioning = -1;
    private TidalContext? _lastContext;
    private double _bpm = 120;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _closing;
    private bool _again, _againKeepSound = true;

    public SuggestPopup(MuseService muse, AppServices services, Func<string, bool, Task> paste)
    {
        _muse = muse;
        _services = services;
        _paste = paste;
        Title = "Muse — suggestions";
        Width = 660;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.BorderOnly;
        Background = Bg;
        var s = services.Settings.Muse;
        _surprise.Value = s.Surprise;
        _density.Value = s.Density;
        UpdateDensityLabel();
        Content = Build();
        KeyDown += OnKey;
        Deactivated += (_, _) => { if (!_closing) _ = CloseAsync(); };
        Opened += async (_, _) => await RefreshAsync();
    }

    private SuggestKind Kind
    {
        get => Enum.TryParse<SuggestKind>(_services.Settings.Muse.Kind, out var k) ? k : SuggestKind.Melody;
        set => _services.Settings.Muse.Kind = value.ToString();
    }

    private Control Build()
    {
        var title = new TextBlock { Text = "Muse", FontWeight = FontWeight.Bold, FontSize = 15, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center };
        var model = new TextBlock { Text = _muse.ModelStatus, Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var head = new DockPanel { Children = { title, model } };

        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (kind, label) in new[] { (SuggestKind.Melody, "M  melody"), (SuggestKind.Bass, "B  bass"), (SuggestKind.Drums, "D  drums"), (SuggestKind.Chords, "C  chords"), (SuggestKind.NewPart, "P  new part") })
        {
            var b = new ToggleButton { Content = label, FontSize = 12, Padding = new Thickness(10, 4), IsChecked = Kind == kind };
            b.Click += async (_, _) => { Kind = kind; SyncButtons(); await RefreshAsync(); };
            _kindButtons[kind] = b;
            kinds.Children.Add(b);
        }

        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bars.Children.Add(new TextBlock { Text = "bars", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        foreach (int n in Tokenizer.LengthChoices)
        {
            var b = new ToggleButton { Content = n.ToString(), FontSize = 11, Padding = new Thickness(7, 2), IsChecked = _services.Settings.Muse.Bars == n };
            b.Click += async (_, _) => { _services.Settings.Muse.Bars = n; SyncButtons(); await RefreshAsync(); };
            _barButtons[n] = b;
            bars.Children.Add(b);
        }
        _surprise.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _services.Settings.Muse.Surprise = _surprise.Value; };
        _density.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _services.Settings.Muse.Density = (int)_density.Value;
            UpdateDensityLabel();
        };
        _surprise.PointerCaptureLost += async (_, _) => await RefreshAsync();
        _density.PointerCaptureLost += async (_, _) => await RefreshAsync();
        _sound.SelectionChanged += async (_, _) => { if (!_busy && _sound.IsDropDownOpen == false) await RefreshAsync(keepSound: true); };

        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(bars);
        controls.Children.Add(Labeled("busy", _density, _densityLabel));
        controls.Children.Add(Labeled("surprise", _surprise));
        controls.Children.Add(Labeled("sound", _sound));
        foreach (var c in controls.Children.OfType<Control>()) c.Margin = new Thickness(0, 2, 14, 2);

        var scroll = new ScrollViewer { Content = _list, MaxHeight = 470, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var keys = new TextBlock
        {
            Text = "↑↓ choose · Space listen (d14–16) · Enter paste · Ctrl+Enter paste & run · R new ideas · + like · − dislike · Esc close",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        };
        var root = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10), Children = { head, _context, kinds, controls, scroll, _status, keys } };
        return new Border { BorderBrush = Accent, BorderThickness = new Thickness(1), Child = root };
    }

    private static Control Labeled(string label, params Control[] controls)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        p.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        foreach (var c in controls) p.Children.Add(c);
        return p;
    }

    private void UpdateDensityLabel() => _densityLabel.Text = (int)_density.Value switch
    {
        < 0 => "auto", 0 => "sparse", 1 => "light", 2 => "medium", 3 => "busy", _ => "very busy",
    };

    private void SyncButtons()
    {
        foreach (var (k, b) in _kindButtons) b.IsChecked = k == Kind;
        foreach (var (n, b) in _barButtons) b.IsChecked = n == _services.Settings.Muse.Bars;
    }

    private void FillSounds(TidalContext context)
    {
        var current = (_sound.SelectedItem as ComboBoxItem)?.Tag as string;
        _busy = true;
        try
        {
            _sound.Items.Clear();
            _sound.Items.Add(new ComboBoxItem { Content = "auto", Tag = null });
            var catalog = _muse.Catalog;
            IEnumerable<string> names = Kind switch
            {
                SuggestKind.Drums => catalog.Kits.Keys.OrderBy(k => k),
                SuggestKind.NewPart => Array.Empty<string>(),
                _ => catalog.ForRole(Suggester.RoleOf(Kind)).Select(s => s.Name),
            };
            foreach (var n in names) _sound.Items.Add(new ComboBoxItem { Content = n, Tag = n });
            _sound.SelectedIndex = Math.Max(0, _sound.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string?)i.Tag == current));
        }
        finally { _busy = false; }
    }

    private async Task RefreshAsync(bool keepSound = false)
    {
        // Asked again while busy (a key during "Thinking…"): stop this round and do it again with the new choice.
        if (_busy) { _again = true; _againKeepSound &= keepSound; _cts?.Cancel(); return; }
        _again = false;
        _againKeepSound = true;
        _busy = true;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await StopAuditionAsync();
            _status.Text = "Listening to what's playing…";
            var s = _services.Settings.Muse;
            var (context, bpm) = await _muse.LiveContextAsync(Math.Max(4, s.Bars));
            _lastContext = context;
            _bpm = bpm;
            _context.Text = (_services.Ghci.IsReady ? "" : "Tidal isn't running: suggestions start from C minor.  ") +
                            $"{context.Describe()} · {bpm:0} bpm";
            if (!keepSound) { _busy = false; FillSounds(context); _busy = true; }
            string? sound = (_sound.SelectedItem as ComboBoxItem)?.Tag as string;
            var options = new SuggestOptions
            {
                Kind = Kind, Bars = s.Bars, Density = s.Density, Surprise = s.Surprise, InKeyOnly = s.InKeyOnly,
                Sound = sound, UseModel = s.UseModel, Count = 4, Bpm = bpm,
            };
            _status.Text = "Thinking…";
            var suggester = _muse.CreateSuggester();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var items = await Task.Run(() => suggester.Suggest(context, options, new Random(), cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _items = items;
            _selected = 0;
            _status.Text = items.Count == 0 ? "No suggestions (are there samples for this in your samples folder?)"
                : $"{items.Count} ideas in {watch.ElapsedMilliseconds} ms ({items[0].Source}).";
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _status.Text = "Couldn't make suggestions: " + e.Message; }
        finally
        {
            _busy = false;
            if (_again && !_closing) { bool keep = _againKeepSound; _ = Dispatcher.UIThread.InvokeAsync(() => RefreshAsync(keep)); }
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            int index = i;
            var head = new TextBlock
            {
                Text = $"{i + 1}. {item.Title}" + (index == _auditioning ? "   ♪ playing" : ""),
                Foreground = index == _selected ? Accent : Text, FontSize = 12, FontWeight = FontWeight.SemiBold,
            };
            var meta = new TextBlock { Text = $"{item.Source} · taste {item.Taste:+0.00;-0.00;0}", Foreground = Dim, FontSize = 10 };
            var code = new SelectableTextBlock { Text = item.Code, FontFamily = Mono, FontSize = 11.5, Foreground = Text, TextWrapping = TextWrapping.Wrap };
            var card = new Border
            {
                Background = index == _selected ? Selected : Panel, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6),
                Child = new StackPanel { Spacing = 3, Children = { new DockPanel { Children = { head, meta } }, code } },
            };
            DockPanel.SetDock(meta, Dock.Right);
            meta.HorizontalAlignment = HorizontalAlignment.Right;
            card.PointerPressed += (_, e) =>
            {
                _selected = index;
                Render();
                if (e.ClickCount == 2) _ = PasteAsync(run: false);
            };
            _list.Children.Add(card);
        }
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; await CloseAsync(); return;
            case Key.Down: e.Handled = true; Move(1); return;
            case Key.Up: e.Handled = true; Move(-1); return;
            case Key.Enter: e.Handled = true; await PasteAsync(run: ctrl); return;
            case Key.Space: e.Handled = true; await ToggleAuditionAsync(); return;
            case Key.R when !ctrl: e.Handled = true; await RefreshAsync(keepSound: true); return;
            case Key.OemPlus or Key.Add: e.Handled = true; Rate(true); return;
            case Key.OemMinus or Key.Subtract: e.Handled = true; Rate(false); return;
            case Key.M: e.Handled = true; await SetKind(SuggestKind.Melody); return;
            case Key.B: e.Handled = true; await SetKind(SuggestKind.Bass); return;
            case Key.D: e.Handled = true; await SetKind(SuggestKind.Drums); return;
            case Key.C when !ctrl: e.Handled = true; await SetKind(SuggestKind.Chords); return;
            case Key.P: e.Handled = true; await SetKind(SuggestKind.NewPart); return;
            case Key.D1 or Key.D2 or Key.D4 or Key.D8:
                e.Handled = true;
                _services.Settings.Muse.Bars = e.Key switch { Key.D1 => 1, Key.D2 => 2, Key.D4 => 4, _ => 8 };
                SyncButtons();
                await RefreshAsync(keepSound: true);
                return;
        }
    }

    // For the self-test.
    internal int ItemCount => _items.Count;
    internal string StatusText => _status.Text ?? "";
    internal void PressKey(Key key, KeyModifiers mods = KeyModifiers.None) => OnKey(this, new KeyEventArgs { Key = key, KeyModifiers = mods, RoutedEvent = KeyDownEvent });

    private async Task SetKind(SuggestKind kind)
    {
        Kind = kind;
        SyncButtons();
        await RefreshAsync();
    }

    private void Move(int by)
    {
        if (_items.Count == 0) return;
        _selected = (_selected + by + _items.Count) % _items.Count;
        Render();
        if (_auditioning >= 0) _ = AuditionSelectedAsync();
    }

    private async Task ToggleAuditionAsync()
    {
        if (_auditioning == _selected) { await StopAuditionAsync(); Render(); return; }
        await AuditionSelectedAsync();
    }

    private async Task AuditionSelectedAsync()
    {
        if (_selected >= _items.Count) return;
        if (!_services.Ghci.IsReady) { _status.Text = "Start Tidal to listen to suggestions."; return; }
        var result = await _muse.AuditionAsync(_items[_selected]);
        _auditioning = result.Success ? _selected : -1;
        _status.Text = result.Success ? "Listening on d14–d16 (Space to stop)." : "Couldn't play it: " + result.ErrorText;
        Render();
    }

    private async Task StopAuditionAsync()
    {
        if (_auditioning < 0 && !_muse.Auditioning) return;
        _auditioning = -1;
        await _muse.StopAuditionAsync();
    }

    private void Rate(bool liked)
    {
        if (_selected >= _items.Count) return;
        if (liked) _muse.Like(_items[_selected]);
        else _muse.Dislike(_items[_selected]);
        _status.Text = liked ? "Liked — Muse will lean towards ideas like this." : "Disliked — noted.";
    }

    private async Task PasteAsync(bool run)
    {
        if (_selected >= _items.Count) return;
        var item = _items[_selected];
        await StopAuditionAsync();
        _muse.RecordInsert(item);
        _closing = true;
        Close();
        await _paste(item.Code, run);
    }

    private async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _cts?.Cancel();
        await StopAuditionAsync();
        Close();
    }

    /// <summary>Opens the popup next to a screen point (the editor's cursor).</summary>
    public void ShowAt(Window owner, PixelPoint point)
    {
        var screen = owner.Screens.ScreenFromPoint(point) ?? owner.Screens.Primary;
        int x = point.X, y = point.Y + 4;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            x = Math.Clamp(x, area.X, area.Right - (int)(Width * scale));
            if (y + 520 * scale > area.Bottom) y = Math.Max(area.Y, point.Y - (int)(560 * scale));
        }
        Position = new PixelPoint(x, y);
        Show(owner);
        Dispatcher.UIThread.Post(() => Activate());
    }
}
