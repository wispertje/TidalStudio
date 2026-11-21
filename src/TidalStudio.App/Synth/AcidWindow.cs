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
using TidalStudio.Core.Synth;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Synth;

/// <summary>
/// Tidepool Acid: a bassline box with accent and slide. Each of its 8 patterns is saved as one seamless loop in one
/// SuperDirt sound ("acidloop" # n 0 = pattern A, n 1 = B, …), to play in time with loopAt / chop.
/// </summary>
public sealed class AcidWindow : Window, ITempoFollower
{
    private static readonly IBrush SlotBrush = new SolidColorBrush(Color.Parse("#1F232C"));
    private static readonly IBrush SlotSelectedBrush = new SolidColorBrush(Color.Parse("#2E3A22"));
    private static readonly Color VoiceColor = AcidGridView.NoteColor;
    private static readonly Color NotesColor = AcidGridView.SlideColor;
    private static readonly Color FxColor = Color.Parse("#FB923C");
    private static readonly Color OutColor = Color.Parse("#8A93A6");

    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly Action _samplesChanged;
    private readonly Action<OutputKind, string> _log;
    private readonly Func<string, Task<(bool Ok, string Message)>> _restartSuperDirt;
    private readonly DispatcherTimer _autoPreviewTimer;
    private readonly DispatcherTimer _playheadTimer;
    private readonly Random _random = new();
    private readonly Func<string, bool, Task>? _insertCode;

    private AcidBox _box;
    private int _slot;
    private int _renderVersion;
    private bool _playing;
    private bool _rendering, _renderAgain;
    private bool _loading;

