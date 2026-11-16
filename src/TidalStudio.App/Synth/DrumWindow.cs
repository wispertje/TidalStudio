using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TidalStudio.App.Services;
using TidalStudio.Core.Completion;
using TidalStudio.Core.Settings;
using TidalStudio.Core.Synth;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Synth;

/// <summary>
/// Tidepool Drums: a drum synthesizer that saves a whole kit as SuperDirt sample folders: one sound per drum
/// ("kitbd", "kitsd", … with versions as n 0, n 1, …) and optionally the kit as one sound ("kit" # n 0..).
/// </summary>
public sealed class DrumWindow : Window
{
    private static readonly IBrush PadBrush = new SolidColorBrush(Color.Parse("#1F232C"));
    private static readonly IBrush PadSelectedBrush = new SolidColorBrush(Color.Parse("#262B36"));

    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly Action _samplesChanged;
    private readonly Action<OutputKind, string> _log;
    private readonly Func<string, Task<(bool Ok, string Message)>> _restartSuperDirt;
    private readonly PreviewPlayer _player = new();
    private readonly DispatcherTimer _autoPreviewTimer;

    private DrumKit _kit;
    private int _selected;
    private int _previewVariation;
    private int _renderVersion;

    private readonly ComboBox _kits = new() { MinWidth = 170, PlaceholderText = "Choose a kit…" };
    private readonly TextBox _name = new() { Width = 120 };
    private readonly CheckBox _autoPreview = new() { Content = "Auto-preview", IsChecked = true };
    private readonly CheckBox _restartOnSave = new() { Content = "Restart SuperDirt after saving", FontSize = 12 };
    private readonly CheckBox _combined = new() { Content = "Also save as one kit", FontSize = 12 };
    private readonly WrapPanel _pads = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _editor = new();
    private readonly WaveformView _wave = new() { Height = 70, Hint = "Click a drum (or Space) to hear it, G for a groove" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _folderLabel = new() { FontSize = 11, Foreground = SynthWindow.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public DrumWindow(AppServices services, Action samplesChanged, Action<OutputKind, string> log,
        Func<string, Task<(bool Ok, string Message)>> restartSuperDirt)
    {
        _services = services;
        _settings = services.Settings;
        _samplesChanged = samplesChanged;
        _log = log;
        _restartSuperDirt = restartSuperDirt;
        _kit = LoadLastKit();

        Title = "Tidepool Drums — drum kit synth";
        Width = 1340;
        Height = 880;
        MinWidth = 860;
        MinHeight = 560;
        Background = (IBrush?)Application.Current?.FindResource("TsWindowBackground") ?? Brushes.Black;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TidalStudio/Assets/TidalStudio.ico"))); }
        catch (Exception) { }

        _autoPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _autoPreviewTimer.Tick += (_, _) => { _autoPreviewTimer.Stop(); Preview(); };

        _restartOnSave.IsChecked = _settings.SynthRestartOnSave;
        _restartOnSave.IsCheckedChanged += (_, _) => _settings.SynthRestartOnSave = _restartOnSave.IsChecked == true;
        ToolTip.SetTip(_restartOnSave, "After saving, make SuperDirt load the kit (restarting a SuperCollider that TidalStudio didn't start).");
        _combined.IsChecked = _settings.DrumSaveCombined;
        _combined.IsCheckedChanged += (_, _) => { _settings.DrumSaveCombined = _combined.IsChecked == true; UpdateStatusHint(); };
        ToolTip.SetTip(_combined, "Besides one sound per drum (kitbd, kitsd, …), save the kit as one sound with a drum per n: s \"kit\" # n 0, n 1, …");

        Content = BuildLayout();
        RebuildAll();
        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            _settings.DrumKitJson = _kit.ToJson();
            _player.Dispose();
        };
    }

    private DrumVoice Voice => _kit.Voices[Math.Clamp(_selected, 0, _kit.Voices.Count - 1)];

    // ------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        RefreshKitList();
        _kits.SelectionChanged += (_, _) =>
        {
            if (_kits.SelectedItem is not string item || item.StartsWith('—')) return;
            LoadKit(item);
        };
        _name.Text = _kit.Name;
        _name.TextChanged += (_, _) => { _kit.Name = _name.Text?.Trim() ?? ""; RefreshPads(); UpdateStatusHint(); };
        ToolTip.SetTip(_name, "Kit name. Every drum is saved as kit name + drum name, e.g. \"mykit\" + \"bd\" = s \"mykitbd\".");

        var play = SynthWindow.AccentButton("▶ Play", "Play the selected drum (Space)");
        play.Click += (_, _) => Preview();
        var groove = SynthWindow.AccentButton("▶ Groove", "Play a two-bar beat with this kit (G)");
        groove.Click += (_, _) => PlayGroove();
        var save = SynthWindow.AccentButton("💾 Save kit to SuperDirt", "Render every drum to WAV files in your samples folder (Ctrl+S)");
        save.Click += async (_, _) => await SaveAsync();

        var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 6) };
        top.Children.Add(Labelled("Kit", _kits));
        top.Children.Add(Labelled("Name", _name));
        top.Children.Add(play);
        top.Children.Add(groove);
        top.Children.Add(_autoPreview);
        top.Children.Add(new Border { Width = 12 });
        top.Children.Add(save);
        top.Children.Add(_combined);
        top.Children.Add(_restartOnSave);
        top.Children.Add(SynthWindow.ExampleButton(this, "02-tidepool-drums.tidal", "how to play the kits: one sound per drum, versions, the combined kit, fills, groove"));

        // Left: the pads. Right: the selected drum's controls.
        var add = SynthWindow.SmallButton("+ Add drum", "Add another drum to the kit");
        add.Click += (_, _) => AddVoice();
        var remove = SynthWindow.SmallButton("Remove", "Remove the selected drum from the kit");
        remove.Click += (_, _) => RemoveVoice();
        var padButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        padButtons.Children.Add(add);
        padButtons.Children.Add(remove);
        var padTitle = new TextBlock { Text = "DRUMS — click to select and play", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = SynthWindow.MutedBrush, Margin = new Thickness(0, 0, 0, 6) };
        var padColumn = new DockPanel { Width = 4 * 112 + 8 };
        DockPanel.SetDock(padTitle, Dock.Top);
        DockPanel.SetDock(padButtons, Dock.Bottom);
        padColumn.Children.Add(padTitle);
        padColumn.Children.Add(padButtons);
        padColumn.Children.Add(new ScrollViewer { Content = _pads });

        var middle = new DockPanel { Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(padColumn, Dock.Left);
        middle.Children.Add(padColumn);
        middle.Children.Add(new ScrollViewer { Content = _editor, Margin = new Thickness(12, 0, 0, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var changeFolder = SynthWindow.SmallButton("Change…", "Choose the folder your sample folders are saved in");
        changeFolder.Click += async (_, _) => await ChooseSamplesFolderAsync();
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        folderRow.Children.Add(_folderLabel);
        folderRow.Children.Add(changeFolder);
        UpdateFolderLabel();

        var statusBar = new DockPanel { Margin = new Thickness(12, 4, 12, 10) };
        DockPanel.SetDock(folderRow, Dock.Bottom);
        DockPanel.SetDock(_statusActions, Dock.Right);
        statusBar.Children.Add(folderRow);
        statusBar.Children.Add(_statusActions);
        statusBar.Children.Add(_status);

        var bottom = new StackPanel();
        bottom.Children.Add(new Border { Child = _wave, Margin = new Thickness(12, 6, 12, 4) });
        bottom.Children.Add(new Border { BorderBrush = SynthWindow.BorderBrush2, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusBar });

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(middle);
        return root;
    }

    private void RebuildAll()
    {
        _name.Text = _kit.Name;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _kit.Voices.Count - 1));
        RefreshPads();
        RebuildEditor();
        UpdateStatusHint();
    }

    // ------------------------------------------------------------------ pads

    private static Color ModelColor(DrumModel model) => model switch
    {
        DrumModel.Kick => Color.Parse("#FF6B6B"),
        DrumModel.Snare or DrumModel.Clap or DrumModel.Rim => Color.Parse("#FFD166"),
        DrumModel.ClosedHat or DrumModel.OpenHat or DrumModel.Cymbal or DrumModel.Shaker => Color.Parse("#4ECDC4"),
        DrumModel.Tom => Color.Parse("#FB923C"),
        _ => Color.Parse("#A78BFA"),
    };

    private void RefreshPads()
    {
        _pads.Children.Clear();
        for (int i = 0; i < _kit.Voices.Count; i++)
        {
            int index = i;
            var v = _kit.Voices[i];
            var color = ModelColor(v.Model);
            bool selected = i == _selected;
            var abbr = new TextBlock { Text = v.Abbreviation, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(v.Enabled ? color : Color.FromArgb(100, color.R, color.G, color.B)) };
            var model = new TextBlock { Text = ModelName(v.Model) + (v.Variations > 1 ? $" ×{v.Variations}" : ""), FontSize = 10.5, Foreground = SynthWindow.MutedBrush };
            var sound = new TextBlock { Text = v.Enabled ? _kit.SoundName(v) : "off", FontSize = 10, Foreground = SynthWindow.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 26, 0) };
            var on = new CheckBox { IsChecked = v.Enabled, MinWidth = 0, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -6) };
            ToolTip.SetTip(on, "Save this drum");
            on.IsCheckedChanged += (_, _) => { v.Enabled = on.IsChecked == true; RefreshPads(); UpdateStatusHint(); };
            var text = new StackPanel();
            text.Children.Add(abbr);
            text.Children.Add(model);
            text.Children.Add(sound);
            var grid = new Grid();
            grid.Children.Add(text);
            grid.Children.Add(on);
            var pad = new Border
            {
                Width = 104, Height = 76, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8, 5),
                CornerRadius = new CornerRadius(6),
                Background = selected ? PadSelectedBrush : PadBrush,
                BorderBrush = new SolidColorBrush(selected ? color : Color.Parse("#2A2F3A")),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Child = grid,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(pad, DrumKits.Abbreviations.TryGetValue(v.Abbreviation, out var what) ? $"{v.Abbreviation}: {what}" : v.Abbreviation);
            pad.PointerPressed += (_, e) =>
            {
                if (e.Source is Visual s && s.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
                Select(index);
            };
            _pads.Children.Add(pad);
        }
    }

    private static string ModelName(DrumModel m) => m switch
    {
        DrumModel.ClosedHat => "Closed hat",
        DrumModel.OpenHat => "Open hat",
        _ => m.ToString(),
    };

    private void Select(int index)
    {
        _selected = index;
        _previewVariation = 0;
        RefreshPads();
        RebuildEditor();
        Preview();
    }

    private void AddVoice()
    {
        var used = _kit.Voices.Select(v => v.Abbreviation).ToHashSet();
        string name = "perc";
        for (int i = 2; used.Contains(name); i++) name = $"perc{i}";
        _kit.Voices.Add(DrumKits.Default(DrumModel.Zap, name));
        Select(_kit.Voices.Count - 1);
    }

    private void RemoveVoice()
    {
        if (_kit.Voices.Count <= 1) return;
        _kit.Voices.RemoveAt(_selected);
        _selected = Math.Min(_selected, _kit.Voices.Count - 1);
        RebuildAll();
    }

    // ------------------------------------------------------------------ editor

    private readonly List<Action> _refreshers = new();
    private readonly Dictionary<string, Control> _sectionMap = new();

    /// <summary>A knob moved: redraw the diagram, the graphs and the pad.</summary>
    private void Changed()
    {
        foreach (var refresh in _refreshers) refresh();
    }

    private void RebuildEditor()
    {
        _refreshers.Clear();
        _sectionMap.Clear();
        if (_kit.Voices.Count == 0) { _editor.Content = null; return; }
        var v = Voice;

        // --- header: name, starting point, randomise
        var abbr = new TextBox { Text = v.Abbreviation, Width = 80 };
        ToolTip.SetTip(abbr, "Drum name: lowercase letters and digits. Saved as kit name + this (bd, sd, hh, oh, cp, lt, mt, ht, rim, cb, cr, rd, sh, perc…).");
        abbr.TextChanged += (_, _) => { v.Abbreviation = abbr.Text?.Trim() ?? ""; RefreshPads(); UpdateStatusHint(); };
        var models = Enum.GetValues<DrumModel>().ToList();
        var model = new ComboBox { ItemsSource = models.Select(ModelName).ToList(), SelectedIndex = models.IndexOf(v.Model), MinWidth = 120, FontSize = 12 };
        ToolTip.SetTip(model, "Start this drum from another model's layers (keeps the name, tune, output and versions)");
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedIndex < 0 || models[model.SelectedIndex] == v.Model) return;
            var fresh = DrumKits.Default(models[model.SelectedIndex], v.Abbreviation);
            fresh.Enabled = v.Enabled; fresh.Variations = v.Variations; fresh.Spread = v.Spread; fresh.Pan = v.Pan; fresh.Tune = v.Tune;
            _kit.Voices[_selected] = fresh;
            RefreshPads();
            RebuildEditor();
            Commit();
        };
        var reset = SynthWindow.SmallButton("Reset", "Back to the starting layers of this model");
        reset.Click += (_, _) =>
        {
            var fresh = DrumKits.Default(v.Model, v.Abbreviation);
            fresh.Enabled = v.Enabled; fresh.Variations = v.Variations; fresh.Spread = v.Spread;
            _kit.Voices[_selected] = fresh;
            RefreshPads();
            RebuildEditor();
            Commit();
        };
        var random = SynthWindow.SmallButton("Randomise", "A random variation on this drum (Ctrl+R)");
        random.Click += (_, _) => RandomiseVoice();
        var header = new WrapPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Labelled("Drum", abbr));
        header.Children.Add(Labelled("Start from", model));
        header.Children.Add(reset);
        header.Children.Add(random);
        header.Children.Add(new TextBlock { Text = $"→ s \"{_kit.SoundName(v)}\"", FontSize = 12, Foreground = SynthWindow.MutedBrush, VerticalAlignment = VerticalAlignment.Center });

        // --- signal flow
        var flow = new DrumFlowView(() => Voice);
        flow.BlockClicked += key =>
        {
            if (!_sectionMap.TryGetValue(key, out var target)) return;
            target.BringIntoView();
            if (target is Border border)
            {
                var original = border.Background;
                border.Background = new SolidColorBrush(Color.Parse("#262B36"));
                DispatcherTimer.RunOnce(() => border.Background = original, TimeSpan.FromMilliseconds(450));
            }
        };
        _refreshers.Add(flow.InvalidateVisual);

        // All graphs share one time span: how long the drum sounds.
        Func<double> span = () => Math.Max(0.05, DrumEngine.SoundingLength(Voice) * 1.05);

        var rows = new StackPanel { Spacing = 10 };
        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(header);
        top.Children.Add(flow);
        rows.Children.Add(SynthWindow.Panel(null, top, DrumColors.Output));
        rows.Children.Add(TwoColumns(Map("osc1", OscPanel(v, v.Osc1, 1, span)), Map("osc2", OscPanel(v, v.Osc2, 2, span))));
        rows.Children.Add(TwoColumns(Map("noise", NoisePanel(v.Noise, span)), Map("metal", MetalPanel(v.Metal, span))));
        var shape = ShapePanel(v);
        _sectionMap["drive"] = _sectionMap["filter"] = _sectionMap["room"] = shape;
        rows.Children.Add(TwoColumns(Map("click", ClickPanel(v.Click)), shape));
        rows.Children.Add(Map("out", OutputPanel(v)));
        rows.Children.Add(SynthWindow.Panel("KIT", KitOptions(), DrumColors.Output));
        _editor.Content = rows;
    }

    private Control Map(string key, Control section)
    {
        _sectionMap[key] = section;
        return section;
    }

    private static Control TwoColumns(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A layer panel: on/off checkbox as its title; the body fades when the layer is off.</summary>
    private Border LayerPanel(string title, Color color, Func<bool> get, Action<bool> set, Control body, string tip)
    {
        var enabled = new CheckBox { IsChecked = get(), Content = title, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = new SolidColorBrush(color) };
        ToolTip.SetTip(enabled, tip);
        body.Opacity = get() ? 1 : 0.45;
        enabled.IsCheckedChanged += (_, _) =>
        {
            set(enabled.IsChecked == true);
            body.Opacity = get() ? 1 : 0.45;
            Changed();
            Commit();
        };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(enabled);
        content.Children.Add(body);
        return SynthWindow.Panel(null, content, color);
    }

    private ComboBox Choice<T>(T value, Action<T> set, Func<T, string>? name = null) where T : struct, Enum
    {
        var values = Enum.GetValues<T>().ToList();
        name ??= x => x.ToString();
        var combo = new ComboBox { ItemsSource = values.Select(name).ToList(), SelectedIndex = values.IndexOf(value), MinWidth = 90, FontSize = 12 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            set(values[combo.SelectedIndex]);
            Changed();
            Commit();
        };
        return combo;
    }

    private Control EnvelopeKnobs(DrumEnvelope env, Color color) => SynthWindow.KnobRow(
        K("Attack", 0, 0.2, 0.0005, () => env.Attack, x => env.Attack = x, SynthWindow.Seconds, curve: 3, accent: color),
        K("Hold", 0, 0.5, 0, () => env.Hold, x => env.Hold = x, SynthWindow.Seconds, curve: 2.5, accent: color),
        K("Decay", 0.005, 4, 0.4, () => env.Decay, x => env.Decay = x, SynthWindow.Seconds, curve: 2.5, accent: color),
        K("Curve", 0, 1, 0.8, () => env.Curve, x => env.Curve = x, x => x < 0.05 ? "linear" : $"{x * 100:0}% exp", accent: color));

    private Control OscPanel(DrumVoice v, DrumOscillator o, int number, Func<double> span)
    {
        var color = number == 1 ? DrumColors.Osc1 : DrumColors.Osc2;
        var tables = Wavetables.Available.Select(t => t.Name).Where(n => n != "Noise").ToList();
        var table = new ComboBox { ItemsSource = tables, SelectedItem = tables.Contains(o.Table) ? o.Table : "Basic", MinWidth = 96, FontSize = 12 };
        ToolTip.SetTip(table, "Wavetable; Basic goes sine → triangle → saw → square with Position");
        table.SelectionChanged += (_, _) => { if (table.SelectedItem is string t) { o.Table = t; Changed(); Commit(); } };

        var graph = new DrumCurveView(t => o.Envelope.At(t), span, color, () => o.Enabled,
            t => o.PitchDrop <= 0 ? 0 : DrumEngine.PitchShape(t, o.PitchTime, o.PitchCurve));
        _refreshers.Add(graph.InvalidateVisual);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(Labelled("Wave", table));
        body.Children.Add(graph);
        body.Children.Add(number == 1
            ? SynthWindow.KnobRow(
                K("Position", 0, 1, 0, () => o.Position, x => o.Position = x, SynthWindow.Percent, accent: color),
                K("Pitch", 20, 4000, 50, () => o.Frequency, x => o.Frequency = x, SynthWindow.Hz, log: true, accent: color),
                K("Level", 0, 1, 1, () => o.Level, x => o.Level = x, SynthWindow.Percent, accent: color))
            : SynthWindow.KnobRow(
                K("Position", 0, 1, 0, () => o.Position, x => o.Position = x, SynthWindow.Percent, accent: color),
                K("Ratio", 0.1, 8, 1.5, () => o.Frequency, x => o.Frequency = x, x => $"×{x:0.##}", log: true, accent: color),
                K("Level", 0, 1, 0.5, () => o.Level, x => o.Level = x, SynthWindow.Percent, accent: color),
                K("FM → 1", 0, 1, 0, () => o.Fm, x => o.Fm = x, SynthWindow.Percent, curve: 1.5, accent: color)));
        body.Children.Add(SynthWindow.KnobRow(
            K("Drop", 0, 60, 0, () => o.PitchDrop, x => o.PitchDrop = x, x => $"{x:0} st", accent: color),
            K("Drop time", 0.002, 1, 0.05, () => o.PitchTime, x => o.PitchTime = x, SynthWindow.Seconds, curve: 2.5, accent: color),
            K("Drop curve", 0, 1, 0.8, () => o.PitchCurve, x => o.PitchCurve = x, x => x < 0.05 ? "linear" : $"{x * 100:0}% exp", accent: color)));
        body.Children.Add(EnvelopeKnobs(o.Envelope, color));
        string tip = number == 1
            ? "The body: a wavetable oscillator whose pitch starts Drop semitones higher and falls to Pitch over Drop time (the kick's punch)."
            : "A second oscillator at Ratio × oscillator 1's pitch (it follows the drop). Mix it in with Level, and/or let it frequency-modulate oscillator 1 with FM for metallic, bell-like or growling tones.";
        return LayerPanel($"OSC {number}", color, () => o.Enabled, x => o.Enabled = x, body, tip);
    }

    private Control NoisePanel(DrumNoise n, Func<double> span)
    {
        var color = DrumColors.Noise;
        var graph = new DrumCurveView(t => DrumEngine.NoiseEnvelope(n, t), span, color, () => n.Enabled);
        _refreshers.Add(graph.InvalidateVisual);
        var combos = new StackPanel { Orientation = Orientation.Horizontal };
        combos.Children.Add(Labelled("Colour", Choice(n.Color, x => n.Color = x)));
        combos.Children.Add(Labelled("Filter", Choice(n.Filter, x => n.Filter = x)));
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(combos);
        body.Children.Add(graph);
        body.Children.Add(SynthWindow.KnobRow(
            K("Cutoff", 40, 18000, 2000, () => n.Cutoff, x => n.Cutoff = x, SynthWindow.Hz, log: true, accent: color),
            K("Reso", 0, 1, 0.1, () => n.Resonance, x => n.Resonance = x, SynthWindow.Percent, accent: color),
            K("Level", 0, 1, 0.6, () => n.Level, x => n.Level = x, SynthWindow.Percent, accent: color),
            K("Hits", 1, 8, 1, () => n.Bursts, x => n.Bursts = (int)x, x => $"{x:0}", step: 1, accent: color),
            K("Spacing", 0.002, 0.05, 0.011, () => n.BurstSpacing, x => n.BurstSpacing = x, SynthWindow.Seconds, accent: color)));
        body.Children.Add(EnvelopeKnobs(n.Envelope, color));
        return LayerPanel("NOISE", color, () => n.Enabled, x => n.Enabled = x, body,
            "Filtered noise: snare wires, claps (several Hits a few ms apart), shakers, breath. Pink and brown noise are darker than white.");
    }

    private Control MetalPanel(DrumMetal m, Func<double> span)
    {
        var color = DrumColors.Metal;
        var graph = new DrumCurveView(t => m.Envelope.At(t), span, color, () => m.Enabled);
        _refreshers.Add(graph.InvalidateVisual);
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(graph);
        body.Children.Add(SynthWindow.KnobRow(
            K("Tune", -24, 24, 0, () => m.Tune, x => m.Tune = x, x => $"{x:+0.0;-0.0;0} st", bipolar: true, accent: color),
            K("Squares", 1, 6, 6, () => m.Voices, x => m.Voices = (int)x, x => x == 2 ? "2 (bell)" : $"{x:0}", step: 1, accent: color),
            K("Band", 300, 16000, 8000, () => m.Cutoff, x => m.Cutoff = x, SynthWindow.Hz, log: true, accent: color),
            K("Reso", 0, 1, 0.3, () => m.Resonance, x => m.Resonance = x, SynthWindow.Percent, accent: color),
            K("Level", 0, 1, 0.8, () => m.Level, x => m.Level = x, SynthWindow.Percent, accent: color)));
        body.Children.Add(EnvelopeKnobs(m.Envelope, color));
        return LayerPanel("METAL", color, () => m.Enabled, x => m.Enabled = x, body,
            "Square waves at inharmonic pitches through a band-pass: hats and cymbals (6 squares, high band) or a cowbell (2 squares, low band).");
    }

    private Control ClickPanel(DrumClick c)
    {
        var color = DrumColors.Click;
        var body = SynthWindow.KnobRow(
            K("Level", 0, 1, 0.4, () => c.Level, x => c.Level = x, SynthWindow.Percent, accent: color),
            K("Length", 0.0005, 0.03, 0.004, () => c.Length, x => c.Length = x, SynthWindow.Seconds, curve: 2, accent: color),
            K("Tone", 300, 12000, 3000, () => c.Tone, x => c.Tone = x, SynthWindow.Hz, log: true, accent: color));
        return LayerPanel("CLICK", color, () => c.Enabled, x => c.Enabled = x, body,
            "The attack: an impulse and a very short burst of bright noise. Makes kicks cut through and gives snares and rims their snap.");
    }

    private Control ShapePanel(DrumVoice v)
    {
        var color = DrumColors.Shape;
        var f = v.Filter;
        var combos = new WrapPanel { Orientation = Orientation.Horizontal };
        combos.Children.Add(Labelled("Drive", Choice(v.DriveMode, x => v.DriveMode = x)));
        combos.Children.Add(Labelled("Filter", Choice(f.Mode, x => f.Mode = x)));
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(combos);
        body.Children.Add(SynthWindow.KnobRow(
            K("Drive", 0, 1, 0, () => v.Drive, x => v.Drive = x, SynthWindow.Percent, accent: color),
            K("Cutoff", 40, 18000, 8000, () => f.Cutoff, x => f.Cutoff = x, SynthWindow.Hz, log: true, accent: color),
            K("Reso", 0, 1, 0.1, () => f.Resonance, x => f.Resonance = x, SynthWindow.Percent, accent: color),
            K("Env", -4, 8, 0, () => f.EnvAmount, x => f.EnvAmount = x, x => $"{x:+0.0;-0.0;0} oct", bipolar: true, accent: color),
            K("Env time", 0.005, 2, 0.15, () => f.EnvDecay, x => f.EnvDecay = x, SynthWindow.Seconds, curve: 2.5, accent: color)));
        body.Children.Add(SynthWindow.KnobRow(
            K("Room", 0, 1, 0, () => v.Room, x => v.Room = x, SynthWindow.Percent, accent: color),
            K("Size", 0, 1, 0.35, () => v.RoomSize, x => v.RoomSize = x, SynthWindow.Percent, accent: color)));
        return SynthWindow.Panel("DRIVE · FILTER · ROOM", body, color);
    }

    private Control OutputPanel(DrumVoice v)
    {
        var color = DrumColors.Output;
        var knobs = SynthWindow.KnobRow(
            K("Tune", -24, 24, 0, () => v.Tune, x => v.Tune = x, x => $"{x:+0.0;-0.0;0} st", bipolar: true, accent: color),
            K("Level", 0, 1, 0.9, () => v.Level, x => v.Level = x, SynthWindow.Percent, accent: color),
            K("Pan", -1, 1, 0, () => v.Pan, x => v.Pan = x, SynthWindow.PanText, bipolar: true, accent: color),
            K("Max len", 0.05, 10, 4, () => v.MaxLength, x => v.MaxLength = x, SynthWindow.Seconds, curve: 2, accent: color),
            K("Versions", 1, 8, 1, () => v.Variations, x => { v.Variations = (int)x; RefreshPads(); UpdateStatusHint(); }, x => $"{x:0}", step: 1, accent: color),
            K("Spread", 0, 1, 0.3, () => v.Spread, x => v.Spread = x, SynthWindow.Percent, accent: color));
        var versions = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < Math.Clamp(v.Variations, 1, 16); i++)
        {
            int variation = i;
            var b = SynthWindow.SmallButton($"n {i}", i == 0 ? "The drum exactly as set" : $"Version {i}: slightly different tune, decays and filters (more with Spread)");
            if (i == _previewVariation) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => { _previewVariation = variation; Preview(); RebuildEditor(); };
            versions.Children.Add(b);
        }
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(knobs);
        body.Children.Add(new TextBlock { Text = "Versions are saved as n 0, n 1, … of this drum — alternate them for a less mechanical groove: # n \"<0 1 2>\"", FontSize = 11, Foreground = SynthWindow.MutedBrush, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(versions);
        return SynthWindow.Panel("OUTPUT & VERSIONS", body, color);
    }

    private void RandomiseVoice()
    {
        DrumKits.Randomise(Voice, new Random());
        RefreshPads();
        RebuildEditor();
        Preview();
    }

    private Control KitOptions()
    {
        var normalize = new CheckBox { Content = "Normalise every sample", IsChecked = _kit.Normalize };
        ToolTip.SetTip(normalize, "Make every sample peak at -1 dB (times the drum's Level), so the kit is consistently loud");
        normalize.IsCheckedChanged += (_, _) => { _kit.Normalize = normalize.IsChecked == true; Commit(); };
        var rates = new List<string> { "44100", "48000" };
        var rate = new ComboBox { ItemsSource = rates, SelectedItem = _kit.SampleRate.ToString(CultureInfo.InvariantCulture), FontSize = 12 };
        rate.SelectionChanged += (_, _) => { if (rate.SelectedItem is string s) _kit.SampleRate = int.Parse(s, CultureInfo.InvariantCulture); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(normalize);
        row.Children.Add(Labelled("Hz", rate));
        return row;
    }

    private Knob K(string label, double min, double max, double def, Func<double> get, Action<double> set,
        Func<double, string> format, bool log = false, double curve = 1, double step = 0, bool bipolar = false, Color? accent = null)
    {
        var knob = new Knob
        {
            Label = label, Minimum = min, Maximum = max, Default = def, Log = log, Curve = curve, Step = step,
            Bipolar = bipolar, Format = format, Accent = accent ?? Color.Parse("#4ECDC4"),
        };
        knob.Value = get();
        knob.ValueChanged += x => { set(x); Changed(); };
        knob.Committed += Commit;
        return knob;
    }

    private static Control Labelled(string label, Control control) => SynthWindow.Labelled(label, control);
    // ------------------------------------------------------------------ playing

    private void Commit()
    {
        if (_autoPreview.IsChecked != true) return;
        _autoPreviewTimer.Stop();
        _autoPreviewTimer.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; Preview(); }
        else if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; PlayGroove(); }
        else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; _ = SaveAsync(); }
        else if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; RandomiseVoice(); }
    }

    private async void Preview()
    {
        if (_kit.Voices.Count == 0) return;
        int version = ++_renderVersion;
        var voice = Voice.Clone();
        int variation = Math.Min(_previewVariation, Math.Max(0, voice.Variations - 1));
        int sr = _kit.SampleRate;
        bool normalize = _kit.Normalize;
        var rendered = await Task.Run(() => DrumEngine.Render(voice, variation, sr, normalize));
        if (version != _renderVersion) return;
        _wave.Note = rendered;
        _wave.InvalidateVisual();
        _player.Play(rendered);
        if (_player.Error is { } err) SetStatus(err);
    }

    /// <summary>Two bars at 126 bpm with the kit's bd, sd/cp, hh and oh (whichever exist).</summary>
    private async void PlayGroove()
    {
        int version = ++_renderVersion;
        var kit = _kit.Clone();
        var groove = await Task.Run(() => RenderGroove(kit));
        if (version != _renderVersion || groove is null) return;
        _wave.Note = groove;
        _wave.InvalidateVisual();
        _player.Play(groove);
        if (_player.Error is { } err) SetStatus(err);
    }

    internal static RenderedNote? RenderGroove(DrumKit kit)
    {
        DrumVoice? Find(params string[] names) =>
            names.Select(n => kit.Voices.FirstOrDefault(v => v.Enabled && v.Abbreviation == n)).FirstOrDefault(v => v is not null);
        var kick = Find("bd") ?? kit.ActiveVoices.FirstOrDefault(v => v.Model == DrumModel.Kick);
        var snare = Find("cp", "sd");
        var hat = Find("hh") ?? kit.ActiveVoices.FirstOrDefault(v => v.Model == DrumModel.ClosedHat);
        var open = Find("oh") ?? kit.ActiveVoices.FirstOrDefault(v => v.Model == DrumModel.OpenHat);
        if (kick is null && snare is null && hat is null && open is null) return null;

        int sr = kit.SampleRate;
        double step = 60.0 / 126 / 4;
        int steps = 32;
        var left = new float[(int)((steps * step + 2) * sr)];
        var right = new float[left.Length];
        var cache = new Dictionary<(DrumVoice, int), RenderedNote>();
        void Hit(DrumVoice? voice, int at, double gain, int variation = 0)
        {
            if (voice is null) return;
            variation %= Math.Max(1, voice.Variations);
            if (!cache.TryGetValue((voice, variation), out var note))
                cache[(voice, variation)] = note = DrumEngine.Render(voice, variation, sr, kit.Normalize);
            int start = (int)(at * step * sr);
            for (int i = 0; i < note.Left.Length && start + i < left.Length; i++)
            {
                left[start + i] += (float)(note.Left[i] * gain);
                right[start + i] += (float)(note.Right[i] * gain);
            }
        }
        for (int s = 0; s < steps; s++)
        {
            int beat = s % 16;
            if (beat % 4 == 0) Hit(kick, s, 0.9);
            if (beat is 4 or 12) Hit(snare, s, 0.7);
            if (beat % 4 == 2) Hit(open ?? hat, s, 0.45, s / 4);
            else if (beat % 2 == 1) Hit(hat, s, 0.35, s);
        }
        float peak = 0;
        for (int i = 0; i < left.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        if (peak > 0.95f)
            for (int i = 0; i < left.Length; i++) { left[i] *= 0.95f / peak; right[i] *= 0.95f / peak; }
        return new RenderedNote(60, left, right, sr);
    }

    // ------------------------------------------------------------------ kits

    private static string UserKitFolder => Path.Combine(AppSettings.DefaultDirectory, "drum-kits");

    private void RefreshKitList()
    {
        var items = DrumKits.All.Select(k => k.Name).ToList();
        if (Directory.Exists(UserKitFolder))
        {
            var mine = Directory.EnumerateFiles(UserKitFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n).ToList();
            if (mine.Count > 0)
            {
                items.Add("— my kits —");
                items.AddRange(mine.Select(n => "★ " + n));
            }
        }
        _kits.ItemsSource = items;
    }

    private void LoadKit(string item)
    {
        if (item.StartsWith("★ ", StringComparison.Ordinal))
        {
            string path = Path.Combine(UserKitFolder, item[2..] + ".json");
            try { _kit = DrumKit.FromJson(File.ReadAllText(path)); }
            catch (Exception e) { SetStatus($"Could not load {path}: {e.Message}"); return; }
        }
        else _kit = DrumKits.Create(item);
        _selected = 0;
        _previewVariation = 0;
        RebuildAll();
        PlayGroove();
    }

    private DrumKit LoadLastKit()
    {
        if (_settings.DrumKitJson is { Length: > 0 } json)
            try
            {
                var kit = DrumKit.FromJson(json);
                if (kit.Voices.Count > 0) return kit;
            }
            catch (Exception) { }
        var fresh = DrumKits.Create("Punchy machine");
        fresh.Name = "mykit";
        return fresh;
    }

    // ------------------------------------------------------------------ saving

    private string SamplesRoot => _settings.SamplesFolder is { Length: > 0 } f ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "samples");

    private void UpdateFolderLabel() => _folderLabel.Text = $"Samples folder: {SamplesRoot}";

    private async Task ChooseSamplesFolderAsync()
    {
        IStorageFolder? start = null;
        try { if (Directory.Exists(SamplesRoot)) start = await StorageProvider.TryGetFolderFromPathAsync(SamplesRoot); } catch (Exception) { }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for your sample folders", SuggestedStartLocation = start });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _settings.SamplesFolder = path;
            UpdateFolderLabel();
        }
    }

    private void UpdateStatusHint()
    {
        var problems = _kit.Validate();
        if (problems.Count > 0) { SetStatus(problems[0]); return; }
        bool combined = _combined.IsChecked == true;
        var plan = DrumExport.Plan(_kit, combined);
        int sounds = plan.Select(p => p.Folder).Distinct().Count();
        var voices = _kit.ActiveVoices.ToList();
        string example = $"s \"{_kit.SoundName(voices[0])}\"" + (voices.Count > 1 ? $", s \"{_kit.SoundName(voices[1])}\"" : "") + " …";
        SetStatus($"Will save {plan.Count} samples as {sounds} sounds: {example}" + (combined ? $"  and the whole kit as s \"{_kit.Name}\" # n 0..{voices.Count - 1}" : ""));
    }

    /// <summary>The status line, for the self-test.</summary>
    internal string StatusText => _status.Text ?? "";

    private void SetStatus(string text, params Control[] actions)
    {
        _status.Text = text;
        _statusActions.Children.Clear();
        foreach (var a in actions) _statusActions.Children.Add(a);
    }

    internal async Task SaveAsync()
    {
        var problems = _kit.Validate();
        if (problems.Count > 0)
        {
            await Dialogs.ShowMessage(this, "Can't save the kit", string.Join("\n", problems));
            return;
        }
        bool combined = _combined.IsChecked == true;
        var names = DrumExport.Folders(_kit, combined);
        var existing = names.Where(n => Directory.Exists(Path.Combine(SamplesRoot, n)) && Directory.EnumerateFiles(Path.Combine(SamplesRoot, n), "*.wav").Any()).ToList();
        if (existing.Count > 0 &&
            !await Dialogs.Confirm(this, "Replace sounds?", $"These sounds already have samples in {SamplesRoot}:\n{string.Join(", ", existing)}\n\nReplace them with this kit?", "Replace"))
            return;
        var library = SampleLibrary.Scan().Where(s => !s.IsSynth).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clashes = names.Where(n => library.Contains(n) && !Directory.Exists(Path.Combine(SamplesRoot, n))).ToList();
        if (clashes.Count > 0 &&
            !await Dialogs.Confirm(this, "Names already used", $"SuperDirt already has sounds called {string.Join(", ", clashes)}. Saving another with the same name makes one unreachable — choose another kit name, or save anyway.", "Save anyway"))
            return;

        var kit = _kit.Clone();
        string root = SamplesRoot;
        int files = DrumExport.Plan(kit, combined).Count;
        SetStatus($"Rendering {files} samples…");
        List<string> folders;
        try
        {
            folders = await Task.Run(() => DrumExport.Write(root, kit, combined));
            Directory.CreateDirectory(UserKitFolder);
            await File.WriteAllTextAsync(Path.Combine(UserKitFolder, kit.Name + ".json"), kit.ToJson());
        }
        catch (Exception e)
        {
            SetStatus($"Saving failed: {e.Message}");
            return;
        }
        RefreshKitList();
        _samplesChanged();
        _log(OutputKind.Info, $"Tidepool Drums: saved {files} samples in {folders.Count} sounds to {root}");

        string example = DrumExport.Example(kit, combined);
        string saved = $"Saved {files} samples ({folders.Count} sounds).";
        var reporter = new SaveReporter(_services, _restartSuperDirt, _samplesChanged, _log);
        var (messages, actions) = await reporter.ReportAsync(this, folders, root, _restartOnSave.IsChecked == true, text => SetStatus($"{saved}  {text}"));
        messages.Insert(0, saved);
        var copy = SynthWindow.SmallButton("Copy example code", example);
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(example);
            copy.Content = "Copied ✓";
        };
        actions.Insert(0, copy);
        SetStatus(string.Join("  ", messages), actions.ToArray());
    }
}
