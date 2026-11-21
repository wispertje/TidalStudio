using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TidalStudio.Core.Synth;

namespace TidalStudio.App.Synth;

/// <summary>A drum of a kit, as the beat sequencer shows it.</summary>
public sealed record SequencerDrum(string Drum, string Sound, string Label, DrumModel Model);

/// <summary>What a melodic synth gives the sequencer: its Tidal sound, the notes its samples are rendered at (n 0, n 1, …)
/// and a way to render a note with the current settings (safe to call from another thread).</summary>
public sealed record MelodicSnapshot(string Sound, IReadOnlyList<int> SampleMidis, Func<int, RenderedNote> Render);

/// <summary>What a drum synth gives the sequencer: the kit's drums and a way to render one (safe to call from another thread).</summary>
public sealed record DrumSnapshot(string Kit, IReadOnlyList<SequencerDrum> Drums, Func<string, RenderedNote?> Render);

/// <summary>
/// A sequencer for one of the Tidepool synths: a piano roll (Tidepool, Tidepool FM) or a drum grid (Tidepool Drums).
/// It loops the sequence with the synth's current sound — knob changes in the synth window are heard while it plays — and
/// writes the sequence as Tidal code that plays the saved samples the same way.
/// </summary>
public sealed class SequencerWindow : Window, ITempoFollower
{
    private readonly string _synthName;
    private readonly Func<MelodicSnapshot>? _melodicSource;
    private readonly Func<DrumSnapshot>? _drumSource;
    private readonly Action<string> _store;
    private readonly Func<string, bool, Task>? _insertCode;

    private MelodySequence? _melody;
    private BeatSequence? _beat;

    private readonly PreviewPlayer _audition = new();
    private readonly DispatcherTimer _playheadTimer;
    private readonly Random _random = new();
    private readonly List<string> _undo = new(), _redo = new();

    // Rendered samples for the current sound (cleared when the sound changes).
    private ConcurrentDictionary<int, RenderedNote> _noteCache = new();
    private ConcurrentDictionary<string, RenderedNote?> _drumCache = new();
    private IReadOnlyList<SequencerDrum> _drums = Array.Empty<SequencerDrum>();

    private bool _playing, _rendering, _renderAgain, _loading;
    private int _renderVersion, _auditionVersion;