    private readonly ComboBox _sounds = new() { MinWidth = 190, PlaceholderText = "Choose a sound…" };
    private readonly TextBox _name = new() { Width = 120 };
    private readonly NumericUpDown _bpm = new() { Minimum = 40, Maximum = 300, Increment = 1, FormatString = "0", Width = 110 };
    private readonly Button _play = SynthWindow.AccentButton("▶ Play", "Play the pattern as a loop (Space)");
    private readonly CheckBox _autoPreview = new() { Content = "Auto-play", IsChecked = true };
    private readonly CheckBox _restartOnSave = new() { Content = "Restart SuperDirt after saving", FontSize = 12 };
    private readonly StackPanel _slots = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly NumericUpDown _steps = new() { Minimum = 1, Maximum = AcidPattern.MaxSteps, Increment = 1, FormatString = "0", Width = 100 };
    private readonly ComboBox _root = new() { MinWidth = 76, FontSize = 12 };
    private readonly ComboBox _scale = new() { MinWidth = 120, FontSize = 12 };
    private readonly TextBlock _patternInfo = new() { FontSize = 11.5, Foreground = SynthWindow.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly AcidGridView _grid;
    private readonly ContentControl _knobs = new();
    private readonly WaveformView _wave = new() { Height = 64, Hint = "Space plays the pattern as a loop" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _folderLabel = new() { FontSize = 11, Foreground = SynthWindow.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public AcidWindow(AppServices services, Action samplesChanged, Action<OutputKind, string> log,
        Func<string, Task<(bool Ok, string Message)>> restartSuperDirt, Func<string, bool, Task>? insertCode = null)
    {
        _insertCode = insertCode;
        _services = services;
        _settings = services.Settings;
        _samplesChanged = samplesChanged;
        _log = log;
        _restartSuperDirt = restartSuperDirt;
        _box = LoadLastBox();
        // One tempo for Tidepool Acid and every open sequencer, so they play in time together.
        _box.Bpm = Transport.Join(this, _box.Bpm);
        _grid = new AcidGridView(() => _box, () => Pattern);

        Title = "Tidepool Acid — bassline box";
        Width = 1180;
        Height = 860;
        MinWidth = 820;
        MinHeight = 560;
        Background = (IBrush?)Application.Current?.FindResource("TsWindowBackground") ?? Brushes.Black;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TidalStudio/Assets/TidalStudio.ico"))); }
        catch (Exception) { }

        _autoPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _autoPreviewTimer.Tick += (_, _) => { _autoPreviewTimer.Stop(); Play(); };
        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();

        _restartOnSave.IsChecked = _settings.SynthRestartOnSave;
        _restartOnSave.IsCheckedChanged += (_, _) => _settings.SynthRestartOnSave = _restartOnSave.IsChecked == true;
        ToolTip.SetTip(_restartOnSave, "After saving, make SuperDirt load the loops (restarting a SuperCollider that TidalStudio didn't start).");
        ToolTip.SetTip(_autoPreview, "Start playing the pattern after every change. While it plays, changes are always heard right away.");

        _grid.Edited += () => { RefreshSlots(); UpdateInfo(); LiveChange(); };
        _grid.Committed += Commit;

        Content = BuildLayout();
        RebuildAll();
        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            _settings.AcidBoxJson = _box.ToJson();
            _playheadTimer.Stop();
            Transport.Leave(this);
        };
    }

    private static SequencerTransport Transport => SequencerTransport.Shared;

    public string TempoName => "Tidepool Acid";

    /// <summary>A sequencer changed the shared tempo.</summary>
    public void FollowTempo(double bpm, string changedBy)
    {
        _box.Bpm = bpm;
        _loading = true;
        try { _bpm.Value = (decimal)bpm; }
        finally { _loading = false; }
        UpdateInfo();
        SetStatus($"Tempo {bpm:0.#} bpm, set in {changedBy}: Tidepool Acid and the sequencers share one tempo, so they play in time.");
        if (_playing) Play();
    }

    private AcidPattern Pattern => _box.Patterns[Math.Clamp(_slot, 0, AcidBox.Slots - 1)];

    // ------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        RefreshSoundList();
        _sounds.SelectionChanged += (_, _) =>
        {
            if (_loading || _sounds.SelectedItem is not string item || item.StartsWith('—')) return;
            LoadSound(item);
        };
        ToolTip.SetTip(_sounds, "Sounds change the voice and effects and keep your patterns. ★ items are boxes you saved (sound and patterns).");
        _name.TextChanged += (_, _) => { if (_loading) return; _box.Name = _name.Text?.Trim() ?? ""; UpdateInfo(); };
        ToolTip.SetTip(_name, "The sound name in Tidal: s \"name\" # n 0 plays pattern A, n 1 pattern B, …");
        _bpm.ValueChanged += (_, _) =>
        {
            if (_loading || _bpm.Value is not { } v) return;
            _box.Bpm = (double)v;
            Transport.SetBpm(_box.Bpm, this);
            UpdateInfo();
            Commit();
        };
        ToolTip.SetTip(_bpm, "Tempo the loops are rendered at, shared with every open sequencer so they play in time. Use the same tempo in Tidal (setcps (bpm/60/4)), or let loopAt stretch them.");

        _play.Click += (_, _) => TogglePlay();
        var save = SynthWindow.AccentButton("💾 Save loops to SuperDirt", "Render every pattern to a loop in your samples folder (Ctrl+S)");
        save.Click += async (_, _) => await SaveAsync();

        var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 4) };
        top.Children.Add(SynthWindow.Labelled("Sound", _sounds));
        top.Children.Add(SynthWindow.Labelled("Name", _name));
        top.Children.Add(SynthWindow.Labelled("BPM", _bpm));
        top.Children.Add(_play);
        top.Children.Add(_autoPreview);
        top.Children.Add(new Border { Width = 12 });
        top.Children.Add(save);
        top.Children.Add(_restartOnSave);
        top.Children.Add(SynthWindow.ExampleButton(this, "03-tidepool-acid.tidal", "how to play the loops in time: loopAt, chop, switching patterns, splice, filter sweeps"));

