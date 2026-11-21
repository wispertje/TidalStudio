using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TidalStudio.App.Services;
using TidalStudio.Core.Completion;
using TidalStudio.Core.Settings;
using TidalStudio.Core.Synth;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Synth;

/// <summary>
/// Tidepool FM: a six-operator FM synth in the spirit of the classic 80s digital keyboards. Like Tidepool, it renders
/// its sound to WAV samples in a SuperDirt folder, played from Tidal with s "name" (and n for the rendered notes).
/// </summary>
public sealed class FmWindow : Window
{
    private static readonly Color GlobalColor = Color.Parse("#C9CFDA");
    private static readonly Color PitchColor = Color.Parse("#F472B6");
    private static readonly Color FxColor = SynthWindow.FxAccent;
    private static readonly Color OutColor = Color.Parse("#60A5FA");

    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly Action _samplesChanged;
    private readonly Action<OutputKind, string> _log;
    private readonly Func<string, Task<(bool Ok, string Message)>> _restartSuperDirt;
    private readonly PreviewPlayer _player = new();
    private readonly List<Action> _refreshers = new();
    private readonly Dictionary<int, Control> _opPanels = new();
    private readonly DispatcherTimer _autoPreviewTimer;

    private FmPatch _patch;
    private int _renderVersion;
    private int? _lastKey;

    private readonly ContentControl _sections = new();
    private readonly ComboBox _presets = new() { MinWidth = 170, PlaceholderText = "Choose a preset…" };
    private readonly TextBox _name = new() { Width = 130 };
    private readonly TextBox _notes = new() { Width = 150 };
    private readonly ComboBox _noteSets = new() { MinWidth = 120 };
    private readonly CheckBox _autoPreview = new() { Content = "Auto-preview", IsChecked = true };
    private readonly CheckBox _restartOnSave = new() { Content = "Restart SuperDirt after saving", FontSize = 12 };

    private readonly Func<string, bool, Task>? _insertCode;
    private SequencerWindow? _sequencer;
    private readonly WaveformView _wave = new() { Height = 70 };
    private readonly KeyboardView _keys = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _folderLabel = new() { FontSize = 11, Foreground = SynthWindow.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private static readonly (string Label, string Notes)[] NoteSets =
    {
        ("One note", "c5"),
        ("Octaves c2–c7", "c2 c3 c4 c5 c6 c7"),
        ("Keys c3–c6", "c3 c4 c5 c6"),
        ("Bass c1–c4", "c1 c2 c3 c4"),
        ("Chromatic", "c5 cs5 d5 ds5 e5 f5 fs5 g5 gs5 a5 as5 b5"),
        ("Custom", ""),
    };

    public FmWindow(AppServices services, Action samplesChanged, Action<OutputKind, string> log,
        Func<string, Task<(bool Ok, string Message)>> restartSuperDirt, Func<string, bool, Task>? insertCode = null)
    {
        _insertCode = insertCode;
        _services = services;
        _settings = services.Settings;
        _samplesChanged = samplesChanged;
        _log = log;
        _restartSuperDirt = restartSuperDirt;
        _restartOnSave.IsChecked = _settings.SynthRestartOnSave;
        _restartOnSave.IsCheckedChanged += (_, _) => _settings.SynthRestartOnSave = _restartOnSave.IsChecked == true;
        ToolTip.SetTip(_restartOnSave, "After saving, make SuperDirt load the new sound (restarting it if it was started elsewhere).");
        _patch = LoadLastPatch();

        Title = "Tidepool FM — six-operator FM synth";
        Width = 1280;
        Height = 900;
        MinWidth = 960;
        MinHeight = 620;
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
            _settings.FmPatchJson = _patch.ToJson();
            _sequencer?.Close();
            _player.Dispose();
        };
    }

    // ------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        RefreshPresetList();
        _presets.SelectionChanged += (_, _) =>
        {
            if (_presets.SelectedItem is not string item || item.StartsWith('—')) return;
            LoadPreset(item);
        };
        var random = SynthWindow.SmallButton("Randomise", "A random FM sound (Ctrl+R)");
        random.Click += (_, _) => Randomise();

        _name.TextChanged += (_, _) => { _patch.Name = _name.Text?.Trim() ?? ""; UpdateStatusHint(); _sequencer?.SourceChanged(); };
        ToolTip.SetTip(_name, "Sound name in Tidal: s \"name\". Lowercase letters, digits, - and _.");