    private readonly PianoRollView? _roll;
    private readonly BeatGridView? _grid;
    private readonly Button _play = SynthWindow.AccentButton("▶ Play", "Loop the sequence (Space)");
    private readonly NumericUpDown _bpm = new() { Minimum = 40, Maximum = 300, Increment = 1, FormatString = "0", Width = 110 };
    private readonly NumericUpDown _steps = new() { Minimum = 1, Maximum = SequenceBase.MaxSteps, Increment = 1, FormatString = "0", Width = 100 };
    private readonly ComboBox _orbit = new() { FontSize = 12, MinWidth = 64 };
    private Knob? _swing;
    private ComboBox? _end, _key, _scale;
    private readonly TextBox _code = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 132,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12.5,
    };
    private readonly TextBlock _hint = new() { FontSize = 11.5, Foreground = SynthWindow.MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _rollScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    private SequencerWindow(string synthName, string? json, Action<string> store, Func<string, bool, Task>? insertCode,
        Func<MelodicSnapshot>? melodic, Func<DrumSnapshot>? drums)
    {
        _synthName = synthName;
        _store = store;
        _insertCode = insertCode;
        _melodicSource = melodic;
        _drumSource = drums;

        if (melodic is not null)
        {
            try { _melody = json is { Length: > 0 } ? MelodySequence.FromJson(json) : MelodySequence.Default(); }
            catch (Exception) { _melody = MelodySequence.Default(); }
            _roll = new PianoRollView(() => _melody!);
            _roll.BeforeEdit += PushUndo;
            _roll.Edited += Edited;
            _roll.Committed += Committed;
            _roll.Audition += AuditionNote;
        }
        else
        {
            _drums = drums!().Drums;
            try { _beat = json is { Length: > 0 } ? BeatSequence.FromJson(json) : BeatSequence.Default(_drums.Select(d => d.Drum)); }
            catch (Exception) { _beat = BeatSequence.Default(_drums.Select(d => d.Drum)); }
            _grid = new BeatGridView(() => _beat!, () => _drums);
            _grid.BeforeEdit += PushUndo;
            _grid.Edited += Edited;
            _grid.Committed += Committed;
            _grid.Audition += AuditionDrum;
            _grid.UpdateSize();
        }

        Title = $"{synthName} — {(IsMelodic ? "sequencer" : "beat sequencer")}";
        Width = 1180;
        Height = IsMelodic ? 860 : 680;
        MinWidth = 760;
        MinHeight = 480;
        Background = (IBrush?)Application.Current?.FindResource("TsWindowBackground") ?? Brushes.Black;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TidalStudio/Assets/TidalStudio.ico"))); }
        catch (Exception) { }

        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();

        // One tempo for every open sequencer, so they play in time together.
        Seq.Bpm = Transport.Join(this, Seq.Bpm);

        Content = BuildLayout();
        LoadControls();
        UpdateCode();
        KeyDown += OnKeyDown;
        Opened += (_, _) => ScrollToNotes();
        Closing += (_, _) =>
        {
            Store();
            _playheadTimer.Stop();
            Transport.Leave(this);
            _audition.Dispose();
        };
    }

    private static SequencerTransport Transport => SequencerTransport.Shared;

    public string TempoName => _synthName;

    /// <summary>Another sequencer changed the shared tempo.</summary>
    public void FollowTempo(double bpm, string changedBy)
    {
        Seq.Bpm = bpm;
        LoadControls();
        UpdateCode();
        Store();
        SetStatus($"Tempo {bpm:0.#} bpm, set in {changedBy}: every open sequencer shares one tempo, so they play in time.");
        if (_playing) Render();
    }

    /// <summary>This window's tempo changed (BPM box, undo): share it with the other sequencers.</summary>
    private void ShareTempo() => Transport.SetBpm(Seq.Bpm, this);

    /// <summary>A piano-roll sequencer for a melodic synth.</summary>
    public static SequencerWindow ForMelody(string synthName, string? json, Action<string> store, Func<string, bool, Task>? insertCode,
        Func<MelodicSnapshot> source) => new(synthName, json, store, insertCode, source, null);

    /// <summary>A beat sequencer for a drum kit.</summary>
    public static SequencerWindow ForBeat(string synthName, string? json, Action<string> store, Func<string, bool, Task>? insertCode,
        Func<DrumSnapshot> source) => new(synthName, json, store, insertCode, null, source);

    private bool IsMelodic => _melody is not null;
    private SequenceBase Seq => (SequenceBase?)_melody ?? _beat!;

    /// <summary>True while the sequence loops (the synth window then leaves out its own previews).</summary>
    public bool IsPlaying => _playing;

    /// <summary>The synth's sound changed: re-render (heard right away while playing) and update the code.</summary>
    public void SourceChanged()
    {
        _noteCache = new();
        _drumCache = new();
        if (_drumSource is not null)
        {
            _drums = _drumSource().Drums;
            _grid!.UpdateSize();
        }
        UpdateCode();
        if (_playing) Render();
    }

    // ------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        _play.Click += (_, _) => TogglePlay();
        _bpm.ValueChanged += (_, _) =>
        {
            if (_loading || _bpm.Value is not { } v) return;
            PushUndo();
            Seq.Bpm = (double)v;
            ShareTempo();
            Committed();
        };
        ToolTip.SetTip(_bpm, "Tempo, shared by every open sequencer (and Tidepool Acid) so they play in time. The code sets it with setcps (bpm/60/4).");
        _steps.ValueChanged += (_, _) =>
        {
            if (_loading || _steps.Value is not { } v) return;
            PushUndo();
            Seq.Length = Math.Clamp((int)v, 1, SequenceBase.MaxSteps);
            Redraw();
            Committed();
        };
        ToolTip.SetTip(_steps, "Length in sixteenths: 16 = one bar = one Tidal cycle, 32 = two cycles (the code adds slow 2).");
        var swing = new Knob
        {
            Label = "Swing", Minimum = 0, Maximum = 0.5, Default = 0, Format = v => $"{v * 200:0}%",
            Accent = PianoRollView.NoteColor, Width = 46, Height = 52,
        };
        swing.Value = Seq.Swing;
        _swing = swing;
        swing.ValueChanged += v => { Seq.Swing = v; Edited(); };
        swing.Committed += Committed;
        ToolTip.SetTip(swing, "Delays every second sixteenth (Tidal: swingBy).");

        var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 4) };
        top.Children.Add(_play);
        top.Children.Add(SynthWindow.Labelled("BPM", _bpm));
        top.Children.Add(SynthWindow.Labelled("Steps", _steps));
        top.Children.Add(swing);
        top.Children.Add(SynthWindow.Gap(10));

        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6) };
        if (IsMelodic) BuildMelodyTools(top, tools);
        else BuildBeatTools(tools);
        var undo = SynthWindow.SmallButton("↶ Undo", "Undo (Ctrl+Z)");
        undo.Click += (_, _) => Undo();
        var redo = SynthWindow.SmallButton("↷ Redo", "Redo (Ctrl+Y)");
        redo.Click += (_, _) => Redo();
        tools.Children.Add(SynthWindow.Gap(8));
        tools.Children.Add(undo);
        tools.Children.Add(redo);

        Control editor;
        if (_roll is not null)
        {
            _rollScroll.Content = _roll;
            _rollScroll.ScrollChanged += (_, _) => { _roll.HeaderOffset = _rollScroll.Offset.Y; _roll.InvalidateVisual(); };
            editor = new Border { Child = _rollScroll, Margin = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(4), ClipToBounds = true };
        }
        else editor = new ScrollViewer { Content = _grid, Margin = new Thickness(12, 0, 12, 0) };

        // Code panel.
        _orbit.ItemsSource = Enumerable.Range(1, 12).Select(i => $"d{i}").ToList();
        _orbit.SelectionChanged += (_, _) =>
        {
            if (_loading || _orbit.SelectedIndex < 0) return;
            Seq.Orbit = _orbit.SelectedIndex + 1;
            UpdateCode();
            Store();
        };
        ToolTip.SetTip(_orbit, "The Tidal channel the code plays on: give every part its own (d1 drums, d2 bass, …).");
        var copy = SynthWindow.AccentButton("📋 Copy code", "Copy the Tidal code to the clipboard");
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_code.Text ?? "");
            SetStatus("Code copied — paste it into a script and run it with Ctrl+Enter.");
        };
        var codeBar = new WrapPanel { Orientation = Orientation.Horizontal };
        codeBar.Children.Add(new TextBlock
        {
            Text = "TIDAL CODE", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = SynthWindow.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        codeBar.Children.Add(SynthWindow.Labelled("Channel", _orbit));
        codeBar.Children.Add(copy);
        if (_insertCode is not null)
        {
            var insert = SynthWindow.SmallButton("Insert into script", "Put the code in the current script, below the block the cursor is in");
            insert.Click += async (_, _) => await _insertCode(_code.Text ?? "", false);
            var run = SynthWindow.SmallButton("Insert and run", "Put the code in the current script and run it in Tidal (stop the preview here first, or both play)");
            run.Click += async (_, _) => { StopPlaying(); await _insertCode(_code.Text ?? "", true); };
            codeBar.Children.Add(insert);
            codeBar.Children.Add(run);
        }
        var codePanel = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8, 12, 4) };
        codePanel.Children.Add(codeBar);
        codePanel.Children.Add(_code);
        codePanel.Children.Add(_hint);

        var statusBar = new Border
        {
            BorderBrush = SynthWindow.BorderBrush2, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Border { Child = _status, Margin = new Thickness(12, 6, 12, 8) },
        };
        SetStatus(IsMelodic
            ? "Click the roll to add a note and drag for its length. Drag notes to move them, their right edge to resize; right-click deletes. Space plays."
            : "Click cells to add hits and drag to paint. Right-click a hit for soft / normal / accent. Space plays.");

        var bottom = new StackPanel();
        bottom.Children.Add(codePanel);
        bottom.Children.Add(statusBar);
        var head = new StackPanel();
        head.Children.Add(top);
        head.Children.Add(tools);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(bottom);
        root.Children.Add(editor);
        return root;
    }

    private void BuildMelodyTools(WrapPanel top, WrapPanel tools)
    {
        var m = _melody!;
        var ends = new[] { ("Legato", NoteEnd.Legato), ("Whole sample", NoteEnd.Full), ("Cut (mono)", NoteEnd.Cut) };
        var end = _end = new ComboBox { ItemsSource = ends.Select(e => e.Item1).ToList(), SelectedIndex = Array.FindIndex(ends, e => e.Item2 == m.End), FontSize = 12, MinWidth = 120 };
        end.SelectionChanged += (_, _) =>
        {
            if (_loading || end.SelectedIndex < 0) return;
            PushUndo();
            m.End = ends[end.SelectedIndex].Item2;
            Committed();
        };
        ToolTip.SetTip(end, "How notes end. Legato: at the end of the note (# legato 1). Whole sample: the full sample with its release. " +
            "Cut: when the next note starts, like a mono synth (# cut 1).");

        var keys = Enumerable.Range(0, 12).Select(i => SynthPatch.NoteName(i + 60)[..^1].ToUpperInvariant().Replace("S", "#")).ToList();
        var key = _key = new ComboBox { ItemsSource = keys, SelectedIndex = ((m.Root % 12) + 12) % 12, FontSize = 12, MinWidth = 64 };
        key.SelectionChanged += (_, _) =>
        {
            if (_loading || key.SelectedIndex < 0) return;
            m.Root = 60 + key.SelectedIndex;
            _roll!.InvalidateVisual();
            Store();
        };
        ToolTip.SetTip(key, "Key: its rows are highlighted, and Random / Mutate stay in it");
        var scales = Enum.GetValues<AcidScale>().ToList();
        var scale = _scale = new ComboBox
        {
            ItemsSource = scales.Select(s => s == AcidScale.MinorPentatonic ? "Minor pentatonic" : s.ToString()).ToList(),
            SelectedIndex = scales.IndexOf(m.Scale), FontSize = 12, MinWidth = 120,
        };
        scale.SelectionChanged += (_, _) =>
        {
            if (_loading || scale.SelectedIndex < 0) return;
            m.Scale = scales[scale.SelectedIndex];
            _roll!.InvalidateVisual();
            Store();
        };
        ToolTip.SetTip(scale, "Scale: its notes are highlighted in the roll");
        var lengths = new[] { ("1/16", 1), ("1/8", 2), ("1/4", 4), ("1/2", 8), ("1 bar", 16) };
        var draw = new ComboBox { ItemsSource = lengths.Select(l => l.Item1).ToList(), SelectedIndex = 1, FontSize = 12, MinWidth = 76 };
        draw.SelectionChanged += (_, _) => { if (draw.SelectedIndex >= 0) _roll!.DrawLength = lengths[draw.SelectedIndex].Item2; };
        ToolTip.SetTip(draw, "Length of new notes (drag while placing a note to change it)");

        top.Children.Add(SynthWindow.Labelled("Notes end", end));
        top.Children.Add(SynthWindow.Labelled("Key", key));
        top.Children.Add(SynthWindow.Labelled("Scale", scale));
        top.Children.Add(SynthWindow.Labelled("New note", draw));


        void Tool(string text, string tip, Action<MelodySequence> action)
        {
            var b = SynthWindow.SmallButton(text, tip);
            b.Click += (_, _) => EditMelody(action);
            tools.Children.Add(b);
        }
        Tool("🎲 Random", "A new random melody in the key and scale (R)", s => SequenceIdeas.RandomMelody(s, _random));
        Tool("Mutate", "Change a few notes (M)", s => SequenceIdeas.Mutate(s, _random));
        Tool("◀", "Move everything one step earlier", s => Shift(s, -1));
        Tool("▶", "Move everything one step later", s => Shift(s, 1));
        Tool("−1", "Transpose down a semitone", s => Transpose(s, -1));
        Tool("+1", "Transpose up a semitone", s => Transpose(s, 1));
        Tool("−oct", "Transpose down an octave", s => Transpose(s, -12));
        Tool("+oct", "Transpose up an octave", s => Transpose(s, 12));
        Tool("Double", "Make the sequence twice as long, playing it twice (then change the second half)", s =>
        {
            if (s.Length * 2 > SequenceBase.MaxSteps) return;
            s.Notes.AddRange(s.Playing.Select(n => new SeqNote { Step = n.Step + s.Length, Length = n.Length, Midi = n.Midi }).ToList());
            s.Length *= 2;
        });
        Tool("Clear", "Remove every note", s => s.Notes.Clear());
    }

    private void BuildBeatTools(WrapPanel tools)
    {
        void Tool(string text, string tip, Action<BeatSequence> action)
        {
            var b = SynthWindow.SmallButton(text, tip);
            b.Click += (_, _) => EditBeat(action);
            tools.Children.Add(b);
        }
        Tool("🎲 Random", "A new random beat (R)", s => SequenceIdeas.RandomBeat(s, _drums.Select(d => (d.Drum, d.Model)).ToList(), _random));
        Tool("Basic beat", "Kick on every beat, snare on 2 and 4, hats in eighths", s =>
        {
            var basic = BeatSequence.Default(_drums.Select(d => d.Drum));
            s.Rows = basic.Rows;
        });
        Tool("◀", "Move everything one step earlier", s => ShiftBeat(s, -1));
        Tool("▶", "Move everything one step later", s => ShiftBeat(s, 1));
        Tool("Double", "Make the beat twice as long, playing it twice (then change the second half)", s =>
        {
            if (s.Length * 2 > SequenceBase.MaxSteps) return;
            foreach (var row in s.Rows.Values) Array.Copy(row, 0, row, s.Length, s.Length);
            s.Length *= 2;
        });
        Tool("Clear", "Remove every hit", s => { foreach (var row in s.Rows.Values) Array.Clear(row); });
    }

    private static void Shift(MelodySequence s, int by)
    {
        foreach (var n in s.Notes) n.Step = ((n.Step + by) % s.Length + s.Length) % s.Length;
    }

    private static void ShiftBeat(BeatSequence s, int by)
    {
        foreach (var row in s.Rows.Values)
        {
            var copy = row.Take(s.Length).ToArray();
            for (int i = 0; i < s.Length; i++) row[((i + by) % s.Length + s.Length) % s.Length] = copy[i];
        }
    }

    private static void Transpose(MelodySequence s, int by)
    {
        if (s.Notes.Any(n => n.Midi + by < PianoRollView.LowestMidi || n.Midi + by > PianoRollView.HighestMidi)) return;
        foreach (var n in s.Notes) n.Midi += by;
        s.Root = ((s.Root + by) % 12 + 12) % 12 + 60;
    }

    private void LoadControls()
    {
        _loading = true;
        try
        {
            _bpm.Value = (decimal)Seq.Bpm;
            _steps.Value = Seq.Length;
            _orbit.SelectedIndex = Seq.Orbit - 1;
            if (_swing is not null) _swing.Value = Seq.Swing;
            if (_melody is not null)
            {
                if (_end is not null) _end.SelectedIndex = (int)_melody.End;
                if (_key is not null) _key.SelectedIndex = ((_melody.Root % 12) + 12) % 12;
                if (_scale is not null) _scale.SelectedIndex = Enum.GetValues<AcidScale>().ToList().IndexOf(_melody.Scale);
            }
        }
        finally { _loading = false; }
    }

    private void ScrollToNotes()
    {
        if (_roll is null || _melody is null) return;
        int center = _melody.Notes.Count > 0 ? (int)_melody.Notes.Average(n => n.Midi) : _melody.Root + 6;
        double y = PianoRollView.RowY(center) - _rollScroll.Bounds.Height / 2;
        _rollScroll.Offset = new Vector(0, Math.Max(0, y));
    }

    // ------------------------------------------------------------------ editing

    private void PushUndo()
    {
        _undo.Add(IsMelodic ? _melody!.ToJson() : _beat!.ToJson());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Undo() => Restore(_undo, _redo);
    private void Redo() => Restore(_redo, _undo);

    private void Restore(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        to.Add(IsMelodic ? _melody!.ToJson() : _beat!.ToJson());
        string json = from[^1];
        from.RemoveAt(from.Count - 1);
        if (IsMelodic) _melody = MelodySequence.FromJson(json);
        else _beat = BeatSequence.FromJson(json);
        ShareTempo();
        LoadControls();
        Redraw();
        Committed();
    }

    private void EditMelody(Action<MelodySequence> change)
    {
        PushUndo();
        change(_melody!);
        _melody!.Repair();
        LoadControls();
        Redraw();
        Committed();
    }

    private void EditBeat(Action<BeatSequence> change)
    {
        PushUndo();
        change(_beat!);
        _beat!.Repair();
        LoadControls();
        Redraw();
        Committed();
    }

    private void Redraw()
    {
        _roll?.InvalidateVisual();
        _grid?.InvalidateVisual();
    }

    /// <summary>A change in progress: heard right away while playing.</summary>
    private void Edited()
    {
        UpdateCode();
        if (_playing) Render();
    }

    /// <summary>A change is finished: update the loop and remember the sequence.</summary>
    private void Committed()
    {
        Edited();
        Store();
    }

    private void Store() => _store(IsMelodic ? _melody!.ToJson() : _beat!.ToJson());

    private void UpdateCode()
    {
        if (_melodicSource is not null)
        {
            var snap = _melodicSource();
            _code.Text = SequenceCode.Melody(_melody!, snap.Sound, snap.SampleMidis);
            string samples = string.Join(", ", snap.SampleMidis.Select((m, i) => $"n {i} = {SynthPatch.NoteName(m)}"));
            var mids = snap.SampleMidis.Count > 0 ? snap.SampleMidis : new[] { 60 };
            int farthest = SequenceCode.Units(_melody!, mids).SelectMany(u => u.Midis.Select(m => Math.Abs(m - mids[u.Sample]))).DefaultIfEmpty(0).Max();
            string far = farthest > 12
                ? $"⚠ Some notes are {farthest} semitones from the closest rendered note, so they sound sped up or slowed down: " +
                  $"add notes near them to the Notes box in {_synthName} (e.g. c4 c5) and save. "
                : "";
            _hint.Text = !SampleExport.IsValidName(snap.Sound)
                ? $"\"{snap.Sound}\" can't be used as a sound name in Tidal: change the name in {_synthName} first."
                : far + $"Plays the saved samples of \"{snap.Sound}\" ({samples}); every note uses the closest one and note shifts its pitch. " +
                  $"Save the sound (💾) in {_synthName} after changing it, so SuperDirt has the samples you hear here.";
        }
        else if (_drumSource is not null)
        {
            _code.Text = SequenceCode.Beat(_beat!, _drums.Select(d => (d.Drum, d.Sound)).ToList());
            var kit = _drumSource().Kit;
            _hint.Text = !SampleExport.IsValidName(kit)
                ? $"\"{kit}\" can't be used as a kit name in Tidal: change the name in {_synthName} first."
                : $"Plays the kit's sounds ({string.Join(", ", _drums.Take(4).Select(d => d.Sound))}{(_drums.Count > 4 ? ", …" : "")}). " +
                  $"Save the kit (💾) in {_synthName} after changing it, so SuperDirt has the sounds you hear here.";
        }
    }

    // ------------------------------------------------------------------ playing

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || e.Source is NumericUpDown) return;
        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Space) { e.Handled = true; TogglePlay(); }
            else if (e.Key == Key.R) { e.Handled = true; Randomise(); }
            else if (e.Key == Key.M && IsMelodic) { e.Handled = true; EditMelody(s => SequenceIdeas.Mutate(s, _random)); }
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) { e.Handled = true; Undo(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y) { e.Handled = true; Redo(); }
    }

    private void TogglePlay()
    {
        if (_playing) StopPlaying();
        else Render();
    }

    private void StopPlaying()
    {
        ++_renderVersion;
        Transport.Stop(this);
        _playing = false;
        _playheadTimer.Stop();
        if (_roll is not null) _roll.PlayStep = -1;
        if (_grid is not null) _grid.PlayStep = -1;
        Redraw();
        _play.Content = "▶ Play";
    }

    /// <summary>
    /// Renders the loop and plays it; while it already plays, the new loop takes over at the same position.
    /// One render at a time: changes made meanwhile are rendered right after, with the latest settings.
    /// </summary>
    private async void Render()
    {
        if (_rendering) { _renderAgain = true; return; }
        _rendering = true;
        try
        {
            do
            {
                _renderAgain = false;
                int version = _renderVersion;
                int steps = Seq.Length; // what the render below uses
                RenderedNote loop;
                try { loop = await RenderLoopAsync(); }
                catch (Exception e) { SetStatus($"Could not render the sequence: {e.Message}"); StopPlaying(); return; }
                if (version != _renderVersion) continue; // stopped meanwhile
                if (Transport.Play(this, loop, steps) is { } err) { StopPlaying(); SetStatus(err); return; }
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

    /// <summary>The tempo, for the self-test; setting it works like typing in the BPM box.</summary>
    internal double Tempo
    {
        get => Seq.Bpm;
        set => _bpm.Value = (decimal)value;
    }

    /// <summary>The generated code, for the self-test.</summary>
    internal string CodeText => _code.Text ?? "";

    /// <summary>Replaces the sequence with a random one (the R key), for the self-test.</summary>
    internal void Randomise()
    {
        if (IsMelodic) EditMelody(s => SequenceIdeas.RandomMelody(s, _random));
        else EditBeat(s => SequenceIdeas.RandomBeat(s, _drums.Select(d => (d.Drum, d.Model)).ToList(), _random));
    }

    internal Task<RenderedNote> RenderLoopAsync()
    {
        if (_melodicSource is not null)
        {
            var seq = _melody!.Clone();
            var snap = _melodicSource();
            var cache = _noteCache;
            var mids = snap.SampleMidis.Count > 0 ? snap.SampleMidis : new[] { 60 };
            var needed = SequenceCode.Units(seq, mids).Select(u => mids[u.Sample]).Distinct().ToList();
            return Task.Run(() =>
            {
                var samples = new List<RenderedNote>();
                foreach (int midi in needed)
                {
                    samples.Add(cache.GetOrAdd(midi, m => snap.Render(m)));
                }
                int sr = samples.FirstOrDefault()?.SampleRate ?? 48000;
                return SequenceRenderer.Melody(seq, samples, sr);
            });
        }
        else
        {
            var seq = _beat!.Clone();
            var snap = _drumSource!();
            var cache = _drumCache;
            var needed = snap.Drums.Select(d => d.Drum).Where(d => !seq.Muted.Contains(d) && seq.HasHits(d)).ToList();
            return Task.Run(() =>
            {
                var hits = new Dictionary<string, RenderedNote>();
                foreach (var drum in needed)
                {
                    if (cache.GetOrAdd(drum, d => snap.Render(d)) is { } note) hits[drum] = note;
                }
                int sr = hits.Values.FirstOrDefault()?.SampleRate ?? 48000;
                return SequenceRenderer.Beat(seq, hits, sr);
            });
        }
    }

    private void UpdatePlayhead()
    {
        int step = Transport.StepIn(this) is { } s ? Seq.StepAt(s * Seq.StepSeconds) : -1;
        if (_roll is not null && _roll.PlayStep != step) { _roll.PlayStep = step; _roll.InvalidateVisual(); }
        if (_grid is not null && _grid.PlayStep != step) { _grid.PlayStep = step; _grid.InvalidateVisual(); }
    }

    private async void AuditionNote(int midi)
    {
        if (_melodicSource is null) return;
        int version = ++_auditionVersion;
        var snap = _melodicSource();
        var note = await Task.Run(() => snap.Render(midi));
        if (version != _auditionVersion) return;
        _audition.Play(note);
    }

    private async void AuditionDrum(string drum)
    {
        if (_drumSource is null) return;
        int version = ++_auditionVersion;
        var cache = _drumCache;
        var snap = _drumSource();
        var note = await Task.Run(() => cache.GetOrAdd(drum, d => snap.Render(d)));
        if (version != _auditionVersion || note is null) return;
        _audition.Play(note);
    }

    private void SetStatus(string text) => _status.Text = text;
}