        // --- pattern bar
        _steps.ValueChanged += (_, _) =>
        {
            if (_loading || _steps.Value is not { } v) return;
            Pattern.Length = Math.Clamp((int)v, 1, AcidPattern.MaxSteps);
            _grid.InvalidateVisual();
            RefreshSlots();
            UpdateInfo();
            Commit();
        };
        ToolTip.SetTip(_steps, "Steps in this pattern: 16 = one bar = one Tidal cycle, 32 = two cycles");
        _root.ItemsSource = Enumerable.Range(24, 25).Select(SynthPatch.NoteName).ToList();
        _root.SelectionChanged += (_, _) =>
        {
            if (_loading || _root.SelectedIndex < 0) return;
            _box.Root = 24 + _root.SelectedIndex;
            _grid.InvalidateVisual();
            Commit();
        };
        ToolTip.SetTip(_root, "Key: the bottom row of the grid (for every pattern)");
        var scales = Enum.GetValues<AcidScale>().ToList();
        _scale.ItemsSource = scales.Select(ScaleName).ToList();
        _scale.SelectionChanged += (_, _) => { if (!_loading && _scale.SelectedIndex >= 0) _box.Scale = scales[_scale.SelectedIndex]; };
        ToolTip.SetTip(_scale, "The notes Random and Mutate choose from");

        var random = SynthWindow.SmallButton("🎲 Random", "A new random acid line in this slot (R)");
        random.Click += (_, _) => Randomise();
        var mutate = SynthWindow.SmallButton("Mutate", "Change a few steps (M)");
        mutate.Click += (_, _) => Mutate();
        var clear = SynthWindow.SmallButton("Clear", "Empty this pattern");
        clear.Click += (_, _) => Edit(p => { foreach (var s in p.Steps) { s.Gate = false; s.Accent = false; s.Slide = false; s.Octave = 0; } });
        var left = SynthWindow.SmallButton("◀", "Shift the pattern one step earlier");
        left.Click += (_, _) => Edit(p => AcidLines.Rotate(p, -1));
        var right = SynthWindow.SmallButton("▶", "Shift the pattern one step later");
        right.Click += (_, _) => Edit(p => AcidLines.Rotate(p, 1));
        var down = SynthWindow.SmallButton("−1", "Transpose the pattern down a semitone");
        down.Click += (_, _) => Edit(p => AcidLines.Transpose(p, -1));
        var up = SynthWindow.SmallButton("+1", "Transpose the pattern up a semitone");
        up.Click += (_, _) => Edit(p => AcidLines.Transpose(p, 1));
        var copy = SynthWindow.SmallButton("Copy to next", "Copy this pattern to the next slot (to make a variation of it)");
        copy.Click += (_, _) => CopyToNext();

