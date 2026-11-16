using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TidalStudio.App.Services;
using TidalStudio.Core.Completion;
using TidalStudio.Core.Settings;
using TidalStudio.Core.SuperCollider;
using TidalStudio.Core.Synth;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Synth;

/// <summary>
/// Tidepool: a wavetable synth that renders its sound to WAV samples in a SuperDirt sample folder, so the sound
/// can be played from Tidal with s "name" (and n to pick the rendered note).
/// </summary>
public sealed class SynthWindow : Window
{
    private static readonly Color[] OscColors = { Color.Parse("#4ECDC4"), Color.Parse("#A78BFA"), Color.Parse("#FFD166") };
    private static readonly Color FilterColor = Color.Parse("#FF6B6B");
    private static readonly Color EnvColor = Color.Parse("#60A5FA");
    private static readonly Color ModColor = Color.Parse("#F472B6");
    private static readonly Color FxColor = Color.Parse("#34D399");
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#1A1D24"));
    private static readonly IBrush BorderBrush2 = new SolidColorBrush(Color.Parse("#2A2F3A"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8A93A6"));

    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly Action _samplesChanged;
    private readonly Action<OutputKind, string> _log;
    private readonly PreviewPlayer _player = new();
    private readonly List<Action> _refreshers = new();
    private readonly DispatcherTimer _autoPreviewTimer;

    private SynthPatch _patch;
    private int _renderVersion;
    private int? _lastKey;

    private readonly ContentControl _sections = new();
    private readonly ComboBox _presets = new() { MinWidth = 170, PlaceholderText = "Choose a preset…" };
    private readonly TextBox _name = new() { Width = 130 };
    private readonly TextBox _notes = new() { Width = 150 };
    private readonly ComboBox _noteSets = new() { MinWidth = 120 };
    private readonly CheckBox _autoPreview = new() { Content = "Auto-preview", IsChecked = true };
    private readonly WaveformView _wave = new() { Height = 70 };
    private readonly KeyboardView _keys = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _folderLabel = new() { FontSize = 11, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private static readonly (string Label, string Notes)[] NoteSets =
    {
        ("One note", "c5"),
        ("Octaves c2–c7", "c2 c3 c4 c5 c6 c7"),
        ("Bass c1–c4", "c1 c2 c3 c4"),
        ("Chromatic", "c5 cs5 d5 ds5 e5 f5 fs5 g5 gs5 a5 as5 b5"),
        ("Major chord", "c5 e5 g5"),
        ("Custom", ""),
    };

    private readonly Func<string, Task<(bool Ok, string Message)>> _restartSuperDirt;
    private readonly CheckBox _restartOnSave = new() { Content = "Restart SuperDirt after saving", FontSize = 12 };

    public SynthWindow(AppServices services, Action samplesChanged, Action<OutputKind, string> log,
        Func<string, Task<(bool Ok, string Message)>> restartSuperDirt)
    {
        _services = services;
        _settings = services.Settings;
        _samplesChanged = samplesChanged;
        _log = log;
        _restartSuperDirt = restartSuperDirt;
        _restartOnSave.IsChecked = _settings.SynthRestartOnSave;
        _restartOnSave.IsCheckedChanged += (_, _) => _settings.SynthRestartOnSave = _restartOnSave.IsChecked == true;
        ToolTip.SetTip(_restartOnSave,
            "After saving, make SuperDirt load the new sound. A SuperDirt that TidalStudio started just loads it; " +
            "one started elsewhere (e.g. the SuperCollider IDE) is restarted, and TidalStudio runs it from then on.");
        _patch = LoadLastPatch();

        Title = "Tidepool — sample synth";
        Width = 1240;
        Height = 860;
        MinWidth = 900;
        MinHeight = 600;
        Background = (IBrush?)Application.Current?.FindResource("TsWindowBackground") ?? Brushes.Black;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TidalStudio/Assets/TidalStudio.ico"))); }
        catch (Exception) { }

        _autoPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _autoPreviewTimer.Tick += (_, _) => { _autoPreviewTimer.Stop(); Preview(); };

        Content = BuildLayout();
        RebuildSections();
        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            SaveLastPatch();
            _player.Dispose();
        };
    }

    // ------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        // Top bar: preset, name, notes, preview / save.
        RefreshPresetList();
        _presets.SelectionChanged += (_, _) =>
        {
            if (_presets.SelectedItem is not string item || item.StartsWith('—')) return;
            LoadPreset(item);
        };
        var random = SmallButton("Randomise", "Random sound from the current one's tables (Ctrl+R)");
        random.Click += (_, _) => Randomise();

        _name.Text = _patch.Name;
        _name.TextChanged += (_, _) => { _patch.Name = _name.Text?.Trim() ?? ""; UpdateStatusHint(); };
        ToolTip.SetTip(_name, "Sound name in Tidal: s \"name\". Lowercase letters, digits, - and _.");

        _noteSets.ItemsSource = NoteSets.Select(s => s.Label).ToList();
        _noteSets.SelectionChanged += (_, _) =>
        {
            if (_noteSets.SelectedIndex is >= 0 and < 5) _notes.Text = NoteSets[_noteSets.SelectedIndex].Notes;
        };
        _notes.Text = _patch.Notes;
        _notes.TextChanged += (_, _) =>
        {
            _patch.Notes = _notes.Text ?? "";
            int set = Array.FindIndex(NoteSets, s => s.Notes == _patch.Notes);
            _noteSets.SelectedIndex = set >= 0 ? set : NoteSets.Length - 1;
            UpdateKeyboardMarks();
            UpdateStatusHint();
        };
        ToolTip.SetTip(_notes, "Notes to render, one sample each, in Tidal note names (c5 = note 0). n picks them in this order.");

        var preview = AccentButton("▶ Preview", "Play the first note (Space)");
        preview.Click += (_, _) => Preview();
        var save = AccentButton("💾 Save to SuperDirt", "Render every note to WAV files in your samples folder (Ctrl+S)");
        save.Click += async (_, _) => await SaveAsync();

        var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 6) };
        top.Children.Add(Labelled("Preset", _presets));
        top.Children.Add(random);
        top.Children.Add(Gap(16));
        top.Children.Add(Labelled("Name", _name));
        top.Children.Add(Labelled("Notes", _noteSets));
        top.Children.Add(_notes);
        top.Children.Add(Gap(16));
        top.Children.Add(preview);
        top.Children.Add(_autoPreview);
        top.Children.Add(Gap(8));
        top.Children.Add(save);
        top.Children.Add(_restartOnSave);