        _noteSets.ItemsSource = NoteSets.Select(s => s.Label).ToList();
        _noteSets.SelectionChanged += (_, _) =>
        {
            if (_noteSets.SelectedIndex >= 0 && _noteSets.SelectedIndex < NoteSets.Length - 1) _notes.Text = NoteSets[_noteSets.SelectedIndex].Notes;
        };
        _notes.TextChanged += (_, _) =>
        {
            _patch.Notes = _notes.Text ?? "";
            int set = Array.FindIndex(NoteSets, s => s.Notes == _patch.Notes);
            _noteSets.SelectedIndex = set >= 0 ? set : NoteSets.Length - 1;
            UpdateKeyboardMarks();
            UpdateStatusHint();
            _sequencer?.SourceChanged();
        };
        ToolTip.SetTip(_notes, "Notes to render, one sample each, in Tidal note names (c5 = note 0). n picks them in this order.");

        var preview = SynthWindow.AccentButton("▶ Preview", "Play the first note (Space)");
        preview.Click += (_, _) => Preview();
        var save = SynthWindow.AccentButton("💾 Save to SuperDirt", "Render every note to WAV files in your samples folder (Ctrl+S)");
        save.Click += async (_, _) => await SaveAsync();

        var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 6) };
        top.Children.Add(SynthWindow.Labelled("Preset", _presets));
        top.Children.Add(random);
        top.Children.Add(SynthWindow.Gap(16));
        top.Children.Add(SynthWindow.Labelled("Name", _name));
        top.Children.Add(SynthWindow.Labelled("Notes", _noteSets));
        top.Children.Add(_notes);
        top.Children.Add(SynthWindow.Gap(16));
        top.Children.Add(preview);
        top.Children.Add(_autoPreview);
        top.Children.Add(SynthWindow.Gap(8));
        top.Children.Add(save);
        top.Children.Add(_restartOnSave);
        var sequencer = SynthWindow.AccentButton("🎹 Sequencer", "Open the piano roll: play melodies with this sound, live, and get the Tidal code");
        sequencer.Click += (_, _) => OpenSequencer();
        top.Children.Add(sequencer);
        top.Children.Add(SynthWindow.ExampleButton(this, "05-tidepool-fm.tidal", "how to play FM samples: keys, chords, basses, bells, percussion"));

        _keys.NotePressed += midi => { _lastKey = midi; Preview(midi); };
        var octaveDown = SynthWindow.SmallButton("◀", "Keyboard octave down");
        octaveDown.Click += (_, _) => { _keys.LowestC = Math.Max(12, _keys.LowestC - 12); _keys.InvalidateVisual(); };
        var octaveUp = SynthWindow.SmallButton("▶", "Keyboard octave up");
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

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        var bottom = new StackPanel();
        bottom.Children.Add(bottomGrid);
        bottom.Children.Add(new Border { BorderBrush = SynthWindow.BorderBrush2, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusBar });
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _sections, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return root;
    }

    private void RebuildSections()
    {
        _sequencer?.SourceChanged();
        _refreshers.Clear();
        _opPanels.Clear();
        var rows = new StackPanel { Margin = new Thickness(12, 0, 12, 0), Spacing = 10 };

        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("300,10,*") };
        var algorithm = AlgorithmSection();
        Grid.SetColumn(algorithm, 0);
        row1.Children.Add(algorithm);
        var ops = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*"),
            RowDefinitions = new RowDefinitions("Auto,10,Auto"),
        };
        for (int k = 1; k <= FmPatch.OperatorCount; k++)
        {
            var panel = OperatorSection(k);
            _opPanels[k] = panel;
            Grid.SetColumn(panel, (k - 1) % 3 * 2);
            Grid.SetRow(panel, (k - 1) / 3 * 2);
            ops.Children.Add(panel);
        }
        Grid.SetColumn(ops, 2);
        row1.Children.Add(ops);
        rows.Children.Add(row1);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,1.8*,10,*") };
        Place(row2, PitchSection(), 0);
        Place(row2, LfoSection(), 2);
        Place(row2, FxSection(), 4);
        Place(row2, OutputSection(), 6);
        rows.Children.Add(row2);

        _sections.Content = rows;
        _name.Text = _patch.Name;
        _notes.Text = _patch.Notes;
        UpdateKeyboardMarks();
        UpdateStatusHint();
    }

    private static void Place(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private Control AlgorithmSection()
    {
        var names = FmPatch.Algorithms.Select(a => a.Name).ToList();
        var combo = new ComboBox { ItemsSource = names, SelectedIndex = _patch.Algorithm, MinWidth = 180, FontSize = 12 };
        ToolTip.SetTip(combo, _patch.CurrentAlgorithm.Description);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex == _patch.Algorithm) return;
            _patch.Algorithm = combo.SelectedIndex;
            // Carrier / modulator roles change: rebuild the operator panels after this event.
            Dispatcher.UIThread.Post(() => { RebuildSections(); Commit(); });
        };
        var view = new AlgorithmView(() => _patch);
        view.OperatorClicked += op =>
        {
            if (!_opPanels.TryGetValue(op, out var panel)) return;
            panel.BringIntoView();
            Flash(panel);
        };
        _refreshers.Add(view.InvalidateVisual);
        var description = new TextBlock
        {
            Text = _patch.CurrentAlgorithm.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = SynthWindow.MutedBrush,
        };

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(combo);
        body.Children.Add(description);
        body.Children.Add(view);
        body.Children.Add(SynthWindow.KnobRow(
            K("Feedback", 0, 1, 0, () => _patch.Feedback, v => _patch.Feedback = v, SynthWindow.Percent, accent: AlgorithmView.OpColors[5],
                tip: "Operator 6 modulates itself: sine → buzzy saw → noise"),
            K("Bright", 0, 2, 1, () => _patch.Brightness, v => _patch.Brightness = v, SynthWindow.Percent, accent: GlobalColor,
                tip: "Scales every modulator at once: darker or brighter"),
            K("Key rate", 0, 1, 0, () => _patch.KeyRate, v => _patch.KeyRate = v, SynthWindow.Percent, accent: GlobalColor,
                tip: "Higher notes get shorter envelopes, like a piano or mallet")));
        return SynthWindow.Panel("ALGORITHM", body, GlobalColor);
    }

    private Control OperatorSection(int k)
    {
        var op = _patch.Operators[k - 1];
        var color = AlgorithmView.OpColors[k - 1];
        var algorithm = _patch.CurrentAlgorithm;
        string role = algorithm.IsCarrier(k)
            ? "carrier · heard"
            : "modulates " + string.Join(", ", algorithm.Links.Where(l => l.From == k).Select(l => l.To));

        var enabled = new CheckBox { IsChecked = op.Enabled, Content = $"OP {k}", FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color) };
        enabled.IsCheckedChanged += (_, _) => { op.Enabled = enabled.IsChecked == true; Changed(); Commit(); };
        var roleText = new TextBlock { Text = role, FontSize = 11, Foreground = SynthWindow.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var fixedBox = new CheckBox { IsChecked = op.Fixed, Content = "Fixed Hz", FontSize = 11 };
        ToolTip.SetTip(fixedBox, "Ignore the note: always play the Hz below (metallic hits, drums)");
        var ratioText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        void UpdateRatio() => ratioText.Text = op.Fixed ? $"{op.FixedHz:0} Hz" : $"× {op.Ratio:0.###}";
        UpdateRatio();
        _refreshers.Add(UpdateRatio);

        var header = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(ratioText);
        right.Children.Add(fixedBox);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(enabled);
        left.Children.Add(roleText);
        header.Children.Add(left);

        bool carrier = algorithm.IsCarrier(k);
        var ratio = K("Ratio", 0, 16, 1, () => op.Coarse, v => op.Coarse = (int)v, v => v < 0.5 ? "×0.5" : $"×{v:0}", step: 1, accent: color,
            tip: "Frequency as a multiple of the note. Whole numbers sound harmonic, Fine adds inharmonic (bell) ratios");
        var fine = K("Fine", 0, 0.99, 0, () => op.Fine, v => op.Fine = v, v => $"+{v * 100:0}%", accent: color);
        var hz = K("Hz", 20, 12000, 440, () => op.FixedHz, v => op.FixedHz = v, SynthWindow.Hz, log: true, accent: color,
            tip: "The fixed frequency, the same for every note");
        void ShowMode()
        {
            ratio.IsVisible = fine.IsVisible = !op.Fixed;
            hz.IsVisible = op.Fixed;
        }
        ShowMode();
        fixedBox.IsCheckedChanged += (_, _) => { op.Fixed = fixedBox.IsChecked == true; ShowMode(); Changed(); Commit(); };
        var tune = SynthWindow.KnobRow(
            ratio,
            fine,
            hz,
            K("Detune", -50, 50, 0, () => op.Detune, v => op.Detune = v, v => $"{v:+0;-0;0} ct", bipolar: true, accent: color),
            K(carrier ? "Level" : "Depth", 0, 1, 0.8, () => op.Level, v => op.Level = v, SynthWindow.Percent, accent: color,
                tip: carrier ? "How loud this operator is" : "How strongly it modulates: more = brighter, harsher"),
            K("Key scl", 0, 1, 0, () => op.KeyScale, v => op.KeyScale = v, SynthWindow.Percent, accent: color,
                tip: "Quieter / less bright on higher notes"));
        var env = new EnvelopeView(() => op.Envelope, () => _patch.Hold, color);
        _refreshers.Add(env.InvalidateVisual);
        var adsr = SynthWindow.KnobRow(
            K("Attack", 0, 10, 0.002, () => op.Envelope.Attack, v => op.Envelope.Attack = v, SynthWindow.Seconds, curve: 3.5, accent: color),
            K("Decay", 0, 10, 1, () => op.Envelope.Decay, v => op.Envelope.Decay = v, SynthWindow.Seconds, curve: 3.5, accent: color),
            K("Sustain", 0, 1, 0.6, () => op.Envelope.Sustain, v => op.Envelope.Sustain = v, SynthWindow.Percent, accent: color),
            K("Release", 0, 10, 0.4, () => op.Envelope.Release, v => op.Envelope.Release = v, SynthWindow.Seconds, curve: 3.5, accent: color));

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(header);
        body.Children.Add(tune);
        body.Children.Add(env);
        body.Children.Add(adsr);
        return SynthWindow.Panel(null, body, color);
    }

    private Control PitchSection()
    {
        var body = SynthWindow.KnobRow(
            K("Amount", -24, 24, 0, () => _patch.PitchEnvAmount, v => _patch.PitchEnvAmount = v, v => $"{v:+0.0;-0.0;0} st", bipolar: true, accent: PitchColor,
                tip: "Start this many semitones away and glide to the note (kicks, toms, brass blips)"),
            K("Time", 0.005, 2, 0.1, () => _patch.PitchEnvDecay, v => _patch.PitchEnvDecay = v, SynthWindow.Seconds, log: true, accent: PitchColor));
        return SynthWindow.Panel("PITCH ENVELOPE", body, PitchColor);
    }

    private Control LfoSection()
    {
        var body = SynthWindow.KnobRow(
            K("Rate", 0.1, 20, 5, () => _patch.LfoRate, v => _patch.LfoRate = v, v => $"{v:0.##} Hz", log: true, accent: PitchColor),
            K("Vibrato", 0, 100, 0, () => _patch.Vibrato, v => _patch.Vibrato = v, v => $"{v:0} ct", curve: 2, accent: PitchColor),
            K("Tremolo", 0, 1, 0, () => _patch.Tremolo, v => _patch.Tremolo = v, SynthWindow.Percent, accent: PitchColor),
            K("Delay", 0, 3, 0.3, () => _patch.LfoDelay, v => _patch.LfoDelay = v, SynthWindow.Seconds, curve: 2, accent: PitchColor));
        return SynthWindow.Panel("LFO", body, PitchColor);
    }

    private Control FxSection()
    {
        var fx = _patch.Fx;
        var body = SynthWindow.KnobRow(
            K("Drive", 0, 1, 0, () => fx.Drive, v => fx.Drive = v, SynthWindow.Percent, accent: FxColor),
            K("Chorus", 0, 1, 0, () => fx.ChorusMix, v => fx.ChorusMix = v, SynthWindow.Percent, accent: FxColor),
            K("Rate", 0.05, 8, 0.6, () => fx.ChorusRate, v => fx.ChorusRate = v, SynthWindow.RateText, log: true, accent: FxColor),
            K("Depth", 0, 10, 4, () => fx.ChorusDepth, v => fx.ChorusDepth = v, SynthWindow.Millis, accent: FxColor),
            K("Delay", 0, 1, 0, () => fx.DelayMix, v => fx.DelayMix = v, SynthWindow.Percent, accent: FxColor),
            K("Time", 0.02, 1.5, 0.25, () => fx.DelayTime, v => fx.DelayTime = v, SynthWindow.Seconds, log: true, accent: FxColor),
            K("Feedback", 0, 0.95, 0.35, () => fx.DelayFeedback, v => fx.DelayFeedback = v, SynthWindow.Percent, accent: FxColor),
            K("Reverb", 0, 1, 0, () => fx.ReverbMix, v => fx.ReverbMix = v, SynthWindow.Percent, accent: FxColor),
            K("Size", 0, 1, 0.7, () => fx.ReverbSize, v => fx.ReverbSize = v, SynthWindow.Percent, accent: FxColor),
            K("Tape", 0, 1, 0, () => fx.Tape, v => fx.Tape = v, SynthWindow.Percent, accent: FxColor));
        var panel = SynthWindow.Panel("EFFECTS", body, FxColor);
        ToolTip.SetTip(panel, SynthWindow.FxTip);
        return panel;
    }

    private Control OutputSection()
    {
        var normalize = new CheckBox { Content = "Normalise", IsChecked = _patch.Normalize };
        ToolTip.SetTip(normalize, "Make every sample peak at -1 dB, so all notes are equally loud");
        normalize.IsCheckedChanged += (_, _) => { _patch.Normalize = normalize.IsChecked == true; Commit(); };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(SynthWindow.KnobRow(
            K("Volume", 0, 1, 0.8, () => _patch.Volume, v => _patch.Volume = v, SynthWindow.Percent, accent: OutColor),
            K("Hold", 0.05, 10, 1, () => _patch.Hold, v => _patch.Hold = v, SynthWindow.Seconds, curve: 2, accent: OutColor),
            K("Max len", 0.2, 20, 6, () => _patch.MaxLength, v => _patch.MaxLength = v, SynthWindow.Seconds, curve: 2, accent: OutColor)));
        body.Children.Add(normalize);
        return SynthWindow.Panel("OUTPUT", body, OutColor);
    }

    private static void Flash(Control section)
    {
        if (section is not Border border) return;
        var original = border.Background;
        border.Background = new SolidColorBrush(Color.Parse("#262B36"));
        DispatcherTimer.RunOnce(() => border.Background = original, TimeSpan.FromMilliseconds(450));
    }

    private Knob K(string label, double min, double max, double def, Func<double> get, Action<double> set,
        Func<double, string>? format = null, bool log = false, double curve = 1, double step = 0, bool bipolar = false,
        Color? accent = null, string? tip = null)
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
        if (tip is not null) ToolTip.SetTip(knob, tip);
        return knob;
    }

    // ------------------------------------------------------------------ behaviour

    private void Changed()
    {
        foreach (var refresh in _refreshers) refresh();
        _sequencer?.SourceChanged();
    }

    private void Commit()
    {
        _sequencer?.SourceChanged();
        if (_autoPreview.IsChecked != true || _sequencer is { IsPlaying: true }) return;
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


    /// <summary>Opens the sequencer for this sound (or brings it to the front).</summary>
    internal SequencerWindow OpenSequencer()
    {
        if (_sequencer is { } open) { open.Activate(); return open; }
        _sequencer = SequencerWindow.ForMelody("Tidepool FM", _settings.FmSequenceJson, json => _settings.FmSequenceJson = json, _insertCode, () => { var patch = _patch.Clone(); return new MelodicSnapshot(patch.Name, SynthPatch.ParseNotes(patch.Notes), m => FmEngine.Render(patch, m)); });
        _sequencer.Closed += (_, _) => _sequencer = null;
        _sequencer.Show(this);
        return _sequencer;
    }

    private async void Preview(int? midi = null)
    {
        int note = midi ?? _lastKey ?? SynthPatch.ParseNotes(_patch.Notes).DefaultIfEmpty(60).First();
        int version = ++_renderVersion;
        var patch = _patch.Clone();
        var rendered = await Task.Run(() => FmEngine.Render(patch, note));
        if (version != _renderVersion) return;
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
        double[] ratios = { 0.5, 1, 1, 1, 2, 2, 3, 4, 5, 7, 1.41, 3.5 };
        _patch.Algorithm = r.Next(FmPatch.Algorithms.Count);
        _patch.Feedback = r.NextDouble() < 0.5 ? 0 : r.NextDouble() * 0.7;
        _patch.Brightness = 1;
        var algorithm = _patch.CurrentAlgorithm;
        for (int k = 1; k <= FmPatch.OperatorCount; k++)
        {
            var op = _patch.Operators[k - 1];
            bool carrier = algorithm.IsCarrier(k);
            op.Enabled = k == 1 || r.NextDouble() < 0.8;
            op.Fixed = false;
            op.WithRatio(carrier && r.NextDouble() < 0.7 ? 1 : ratios[r.Next(ratios.Length)]);
            op.Detune = r.NextDouble() < 0.4 ? r.NextDouble() * 14 - 7 : 0;
            op.Level = carrier ? 0.6 + r.NextDouble() * 0.4 : 0.15 + r.NextDouble() * 0.45;
            op.KeyScale = r.NextDouble() * 0.5;
            op.Envelope = new EnvelopeSettings
            {
                Attack = Math.Pow(r.NextDouble(), 4) * 0.8,
                Decay = 0.05 + Math.Pow(r.NextDouble(), 2) * 3,
                Sustain = r.NextDouble() < 0.4 ? 0 : r.NextDouble(),
                Release = 0.05 + r.NextDouble() * 1.2,
            };
        }
        _patch.PitchEnvAmount = r.NextDouble() < 0.15 ? r.Next(-12, 13) : 0;
        _patch.Vibrato = r.NextDouble() < 0.3 ? r.NextDouble() * 25 : 0;
        _patch.Fx = new FxSettings { ChorusMix = r.NextDouble() * 0.5, ReverbMix = r.NextDouble() * 0.4, DelayMix = r.NextDouble() < 0.3 ? r.NextDouble() * 0.3 : 0 };
        RebuildSections();
        Preview();
    }

    // ------------------------------------------------------------------ presets

    private static string UserPresetFolder => Path.Combine(AppSettings.DefaultDirectory, "fm-presets");

    private void RefreshPresetList()
    {
        var items = new List<string>();
        foreach (var (category, presets) in FmPresets.Groups)
        {
            if (items.Count > 0) items.Add($"— {category.ToLowerInvariant()} —");
            items.AddRange(presets.Select(p => p.Name));
        }
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
            try { _patch = FmPatch.FromJson(File.ReadAllText(path)); }
            catch (Exception e) { SetStatus($"Could not load {path}: {e.Message}"); return; }
        }
        else _patch = FmPresets.Create(item);
        _lastKey = null;
        RebuildSections();
        Preview();
    }

    private FmPatch LoadLastPatch()
    {
        if (_settings.FmPatchJson is { Length: > 0 } json)
            try { return FmPatch.FromJson(json); } catch (Exception) { }
        return FmPresets.Create("Tine piano");
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
            await Dialogs.ShowMessage(this, "Sound name", $"\"{name}\" can't be used in Tidal. Use lowercase letters, digits, - or _, starting with a letter (for example: myep, bass-2).");
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
        if (SampleLibrary.Scan().Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !s.IsSynth) &&
            !Directory.Exists(folder) &&
            !await Dialogs.Confirm(this, "Name already used", $"SuperDirt already has a sound called \"{name}\". Saving another one with the same name makes one of them unreachable. Save anyway?", "Save anyway"))
            return;

        SetStatus($"Rendering {notes.Count} note{(notes.Count > 1 ? "s" : "")}…");
        var patch = _patch.Clone();
        List<string> paths;
        try
        {
            var rendered = await Task.Run(() => notes.AsParallel().AsOrdered().Select(m => FmEngine.Render(patch.Clone(), m)).ToList());
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
        _log(OutputKind.Info, $"Tidepool FM: saved {paths.Count} sample{(paths.Count > 1 ? "s" : "")} to {folder}");
        string usage = paths.Count == 1 ? $"d1 $ s \"{name}\"" : $"d1 $ s \"{name}\" # n \"0 .. {paths.Count - 1}\"";
        string saved = $"Saved {paths.Count} sample{(paths.Count > 1 ? "s" : "")} to {folder}.  Try: {usage}";
        var reporter = new SaveReporter(_services, _restartSuperDirt, _samplesChanged, _log);
        var (messages, actions) = await reporter.ReportAsync(this, new[] { folder }, SamplesRoot, _restartOnSave.IsChecked == true,
            text => SetStatus($"{saved}  {text}"));
        messages.Insert(0, saved);
        SetStatus(string.Join("  ", messages), actions.ToArray());
    }
}