        var patternBar = new WrapPanel { Orientation = Orientation.Horizontal };
        patternBar.Children.Add(SynthWindow.Labelled("Pattern", _slots));
        patternBar.Children.Add(SynthWindow.Labelled("Steps", _steps));
        patternBar.Children.Add(SynthWindow.Labelled("Key", _root));
        patternBar.Children.Add(SynthWindow.Labelled("Scale", _scale));
        foreach (var b in new[] { random, mutate, clear, left, right, down, up, copy }) patternBar.Children.Add(b);
        var copyCode = SynthWindow.SmallButton("📋 Copy code", "Copy Tidal code that plays this pattern's saved loop in time (loopAt + chop)");
        copyCode.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(AcidLines.Example(_box, _slot));
            SetStatus($"Code for pattern {AcidBox.SlotNames[_slot]} copied. Save the loops (Ctrl+S) so SuperDirt has them.");
        };
        patternBar.Children.Add(copyCode);
        if (_insertCode is not null)
        {
            var insert = SynthWindow.SmallButton("Insert into script", "Put the code for this pattern in the current script");
            insert.Click += async (_, _) => await _insertCode(AcidLines.Example(_box, _slot), false);
            patternBar.Children.Add(insert);
        }

        var gridPanel = new StackPanel { Spacing = 6 };
        gridPanel.Children.Add(patternBar);
        gridPanel.Children.Add(_grid);
        gridPanel.Children.Add(_patternInfo);

        var editor = new StackPanel { Spacing = 10, Margin = new Thickness(12, 4, 12, 0) };
        editor.Children.Add(SynthWindow.Panel(null, gridPanel, VoiceColor));
        editor.Children.Add(_knobs);

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
        root.Children.Add(new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return root;
    }

    private static string ScaleName(AcidScale s) => s switch
    {
        AcidScale.MinorPentatonic => "Minor pentatonic",
        _ => s.ToString(),
    };

    private void RebuildAll()
    {
        _loading = true;
        try
        {
            _name.Text = _box.Name;
            _bpm.Value = (decimal)Math.Clamp(_box.Bpm, 40, 300);
            _root.SelectedIndex = Math.Clamp(_box.Root - 24, 0, 24);
            _scale.SelectedIndex = Enum.GetValues<AcidScale>().ToList().IndexOf(_box.Scale);
            _steps.Value = Pattern.Length;
        }
        finally { _loading = false; }
        RefreshSlots();
        RebuildKnobs();
        _grid.InvalidateVisual();
        UpdateInfo();
    }

    // ------------------------------------------------------------------ slots

    private void RefreshSlots()
    {
        _slots.Children.Clear();
        for (int i = 0; i < AcidBox.Slots; i++)
        {
            int slot = i;
            var p = _box.Patterns[i];
            bool selected = i == _slot;
            var text = new TextBlock
            {
                Text = AcidBox.SlotNames[i].ToString(), FontWeight = FontWeight.SemiBold, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = p.IsEmpty ? SynthWindow.MutedBrush : new SolidColorBrush(VoiceColor),
            };
            var button = new Border
            {
                Width = 30, Height = 28, CornerRadius = new CornerRadius(4),
                Background = selected ? SlotSelectedBrush : SlotBrush,
                BorderBrush = new SolidColorBrush(selected ? VoiceColor : Color.Parse("#2A2F3A")),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Child = text,
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(button, $"Pattern {AcidBox.SlotNames[i]}: n {i} in Tidal ({(p.IsEmpty ? "empty" : $"{p.Length} steps")}). Keys 1–8 select a pattern.");
            button.PointerPressed += (_, _) => SelectSlot(slot);
            _slots.Children.Add(button);
        }
    }

    private void SelectSlot(int slot)
    {
        _slot = Math.Clamp(slot, 0, AcidBox.Slots - 1);
        _loading = true;
        try { _steps.Value = Pattern.Length; }
        finally { _loading = false; }
        RefreshSlots();
        _grid.InvalidateVisual();
        UpdateInfo();
        if (!Pattern.IsEmpty && (_playing || _autoPreview.IsChecked == true)) Play();
        else if (Pattern.IsEmpty) StopPlaying();
    }

    private void Edit(Action<AcidPattern> change)
    {
        change(Pattern);
        _grid.InvalidateVisual();
        RefreshSlots();
        UpdateInfo();
        Commit();
    }

    private void Randomise()
    {
        Edit(p => AcidLines.Randomise(p, _box.Scale, _random));
    }

    private void Mutate()
    {
        if (Pattern.IsEmpty) { Randomise(); return; }
        Edit(p => AcidLines.Mutate(p, _box.Scale, _random));
    }

    private void CopyToNext()
    {
        if (_slot >= AcidBox.Slots - 1) { SetStatus("This is the last slot."); return; }
        _box.Patterns[_slot + 1] = Pattern.Clone();
        SelectSlot(_slot + 1);
        SetStatus($"Copied to pattern {AcidBox.SlotNames[_slot]} — change it with Mutate or by hand.");
    }

    private void UpdateInfo()
    {
        var p = Pattern;
        int notes = p.Steps.Take(p.Length).Count(s => s.Gate);
        string cycles = AcidBox.Cycles(p);
        _patternInfo.Text = $"Pattern {AcidBox.SlotNames[_slot]}: {notes} notes in {p.Length} steps = {cycles} cycle(s), {p.Length * _box.StepSeconds:0.00} s at {_box.Bpm:0.#} bpm  →  "
            + $"s \"{_box.Name}\" # n {_slot}   ·   accent = louder & brighter (several in a row build up), slide = glide into the next note";
        var problems = _box.Validate();
        if (problems.Count > 0) SetStatus(problems[0]);
        else SetStatus($"Will save {_box.SavedSlotCount} loop(s) as s \"{_box.Name}\" # n 0..{_box.SavedSlotCount - 1}. "
            + $"Play in Tidal with: d1 $ loopAt {AcidBox.Cycles(Pattern)} $ chop {Pattern.Length} $ s \"{_box.Name}\" # n {_slot}");
    }

    // ------------------------------------------------------------------ knobs

    private void RebuildKnobs()
    {
        var v = _box.Voice;
        var fx = _box.Fx;
        var waves = Enum.GetValues<AcidWave>().ToList();
        var wave = new ComboBox { ItemsSource = waves.Select(w => w.ToString()).ToList(), SelectedIndex = waves.IndexOf(v.Wave), MinWidth = 90, FontSize = 12 };
        ToolTip.SetTip(wave, "Saw: the classic buzzy acid. Square: hollower and deeper.");
        wave.SelectionChanged += (_, _) => { if (wave.SelectedIndex >= 0) { v.Wave = waves[wave.SelectedIndex]; Commit(); } };

        var voiceBody = new StackPanel { Spacing = 6 };
        voiceBody.Children.Add(SynthWindow.Labelled("Wave", wave));
        voiceBody.Children.Add(SynthWindow.KnobRow(
            K("Tune", -12, 12, 0, () => v.Tune, x => v.Tune = x, x => $"{x:+0.0;-0.0;0} st", bipolar: true, accent: VoiceColor),
            K("Cutoff", 40, 4000, 380, () => v.Cutoff, x => v.Cutoff = x, SynthWindow.Hz, log: true, accent: VoiceColor),
            K("Reso", 0, 1, 0.78, () => v.Resonance, x => v.Resonance = x, SynthWindow.Percent, accent: VoiceColor),
            K("Env mod", 0, 1, 0.55, () => v.EnvMod, x => v.EnvMod = x, SynthWindow.Percent, accent: VoiceColor),
            K("Decay", 0.03, 2.5, 0.4, () => v.Decay, x => v.Decay = x, SynthWindow.Seconds, curve: 2.2, accent: VoiceColor),
            K("Accent", 0, 1, 0.6, () => v.Accent, x => v.Accent = x, SynthWindow.Percent, accent: AcidGridView.AccentColor)));
        var voice = SynthWindow.Panel("VOICE · FILTER", voiceBody, VoiceColor);
        ToolTip.SetTip(voice, "One oscillator into a resonant 4-pole low-pass. Every note opens the filter by Env mod and it closes over Decay; accented notes close fast and jump out.");

        var notes = SynthWindow.Panel("NOTES · GROOVE", SynthWindow.KnobRow(
            K("Slide", 0.01, 0.3, 0.06, () => v.SlideTime, x => v.SlideTime = x, SynthWindow.Seconds, curve: 1.6, accent: NotesColor),
            K("Gate", 0.1, 1, 0.55, () => v.Gate, x => v.Gate = x, SynthWindow.Percent, accent: NotesColor),
            K("Swing", 0, 0.5, 0, () => _box.Swing, x => _box.Swing = x, x => $"{x * 100:0}%", accent: NotesColor)), NotesColor);
        ToolTip.SetTip(notes, "Slide: glide time between slid notes. Gate: how long notes hold within their step (short = staccato). Swing: delays every second sixteenth.");

        var fxPanel = SynthWindow.Panel("DRIVE · DELAY · REVERB", SynthWindow.KnobRow(
            K("Drive", 0, 1, 0.15, () => v.Drive, x => v.Drive = x, SynthWindow.Percent, accent: FxColor),
            K("Delay", 0, 0.8, 0, () => fx.DelayMix, x => fx.DelayMix = x, SynthWindow.Percent, accent: FxColor),
            K("Time", 1, 8, 3, () => fx.DelaySteps, x => fx.DelaySteps = x, DelayText, step: 1, accent: FxColor),
            K("Feedback", 0, 0.9, 0.4, () => fx.DelayFeedback, x => fx.DelayFeedback = x, SynthWindow.Percent, accent: FxColor),
            K("Reverb", 0, 0.8, 0, () => fx.ReverbMix, x => fx.ReverbMix = x, SynthWindow.Percent, accent: FxColor),
            K("Size", 0, 1, 0.5, () => fx.ReverbSize, x => fx.ReverbSize = x, SynthWindow.Percent, accent: FxColor)), FxColor);
        ToolTip.SetTip(fxPanel, "The delay follows the tempo (Time in sixteenths). Delay and reverb tails wrap around, so the saved loop is seamless.");

        var normalize = new CheckBox { Content = "Normalise", IsChecked = _box.Normalize, FontSize = 12 };
        normalize.IsCheckedChanged += (_, _) => { _box.Normalize = normalize.IsChecked == true; Commit(); };
        var rates = new List<string> { "44100", "48000" };
        var rate = new ComboBox { ItemsSource = rates, SelectedItem = _box.SampleRate.ToString(CultureInfo.InvariantCulture), FontSize = 12 };
        rate.SelectionChanged += (_, _) => { if (rate.SelectedItem is string s) { _box.SampleRate = int.Parse(s, CultureInfo.InvariantCulture); Commit(); } };
        var outBody = new StackPanel { Spacing = 6 };
        outBody.Children.Add(SynthWindow.KnobRow(
            K("Level", 0, 1, 0.9, () => v.Level, x => v.Level = x, SynthWindow.Percent, accent: OutColor)));
        outBody.Children.Add(normalize);
        outBody.Children.Add(SynthWindow.Labelled("Hz", rate));
        var output = SynthWindow.Panel("OUTPUT", outBody, OutColor);

        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,Auto") };
        Grid.SetColumn(voice, 0);
        Grid.SetColumn(notes, 2);
        row1.Children.Add(voice);
        row1.Children.Add(notes);
        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,Auto") };
        Grid.SetColumn(fxPanel, 0);
        Grid.SetColumn(output, 2);
        row2.Children.Add(fxPanel);
        row2.Children.Add(output);
        var rows = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        rows.Children.Add(row1);
        rows.Children.Add(row2);
        _knobs.Content = rows;
    }

    private static string DelayText(double steps) => Math.Round(steps) switch
    {
        1 => "1/16",
        2 => "1/8",
        3 => "3/16 (dotted)",
        4 => "1/4",
        6 => "3/8 (dotted)",
        8 => "1/2",
        var s => $"{s:0}/16",
    };

    private Knob K(string label, double min, double max, double def, Func<double> get, Action<double> set,
        Func<double, string> format, bool log = false, double curve = 1, double step = 0, bool bipolar = false, Color? accent = null)
    {
        var knob = new Knob
        {
            Label = label, Minimum = min, Maximum = max, Default = def, Log = log, Curve = curve, Step = step,
            Bipolar = bipolar, Format = format, Accent = accent ?? VoiceColor,
        };
        knob.Value = get();
        knob.ValueChanged += x => { set(x); LiveChange(); };
        knob.Committed += Commit;
        return knob;
    }

    // ------------------------------------------------------------------ playing

    /// <summary>A change is finished: update the playing loop, or start playing when Auto-play is on.</summary>
    private void Commit()
    {
        if (_playing) { Play(); return; }
        if (_autoPreview.IsChecked != true) return;
        _autoPreviewTimer.Stop();
        _autoPreviewTimer.Start();
    }

    /// <summary>A change in progress (a knob being turned, the grid being painted): heard right away while playing.</summary>
    private void LiveChange()
    {
        if (_playing) Play();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || e.Source is NumericUpDown) return;
        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Space) { e.Handled = true; TogglePlay(); }
            else if (e.Key == Key.R) { e.Handled = true; Randomise(); }
            else if (e.Key == Key.M) { e.Handled = true; Mutate(); }
            else if (e.Key is >= Key.D1 and <= Key.D8) { e.Handled = true; SelectSlot(e.Key - Key.D1); }
        }
        else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; _ = SaveAsync(); }
    }

    private void TogglePlay()
    {
        if (_playing) StopPlaying();
        else Play();
    }

    private void StopPlaying()
    {
        ++_renderVersion;
        Transport.Stop(this);
        _playing = false;
        _playheadTimer.Stop();
        _grid.PlayStep = -1;
        _grid.InvalidateVisual();
        _play.Content = "▶ Play";
    }

    /// <summary>
    /// Renders the selected pattern as one loop and plays it until stopped, with a moving playhead. While it already plays,
    /// the new render replaces the loop at the same position, so knob and grid changes are heard without restarting.
    /// Only one render runs at a time: changes made meanwhile are rendered right after it, with the latest settings.
    /// </summary>
    private async void Play()
    {
        if (_rendering) { _renderAgain = true; return; }
        _rendering = true;
        try
        {
            do
            {
                _renderAgain = false;
                if (Pattern.IsEmpty)
                {
                    if (_playing) StopPlaying();
                    SetStatus("This pattern is empty: click the grid to add notes, or press R for a random line.");
                    return;
                }
                int version = _renderVersion;
                var box = _box.Clone();
                int slot = _slot;
                int steps = box.Patterns[slot].Length;
                var rendered = await Task.Run(() => AcidEngine.Render(box, slot));
                if (version != _renderVersion) continue; // stopped meanwhile (plays again only if asked since)
                _wave.Note = rendered;
                _wave.InvalidateVisual();
                if (Transport.Play(this, rendered, steps) is { } err) { StopPlaying(); SetStatus(err); return; }
                if (!_playing)
                {
                    var others = Transport.Others(this).Where(f => Transport.IsPlaying(f)).Select(f => f.TempoName).ToList();
                    if (others.Count > 0) SetStatus($"Playing in time with {string.Join(" and ", others)} ({Transport.Bpm:0.#} bpm).");
                    _playing = true;
                    _play.Content = "■ Stop";
                    _playheadTimer.Start();
                }
            }
            while (_renderAgain);
        }
        finally { _rendering = false; }
    }

    private void UpdatePlayhead()
    {
        int step = -1;
        if (Transport.StepIn(this) is { } stepNow)
        {
            double inLoop = stepNow * _box.StepSeconds;
            var p = Pattern;
            int sr = 1000;
            int ms = (int)(inLoop * sr);
            for (int i = p.Length - 1; i >= 0; i--)
                if (AcidEngine.StepStart(_box, i, sr) <= ms) { step = i; break; }
        }
        if (step != _grid.PlayStep)
        {
            _grid.PlayStep = step;
            _grid.InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ sounds and boxes

    private static string UserBoxFolder => Path.Combine(AppSettings.DefaultDirectory, "acid-boxes");

    private void RefreshSoundList()
    {
        var items = new List<string> { "— acid sounds (keep patterns) —" };
        items.AddRange(AcidLines.Sounds.Select(s => s.Name));
        items.Add("— synth pop sounds (keep patterns) —");
        items.AddRange(AcidLines.PopSounds.Select(s => s.Name));
        if (Directory.Exists(UserBoxFolder))
        {
            var mine = Directory.EnumerateFiles(UserBoxFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n).ToList();
            if (mine.Count > 0)
            {
                items.Add("— my boxes (sound + patterns) —");
                items.AddRange(mine.Select(n => "★ " + n));
            }
        }
        items.Add("— replace all patterns —");
        items.Add(AcidDemo);
        items.Add(PopDemo);
        _sounds.ItemsSource = items;
    }

    private const string AcidDemo = "Acid demo patterns";
    private const string PopDemo = "Synth pop patterns + Pop octave bass";

    private void RefreshSoundListQuietly()
    {
        _loading = true;
        try { _sounds.SelectedItem = null; }
        finally { _loading = false; }
    }

    private async void LoadSound(string item)
    {
        if (item.StartsWith("★ ", StringComparison.Ordinal))
        {
            string path = Path.Combine(UserBoxFolder, item[2..] + ".json");
            try { _box = AcidBox.FromJson(File.ReadAllText(path)); }
            catch (Exception e) { SetStatus($"Could not load {path}: {e.Message}"); return; }
            _slot = 0;
        }
        else if (item is AcidDemo or PopDemo)
        {
            if (!_box.Patterns.All(p => p.IsEmpty) &&
                !await Dialogs.Confirm(this, "Replace patterns?", "This replaces all 8 patterns of the box. Saved boxes (★) are not affected.", "Replace"))
            {
                RefreshSoundListQuietly();
                return;
            }
            bool pop = item == PopDemo;
            var demo = AcidLines.FromPatterns(pop ? AcidLines.PopPatterns : AcidLines.DemoPatterns);
            demo.Name = _box.Name;
            demo.Voice = _box.Voice;
            demo.Fx = _box.Fx;
            demo.Bpm = pop ? 118 : _box.Bpm;
            demo.Scale = _box.Scale;
            if (pop) AcidLines.PopSounds[0].Apply(demo);
            _box = demo;
            _slot = 0;
        }
        else
        {
            var sound = AcidLines.AllSounds.FirstOrDefault(s => s.Name == item);
            if (sound.Apply is null) return;
            sound.Apply(_box);
        }
        Transport.SetBpm(_box.Bpm, this); // a loaded box or the pop demo brings its own tempo
        RebuildAll();
        if (!Pattern.IsEmpty) Play();
    }

    private AcidBox LoadLastBox()
    {
        if (_settings.AcidBoxJson is { Length: > 0 } json)
            try { return AcidBox.FromJson(json); }
            catch (Exception) { }
        return AcidLines.Default();
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
        var problems = _box.Validate();
        if (problems.Count > 0)
        {
            await Dialogs.ShowMessage(this, "Can't save the loops", string.Join("\n", problems));
            return;
        }
        string name = _box.Name;
        string folder = Path.Combine(SamplesRoot, name);
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.wav").Any() &&
            !await Dialogs.Confirm(this, "Replace sound?", $"\"{name}\" already has samples in {SamplesRoot}.\n\nReplace them with these loops?", "Replace"))
            return;
        var library = SampleLibrary.Scan().Where(s => !s.IsSynth).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (library.Contains(name) && !Directory.Exists(folder) &&
            !await Dialogs.Confirm(this, "Name already used", $"SuperDirt already has a sound called {name}. Saving another with the same name makes one unreachable — choose another name, or save anyway.", "Save anyway"))
            return;

        var box = _box.Clone();
        string root = SamplesRoot;
        int count = box.SavedSlotCount;
        SetStatus($"Rendering {count} loop(s)…");
        try
        {
            await Task.Run(() => AcidLines.Write(root, box));
            Directory.CreateDirectory(UserBoxFolder);
            await File.WriteAllTextAsync(Path.Combine(UserBoxFolder, box.Name + ".json"), box.ToJson());
        }
        catch (Exception e)
        {
            SetStatus($"Saving failed: {e.Message}");
            return;
        }
        _loading = true;
        try { RefreshSoundList(); }
        finally { _loading = false; }
        _samplesChanged();
        _log(OutputKind.Info, $"Tidepool Acid: saved {count} loop(s) as \"{box.Name}\" to {root}");

        string example = AcidLines.Example(box, Math.Min(_slot, Math.Max(0, count - 1)));
        string saved = $"Saved {count} loop(s): s \"{box.Name}\" # n 0..{count - 1}.";
        var reporter = new SaveReporter(_services, _restartSuperDirt, _samplesChanged, _log);
        var (messages, actions) = await reporter.ReportAsync(this, new List<string> { Path.Combine(root, box.Name) }, root, _restartOnSave.IsChecked == true, text => SetStatus($"{saved}  {text}"));
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