        // Bottom: waveform + keyboard, then status.
        _keys.NotePressed += midi => { _lastKey = midi; Preview(midi); };
        var octaveDown = SmallButton("◀", "Keyboard octave down");
        octaveDown.Click += (_, _) => { _keys.LowestC = Math.Max(12, _keys.LowestC - 12); _keys.InvalidateVisual(); };
        var octaveUp = SmallButton("▶", "Keyboard octave up");
        octaveUp.Click += (_, _) => { _keys.LowestC = Math.Min(96, _keys.LowestC + 12); _keys.InvalidateVisual(); };
        var keyRow = new DockPanel();
        DockPanel.SetDock(octaveDown, Dock.Left);
        DockPanel.SetDock(octaveUp, Dock.Right);
        keyRow.Children.Add(octaveDown);
        keyRow.Children.Add(octaveUp);
        keyRow.Children.Add(_keys);
        var bottomGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(12, 4, 12, 4) };
        Grid.SetColumn(_wave, 0);
        Grid.SetColumn(keyRow, 2);
        bottomGrid.Children.Add(_wave);
        bottomGrid.Children.Add(keyRow);

        var changeFolder = SmallButton("Change…", "Choose the folder your sample folders are saved in");
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
        UpdateStatusHint();

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        var bottom = new StackPanel();
        bottom.Children.Add(bottomGrid);
        bottom.Children.Add(new Border { BorderBrush = BorderBrush2, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusBar });
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _sections, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return root;
    }

    /// <summary>Builds every section from the current patch (after loading a preset, all controls change).</summary>
    private void RebuildSections()
    {
        _refreshers.Clear();
        var rows = new StackPanel { Margin = new Thickness(12, 0, 12, 0), Spacing = 10 };

        var oscRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*") };
        for (int i = 0; i < 3; i++)
        {
            var section = OscSection(i);
            Grid.SetColumn(section, i * 2);
            oscRow.Children.Add(section);
        }
        rows.Children.Add(oscRow);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("1.3*,10,*,10,*,10,1.2*") };
        Place(row2, FilterSection(), 0);
        Place(row2, EnvSection("FILTER ENVELOPE", () => _patch.FilterEnvelope, FilterColor), 2);
        Place(row2, EnvSection("AMP ENVELOPE", () => _patch.AmpEnvelope, EnvColor), 4);
        Place(row2, ModEnvSection(), 6);
        rows.Children.Add(row2);

        var row3 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,1.6*,10,*") };
        Place(row3, LfoSection("LFO 1", () => _patch.Lfo1), 0);
        Place(row3, LfoSection("LFO 2", () => _patch.Lfo2), 2);
        Place(row3, FxSection(), 4);
        Place(row3, OutputSection(), 6);
        rows.Children.Add(row3);

        _sections.Content = rows;
        _name.Text = _patch.Name;
        _notes.Text = _patch.Notes;
        UpdateKeyboardMarks();
    }

    private static void Place(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private Control OscSection(int index)
    {
        var osc = _patch.Oscillators[index];
        var color = OscColors[index];
        var view = new WavetableView(() => osc, color);
        _refreshers.Add(view.InvalidateVisual);

        var enabled = new CheckBox { IsChecked = osc.Enabled, Content = $"OSC {index + 1}", FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color) };
        enabled.IsCheckedChanged += (_, _) => { osc.Enabled = enabled.IsChecked == true; Changed(); Commit(); };
        var table = Combo(Wavetables.Available.Select(t => t.Name).ToList(), osc.Table, v => osc.Table = v,
            Wavetables.Available.ToDictionary(t => t.Name, t => t.Description));

        var row1 = KnobRow(
            K("Position", 0, 1, 0, () => osc.Position, v => osc.Position = v, Percent, accent: color),
            K("Level", 0, 1, 0.8, () => osc.Level, v => osc.Level = v, Percent, accent: color),
            K("Pan", -1, 1, 0, () => osc.Pan, v => osc.Pan = v, PanText, bipolar: true, accent: color),
            K("Octave", -3, 3, 0, () => osc.Octave, v => osc.Octave = (int)v, v => $"{v:+0;-0;0} oct", step: 1, bipolar: true, accent: color),
            K("Semi", -12, 12, 0, () => osc.Semi, v => osc.Semi = (int)v, v => $"{v:+0;-0;0} st", step: 1, bipolar: true, accent: color),
            K("Fine", -100, 100, 0, () => osc.Fine, v => osc.Fine = v, v => $"{v:+0;-0;0} ct", bipolar: true, accent: color));
        var warp = Combo(Enum.GetNames<WarpMode>().ToList(), osc.Warp.ToString(), v => osc.Warp = Enum.Parse<WarpMode>(v), new()
        {
            ["Bend"] = "Phase distortion: squeezes the wave, brighter",
            ["Sync"] = "Hard sync: the wave restarts faster than the note",
            ["Fold"] = "Wavefolder: folds loud parts back, adds harmonics",
            ["Crush"] = "Bit crusher: fewer amplitude steps, lo-fi",
        });
        var row2 = KnobRow(
            K("Unison", 1, 9, 1, () => osc.Unison, v => osc.Unison = (int)v, v => $"{v:0} voice{(v > 1 ? "s" : "")}", step: 1, accent: color),
            K("Detune", 0, 100, 15, () => osc.Detune, v => osc.Detune = v, v => $"{v:0} ct", accent: color),
            K("Spread", 0, 1, 0.6, () => osc.Spread, v => osc.Spread = v, Percent, accent: color),
            Labelled("Warp", warp, vertical: true),
            K("Amount", 0, 1, 0, () => osc.WarpAmount, v => osc.WarpAmount = v, Percent, accent: color));

        var header = new DockPanel();
        DockPanel.SetDock(table, Dock.Right);
        header.Children.Add(table);
        header.Children.Add(enabled);
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(header);
        body.Children.Add(view);
        body.Children.Add(row1);
        body.Children.Add(row2);
        return Panel(null, body, color);
    }

    private Control FilterSection()
    {
        var f = _patch.Filter;
        var mode = Combo(Enum.GetNames<FilterMode>().ToList(), f.Mode.ToString(), v => f.Mode = Enum.Parse<FilterMode>(v));
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(Labelled("Type", mode));
        body.Children.Add(KnobRow(
            K("Cutoff", 20, 20000, 6000, () => f.Cutoff, v => f.Cutoff = v, Hz, log: true, accent: FilterColor),
            K("Reso", 0, 1, 0.15, () => f.Resonance, v => f.Resonance = v, Percent, accent: FilterColor),
            K("Drive", 0, 1, 0, () => f.Drive, v => f.Drive = v, Percent, accent: FilterColor)));
        body.Children.Add(KnobRow(
            K("Env", -8, 8, 0, () => f.EnvAmount, v => f.EnvAmount = v, v => $"{v:+0.0;-0.0;0} oct", bipolar: true, accent: FilterColor),
            K("Key", 0, 1, 0.5, () => f.KeyTrack, v => f.KeyTrack = v, Percent, accent: FilterColor)));
        return Panel("FILTER", body, FilterColor);
    }

    private Control EnvSection(string title, Func<EnvelopeSettings> env, Color color)
    {
        var view = new EnvelopeView(env, () => _patch.Hold, color);
        _refreshers.Add(view.InvalidateVisual);
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(view);
        body.Children.Add(AdsrKnobs(env, color));
        return Panel(title, body, color);
    }

    private Control AdsrKnobs(Func<EnvelopeSettings> env, Color color) => KnobRow(
        K("Attack", 0, 10, 0.005, () => env().Attack, v => env().Attack = v, Seconds, curve: 3.5, accent: color),
        K("Decay", 0, 10, 0.3, () => env().Decay, v => env().Decay = v, Seconds, curve: 3.5, accent: color),
        K("Sustain", 0, 1, 0.7, () => env().Sustain, v => env().Sustain = v, Percent, accent: color),
        K("Release", 0, 10, 0.3, () => env().Release, v => env().Release = v, Seconds, curve: 3.5, accent: color));

    private Control ModEnvSection()
    {
        var m = _patch.ModEnvelope;
        var target = Combo(Enum.GetNames<ModTarget>().ToList(), m.Target.ToString(), v => m.Target = Enum.Parse<ModTarget>(v), TargetTips);
        var view = new EnvelopeView(() => m.Envelope, () => _patch.Hold, ModColor);
        _refreshers.Add(view.InvalidateVisual);
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(Labelled("To", target));
        top.Children.Add(K("Amount", -1, 1, 0, () => m.Amount, v => m.Amount = v, v => $"{v * 100:+0;-0;0}%", bipolar: true, accent: ModColor));
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(top);
        body.Children.Add(view);
        body.Children.Add(AdsrKnobs(() => m.Envelope, ModColor));
        return Panel("MOD ENVELOPE", body, ModColor);
    }

    private Control LfoSection(string title, Func<LfoSettings> lfo)
    {
        var l = lfo();
        var shape = Combo(Enum.GetNames<LfoShape>().ToList(), l.Shape.ToString(), v => l.Shape = Enum.Parse<LfoShape>(v));
        var target = Combo(Enum.GetNames<ModTarget>().ToList(), l.Target.ToString(), v => l.Target = Enum.Parse<ModTarget>(v), TargetTips);
        var body = new StackPanel { Spacing = 6 };
        var combos = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        combos.Children.Add(Labelled("Shape", shape));
        combos.Children.Add(Labelled("To", target));
        body.Children.Add(combos);
        body.Children.Add(KnobRow(
            K("Rate", 0.05, 30, 3, () => l.Rate, v => l.Rate = v, v => $"{v:0.##} Hz", log: true, accent: ModColor),
            K("Amount", 0, 1, 0, () => l.Amount, v => l.Amount = v, Percent, curve: 2, accent: ModColor),
            K("Fade in", 0, 5, 0, () => l.FadeIn, v => l.FadeIn = v, Seconds, curve: 2, accent: ModColor)));
        return Panel(title, body, ModColor);
    }

    private Control FxSection()
    {
        var fx = _patch.Fx;
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(KnobRow(
            K("Drive", 0, 1, 0, () => fx.Drive, v => fx.Drive = v, Percent, accent: FxColor),
            K("Chorus", 0, 1, 0, () => fx.ChorusMix, v => fx.ChorusMix = v, Percent, accent: FxColor),
            K("Delay", 0, 1, 0, () => fx.DelayMix, v => fx.DelayMix = v, Percent, accent: FxColor),
            K("Time", 0.02, 1.5, 0.25, () => fx.DelayTime, v => fx.DelayTime = v, Seconds, log: true, accent: FxColor),
            K("Feedback", 0, 0.95, 0.35, () => fx.DelayFeedback, v => fx.DelayFeedback = v, Percent, accent: FxColor),
            K("Reverb", 0, 1, 0, () => fx.ReverbMix, v => fx.ReverbMix = v, Percent, accent: FxColor),
            K("Size", 0, 1, 0.7, () => fx.ReverbSize, v => fx.ReverbSize = v, Percent, accent: FxColor)));
        return Panel("EFFECTS", body, FxColor);
    }

    private Control OutputSection()
    {
        var normalize = new CheckBox { Content = "Normalise", IsChecked = _patch.Normalize };
        ToolTip.SetTip(normalize, "Make every sample peak at -1 dB, so all notes are equally loud");
        normalize.IsCheckedChanged += (_, _) => { _patch.Normalize = normalize.IsChecked == true; Commit(); };
        var rate = Combo(new List<string> { "44100", "48000" }, _patch.SampleRate.ToString(CultureInfo.InvariantCulture),
            v => _patch.SampleRate = int.Parse(v, CultureInfo.InvariantCulture));
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(KnobRow(
            K("Volume", 0, 1, 0.8, () => _patch.Volume, v => _patch.Volume = v, Percent, accent: EnvColor),
            K("Hold", 0.05, 10, 1, () => _patch.Hold, v => _patch.Hold = v, Seconds, curve: 2, accent: EnvColor),
            K("Max len", 0.2, 20, 6, () => _patch.MaxLength, v => _patch.MaxLength = v, Seconds, curve: 2, accent: EnvColor)));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(normalize);
        row.Children.Add(Labelled("Hz", rate));
        body.Children.Add(row);
        return Panel("OUTPUT", body, EnvColor);
    }

    private static readonly Dictionary<string, string> TargetTips = new()
    {
        ["Pitch"] = "Envelope: up to ±24 semitones (kick drops, risers). LFO: up to ±12 (vibrato with small amounts)",
        ["Position"] = "Morph through the wavetables",
        ["Cutoff"] = "Filter cutoff (wobbles, sweeps)",
        ["Amp"] = "Volume (tremolo)",
        ["Pan"] = "Stereo position (auto-pan)",
        ["Warp"] = "Amount of every oscillator's warp",
    };

    // ------------------------------------------------------------------ control helpers

    private static string Percent(double v) => $"{v * 100:0}%";
    private static string Hz(double v) => v >= 1000 ? $"{v / 1000:0.0} kHz" : $"{v:0} Hz";
    private static string Seconds(double v) => v < 1 ? $"{v * 1000:0} ms" : $"{v:0.00} s";
    private static string PanText(double v) => Math.Abs(v) < 0.01 ? "C" : v < 0 ? $"L{-v * 100:0}" : $"R{v * 100:0}";

    private Knob K(string label, double min, double max, double def, Func<double> get, Action<double> set,
        Func<double, string>? format = null, bool log = false, double curve = 1, double step = 0, bool bipolar = false, Color? accent = null)
    {
        var knob = new Knob
        {
            Label = label, Minimum = min, Maximum = max, Default = def, Log = log, Curve = curve, Step = step,
            Bipolar = bipolar, Format = format ?? (v => v.ToString("0.##", CultureInfo.InvariantCulture)),
            Accent = accent ?? Color.Parse("#4ECDC4"),
        };
        knob.Value = get();
        knob.ValueChanged += v => { set(v); Changed(); };
        knob.Committed += Commit;
        return knob;
    }

    private ComboBox Combo(List<string> items, string selected, Action<string> set, Dictionary<string, string>? tips = null)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedItem = items.Contains(selected) ? selected : items[0], MinWidth = 96, FontSize = 12 };
        if (tips is not null && tips.TryGetValue(selected, out var tip)) ToolTip.SetTip(combo, tip);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string v) return;
            set(v);
            ToolTip.SetTip(combo, tips is not null && tips.TryGetValue(v, out var t) ? t : null);
            Changed();
            Commit();
        };
        return combo;
    }

    private static Control KnobRow(params Control[] children)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children)
        {
            c.Margin = new Thickness(0, 0, 4, 0);
            row.Children.Add(c);
        }
        return row;
    }

    private static Control Labelled(string label, Control control, bool vertical = false)
    {
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = vertical ? 2 : 6,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(text);
        panel.Children.Add(control);
        return panel;
    }

    private static Control Gap(double width) => new Border { Width = width };

    private static Border Panel(string? title, Control body, Color accent)
    {
        var content = new StackPanel { Spacing = 6 };
        if (title is not null)
            content.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(accent) });
        content.Children.Add(body);
        return new Border
        {
            Background = PanelBrush,
            // Accent-coloured top edge fading into the normal border.
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(accent, 0), new GradientStop(Color.Parse("#2A2F3A"), 0.04) },
            },
            BorderThickness = new Thickness(1, 2, 1, 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = content,
        };
    }

    private static Button SmallButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static Button AccentButton(string text, string tip)
    {
        var b = SmallButton(text, tip);
        b.FontWeight = FontWeight.SemiBold;
        b.Padding = new Thickness(12, 5);
        return b;
    }

    // ------------------------------------------------------------------ behaviour

    private void Changed()
    {
        foreach (var refresh in _refreshers) refresh();
    }

    /// <summary>A gesture finished: re-render for auto-preview.</summary>
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
        else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; _ = SaveAsync(); }
        else if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; Randomise(); }
    }

    private int PreviewNote() =>
        _lastKey ?? SynthPatch.ParseNotes(_patch.Notes).DefaultIfEmpty(60).First();

    private async void Preview(int? midi = null)
    {
        int note = midi ?? PreviewNote();
        int version = ++_renderVersion;
        var patch = _patch.Clone();
        var rendered = await Task.Run(() => SynthEngine.Render(patch, note));
        if (version != _renderVersion) return; // a newer preview is on its way
        _wave.Note = rendered;
        _wave.InvalidateVisual();
        _player.Play(rendered);
        if (_player.Error is { } err) SetStatus(err);
    }

    private void UpdateKeyboardMarks()
    {
        _keys.Marked = SynthPatch.ParseNotes(_patch.Notes).ToHashSet();
        _keys.InvalidateVisual();
    }

    private void Randomise()
    {
        var r = new Random();
        var tables = Wavetables.Available.Select(t => t.Name).Where(n => n != "Noise").ToList();
        for (int i = 0; i < 3; i++)
        {
            var o = _patch.Oscillators[i];
            o.Enabled = i == 0 || r.NextDouble() < 0.5;
            o.Table = tables[r.Next(tables.Count)];
            o.Position = r.NextDouble();
            o.Octave = i == 0 ? 0 : r.Next(-1, 2);
            o.Semi = r.NextDouble() < 0.2 ? new[] { 7, 5, 12, -5 }[r.Next(4)] : 0;
            o.Fine = r.NextDouble() * 14 - 7;
            o.Level = 0.5 + r.NextDouble() * 0.4;
            o.Unison = r.NextDouble() < 0.5 ? 1 : r.Next(2, 8);
            o.Detune = 5 + r.NextDouble() * 40;
            o.Warp = r.NextDouble() < 0.5 ? WarpMode.None : (WarpMode)r.Next(1, 5);
            o.WarpAmount = r.NextDouble() * 0.6;
        }
        _patch.Filter.Mode = r.NextDouble() < 0.8 ? FilterMode.LowPass : (FilterMode)r.Next(2, 5);
        _patch.Filter.Cutoff = 200 * Math.Pow(50, r.NextDouble());
        _patch.Filter.Resonance = r.NextDouble() * 0.6;
        _patch.Filter.EnvAmount = r.NextDouble() * 5;
        _patch.FilterEnvelope = new() { Attack = r.NextDouble() * 0.1, Decay = 0.1 + r.NextDouble(), Sustain = r.NextDouble() * 0.6, Release = 0.1 + r.NextDouble() };
        _patch.AmpEnvelope = new() { Attack = Math.Pow(r.NextDouble(), 3) * 1.5, Decay = 0.1 + r.NextDouble() * 1.5, Sustain = r.NextDouble(), Release = 0.05 + r.NextDouble() * 1.2 };
        _patch.ModEnvelope.Target = r.NextDouble() < 0.5 ? ModTarget.Position : ModTarget.None;
        _patch.ModEnvelope.Amount = r.NextDouble() * 2 - 1;
        _patch.Lfo1 = new() { Shape = (LfoShape)r.Next(5), Rate = 0.1 * Math.Pow(80, r.NextDouble()), Amount = r.NextDouble() * 0.6, Target = r.NextDouble() < 0.6 ? (ModTarget)r.Next(1, 7) : ModTarget.None };
        if (_patch.Lfo1.Target == ModTarget.Pitch) _patch.Lfo1.Amount *= 0.05;
        _patch.Fx = new() { ChorusMix = r.NextDouble() * 0.5, DelayMix = r.NextDouble() < 0.4 ? r.NextDouble() * 0.4 : 0, DelayTime = 0.125 * r.Next(1, 4), ReverbMix = r.NextDouble() * 0.5, ReverbSize = r.NextDouble() };
        RebuildSections();
        Preview();
    }

    // ------------------------------------------------------------------ presets

    private static string UserPresetFolder => Path.Combine(AppSettings.DefaultDirectory, "synth-presets");

    private void RefreshPresetList()
    {
        var items = SynthPresets.All.Select(p => p.Name).ToList();
        if (Directory.Exists(UserPresetFolder))
        {
            var mine = Directory.EnumerateFiles(UserPresetFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n).ToList();
            if (mine.Count > 0)
            {
                items.Add("— my sounds —");
                items.AddRange(mine.Select(n => "★ " + n));
            }
        }
        _presets.ItemsSource = items;
    }

    private void LoadPreset(string item)
    {
        if (item.StartsWith("★ ", StringComparison.Ordinal))
        {
            string path = Path.Combine(UserPresetFolder, item[2..] + ".json");
            try { _patch = SynthPatch.FromJson(File.ReadAllText(path)); }
            catch (Exception e) { SetStatus($"Could not load {path}: {e.Message}"); return; }
        }
        else _patch = SynthPresets.Create(item);
        _lastKey = null;
        RebuildSections();
        Preview();
    }

    private SynthPatch LoadLastPatch()
    {
        if (_settings.SynthPatchJson is { Length: > 0 } json)
            try { return SynthPatch.FromJson(json); } catch (Exception) { }
        return SynthPresets.Create("Supersaw lead");
    }

    private void SaveLastPatch()
    {
        _settings.SynthPatchJson = _patch.ToJson();
    }

    // ------------------------------------------------------------------ saving to SuperDirt

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
        var notes = SynthPatch.ParseNotes(_patch.Notes);
        string name = _patch.Name;
        if (!SampleExport.IsValidName(name))
            SetStatus($"\"{name}\" can't be used as a sound name: use lowercase letters, digits, - or _, starting with a letter.");
        else if (notes.Count == 0)
            SetStatus("Enter at least one note to render, e.g. c5 (that is note 0 in Tidal).");
        else if (notes.Count == 1)
            SetStatus($"Will save 1 sample: d1 $ s \"{name}\"  (play other pitches with # note 7, # note \"-12\" …)");
        else
            SetStatus($"Will save {notes.Count} samples: d1 $ s \"{name}\" # n \"0 .. {notes.Count - 1}\"  (n 0 = {SynthPatch.NoteName(notes[0])}, n {notes.Count - 1} = {SynthPatch.NoteName(notes[^1])})");
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
        string name = _patch.Name;
        if (!SampleExport.IsValidName(name))
        {
            await Dialogs.ShowMessage(this, "Sound name", $"\"{name}\" can't be used in Tidal. Use lowercase letters, digits, - or _, starting with a letter (for example: mypad, bass-2).");
            return;
        }
        var notes = SynthPatch.ParseNotes(_patch.Notes);
        if (notes.Count == 0)
        {
            await Dialogs.ShowMessage(this, "Notes", "Enter at least one note to render, for example c5, or choose a note set.");
            return;
        }

        string folder = Path.Combine(SamplesRoot, name);
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.wav").Any() &&
            !await Dialogs.Confirm(this, "Replace sound?", $"{folder} already has samples. Replace them with this sound?", "Replace"))
            return;
        // A name that already exists elsewhere (e.g. "bd") would clash in SuperDirt.
        if (SampleLibrary.Scan().Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !s.IsSynth) &&
            !Directory.Exists(folder) &&
            !await Dialogs.Confirm(this, "Name already used", $"SuperDirt already has a sound called \"{name}\". Saving another one with the same name makes one of them unreachable. Save anyway?", "Save anyway"))
            return;

        SetStatus($"Rendering {notes.Count} note{(notes.Count > 1 ? "s" : "")}…");
        var patch = _patch.Clone();
        List<string> paths;
        try
        {
            var rendered = await Task.Run(() => notes.AsParallel().AsOrdered().Select(m => SynthEngine.Render(patch, m)).ToList());
            paths = await Task.Run(() => SampleExport.Write(folder, name, rendered));
            Directory.CreateDirectory(UserPresetFolder);
            await File.WriteAllTextAsync(Path.Combine(UserPresetFolder, name + ".json"), patch.ToJson());
        }
        catch (Exception e)
        {
            SetStatus($"Saving failed: {e.Message}");
            return;
        }
        RefreshPresetList();
        _samplesChanged();
        _log(OutputKind.Info, $"Tidepool: saved {paths.Count} sample{(paths.Count > 1 ? "s" : "")} to {folder}");
        string usage = paths.Count == 1 ? $"d1 $ s \"{name}\"" : $"d1 $ s \"{name}\" # n \"0 .. {paths.Count - 1}\"";
        await ReportSuperDirtAsync(folder, name, $"Saved {paths.Count} sample{(paths.Count > 1 ? "s" : "")} to {folder}.  Try: {usage}");
    }

    /// <summary>After saving: load the sound into SuperDirt now if possible, and say whether it loads at startup.</summary>
    private async Task ReportSuperDirtAsync(string folder, string name, string saved)
    {
        string scPath = folder.Replace('\\', '/');
        string loadCode = $"~dirt.loadSoundFiles(\"{scPath}\");";
        var messages = new List<string> { saved };
        var actions = new List<Control>();

        // Now: TidalStudio's own SuperCollider can be told to load it directly (no interruption). One started
        // elsewhere can't be reached, so restart SuperCollider (if allowed), which also makes it TidalStudio's own.
        if (_services.SuperDirt.IsRunning && _services.SuperDirt.TrySendCode(loadCode))
            messages.Add("Loaded into SuperDirt — you can play it now.");
        else if (_restartOnSave.IsChecked == true)
        {
            SetStatus($"{saved}  Restarting SuperCollider so SuperDirt loads it…");
            var (_, message) = await _restartSuperDirt(folder);
            messages.Add(message);
        }
        else if (await SuperDirtProbe.IsRunningAsync())
        {
            messages.Add("To use it right away, run this in SuperCollider (or restart SuperDirt):");
            var copy = SmallButton("Copy load command", loadCode);
            copy.Click += async (_, _) =>
            {
                if (Clipboard is { } clipboard) await clipboard.SetTextAsync(loadCode);
                copy.Content = "Copied ✓";
            };
            actions.Add(copy);
        }

        // Later: does startup.scd load this folder?
        string? startup = SampleLibrary.StartupScriptPath();
        string script = startup is not null && File.Exists(startup) ? await File.ReadAllTextAsync(startup) : "";
        if (startup is not null && StartupScript.LoadsFolder(script, folder))
            messages.Add("SuperDirt also loads it at every start.");
        else if (startup is not null && StartupScript.AddSamplesFolder(script, SamplesRoot) is { } edited)
        {
            messages.Add("SuperDirt doesn't load your samples folder at startup yet.");
            var add = SmallButton("Load my samples at startup", $"Adds {StartupScript.LoadLine(SamplesRoot)} to {startup} (a backup is kept as startup.scd.bak)");
            add.Click += async (_, _) =>
            {
                try
                {
                    File.Copy(startup, startup + ".bak", overwrite: true);
                    await File.WriteAllTextAsync(startup, edited);
                    add.Content = "Added to startup.scd ✓";
                    add.IsEnabled = false;
                    _samplesChanged();
                    _log(OutputKind.Info, $"Tidepool: {startup} now loads {SamplesRoot} (backup: startup.scd.bak)");
                }
                catch (Exception e) { SetStatus($"Could not edit {startup}: {e.Message}"); }
            };
            actions.Add(add);
        }
        else
            messages.Add($"To load it at every start, add this to your SuperCollider startup file: {StartupScript.LoadLine(SamplesRoot)}");

        SetStatus(string.Join("  ", messages), actions.ToArray());
    }
}
