using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using TidalStudio.App.Editor;
using TidalStudio.App.Explorer;
using TidalStudio.App.Services;
using TidalStudio.App.Visualizers;
using TidalStudio.Core.Files;
using TidalStudio.Core.Settings;
using TidalStudio.Core.SuperCollider;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly ConsoleController _console;
    private readonly MiniNotationRenderer _miniRenderer;
    private readonly EvalFlashRenderer _flashRenderer = new();
    private readonly ErrorLineRenderer _errorRenderer = new();
    private readonly InlineVisualGenerator _inlineGenerator;
    private readonly HashSet<string> _muted = new();
    private readonly List<VisualizerTile> _tiles = new();
    private readonly List<DocumentTab> _tabs = new();
    private readonly CompletionCatalog _completionCatalog;
    private readonly CompletionController _completion;

    private DocumentTab? _active;

    private enum DirtState { Unknown, Running, Starting, NotRunning }
    private DirtState _dirtState = DirtState.Unknown;
    private DateTime _lastDirtProbe = DateTime.MinValue;
    private bool _dirtHintShown;
    private int _untitledCounter;
    private bool _bootSupportsVisuals;
    private bool _closeConfirmed;
    private string? _bootDescription;
    private int _frame;
    private int _runCount;
    private string _lastRunLines = "";
    private Stopwatch? _loadingWatch;
    private volatile bool _tidalPrinted;
    /// <summary>--selftest with --allow-restart: let the synth check restart the real SuperCollider.</summary>
    private bool _selfTestAllowRestart;

    // Parameterless constructor for the XAML previewer.
    public MainWindow() : this(new AppServices(), Array.Empty<string>()) { }

    public MainWindow(AppServices services, string[] args)
    {
        _services = services;
        _settings = services.Settings;
        InitializeComponent();
        Synth.SynthWindow.OpenExampleScript = OpenExampleScript;

        _console = new ConsoleController(ConsoleView);
        _miniRenderer = new MiniNotationRenderer(services, () => Editor.Document);
        _inlineGenerator = new InlineVisualGenerator(services);
        _completionCatalog = new CompletionCatalog(services);
        _completion = new CompletionController(Editor, _completionCatalog) { Enabled = _settings.Autocomplete };

        SetupEditor();
        SetupMenus();
        RestoreWindow();
        BuildVisualizerPanel();

        _services.Ghci.Output += line => _console.Add(line);
        _services.Ghci.StateChanged += _ => Dispatcher.UIThread.Post(UpdateStatus);
        _services.SuperDirt.Output += line => _console.Add(line with { Text = "[sc] " + line.Text });
        _services.SuperDirt.Exited += () => Dispatcher.UIThread.Post(() => { if (_dirtState != DirtState.Starting) SetDirtState(DirtState.NotRunning); });
        _services.Osc.Error += msg => _console.Add(OutputKind.Error, msg);
        _services.Ghci.Output += line =>
        {
            if (line.Kind == OutputKind.Output && line.Text.Contains("TidalCycles version", StringComparison.Ordinal)) _tidalPrinted = true;
        };

        Explorer.FileOpenRequested += OnExplorerOpen;
        Explorer.WorkingDirectoryRequested += dir => _ = SetWorkingDirectoryAsync(dir);
        Explorer.NewFileRequested += OnExplorerNewFile;
        Explorer.NewFolderRequested += OnExplorerNewFolder;
        Explorer.DeleteRequested += OnExplorerDelete;
        Explorer.ChooseFolderRequested += () => _ = ChooseWorkingDirectoryAsync();

        Tabs.Activated += ActivateTab;
        Tabs.CloseRequested += tab => _ = CloseTabAsync(tab);
        Tabs.CloseOthersRequested += tab => _ = CloseTabsAsync(_tabs.Where(t => t != tab).ToList());
        Tabs.CloseAllRequested += () => _ = CloseTabsAsync(_tabs.ToList());
        Tabs.NewRequested += () => NewDocument(withSample: false);

        Opened += async (_, _) => await OnOpenedAsync(args);
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    // ------------------------------------------------------------------ startup

    private async Task OnOpenedAsync(string[] args)
    {
        _services.Ticker.Attach(this);
        _services.Ticker.Subscribe(OnFrame);

        int shots = Array.IndexOf(args, "--sequencer-shots");
        if (shots >= 0 && shots + 1 < args.Length)
        {
            NewDocument(withSample: false);
            _ = RunSequencerShotsAsync(args[shots + 1]);
            return;
        }

        int selfTest = Array.IndexOf(args, "--selftest");
        _selfTestAllowRestart = selfTest >= 0 && args.Contains("--allow-restart");
        string? selfTestDir = selfTest >= 0 && selfTest + 1 < args.Length ? args[selfTest + 1] : null;

        string? argFile = args.FirstOrDefault(File.Exists);
        string? lastFile = selfTestDir is null && _settings.LastFile is { } last && File.Exists(last) ? last : null;
        string? file = argFile ?? lastFile;
        string workDir = _settings.WorkingDirectory is { } wd && Directory.Exists(wd) ? wd
            : file is not null ? Path.GetDirectoryName(Path.GetFullPath(file))!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        await SetWorkingDirectoryAsync(workDir, announce: false);

        // Reopen last session's tabs (not in self-test mode), then the file from the command line.
        if (selfTestDir is null)
            foreach (var open in _settings.OpenFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                OpenFile(open, activate: false);
        if (file is not null) OpenFile(file);
        else if (_tabs.Count > 0) ActivateTab(_tabs[0]);
        else NewDocument(withSample: true);

        // Start ghci before anything else: it is by far the slowest part of starting up (GHC alone needs ~5 s on
        // Windows before it reads any input), so everything below runs while it loads.
        bool startTidal = selfTestDir is not null || _settings.StartTidalOnLaunch;
        if (startTidal && selfTestDir is null) ShowLoading();
        Task tidal = startTidal ? StartTidalAsync() : Task.CompletedTask;
        // SuperDirt makes the sound; start it alongside Tidal if it isn't running (not in self-test: that runs silently).
        if (selfTestDir is null) _ = EnsureSuperDirtAsync();

        try
        {
            _services.Osc.Start(_settings.EditorOscPort);
            _console.Add(OutputKind.Info, $"Listening for Tidal events on UDP {_settings.EditorOscPort}.");
        }
        catch (Exception e)
        {
            _console.Add(OutputKind.Error, $"Could not listen on UDP port {_settings.EditorOscPort}: {e.Message}. Visualizers will stay empty. (Is another TidalStudio running?)");
        }

        if (args.Contains("--test-signal")) _services.AudioCapture.StartTestSignal();
        else if (_settings.CaptureAudio) StartAudio();

        if (selfTestDir is not null) _ = RunSelfTestAsync(selfTestDir);
        if (!startTidal) _console.Add(OutputKind.Info, "Tidal is not started. Use Tidal ▸ Start / restart Tidal.");
        await tidal;
        HideLoading();
    }

    // ------------------------------------------------------------------ loading screen

    private void ShowLoading()
    {
        _tidalPrinted = false;
        _loadingWatch = Stopwatch.StartNew();
        LoadingOverlay.Opacity = 1;
        LoadingOverlay.IsVisible = true;
        UpdateLoading();
    }

    private async void HideLoading()
    {
        if (_loadingWatch is null) return;
        _loadingWatch = null;
        LoadingOverlay.Opacity = 0;
        await Task.Delay(260);
        if (_loadingWatch is null) LoadingOverlay.IsVisible = false;
        Editor.Focus();
    }

    private void UpdateLoading()
    {
        if (_loadingWatch is null) return;
        double t = _loadingWatch.Elapsed.TotalSeconds;
        double expected = Math.Clamp(_settings.LastTidalStartSeconds, 2, 60);
        // Linear up to the expected time, then creep towards the end so the bar never looks stuck or finished.
        LoadingProgress.Value = t < expected ? 0.9 * t / expected : 0.9 + 0.09 * (1 - Math.Exp(-(t - expected) / expected));
        LoadingStage.Text = _tidalPrinted ? "Loading Tidal…" : "Starting GHC…";
        LoadingDetail.Text = $"{t:0} s — usually about {expected:0} s.\nGHC needs a few seconds to load each time it starts.";
    }

    private void OnSkipLoading(object? sender, RoutedEventArgs e) => HideLoading();

    private void SetupEditor()
    {
        using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(
                   typeof(MainWindow).Assembly.GetManifestResourceNames().First(n => n.EndsWith("Tidal.xshd", StringComparison.Ordinal)))!)
        using (var reader = XmlReader.Create(stream))
        {
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }

        Editor.FontFamily = new FontFamily(_settings.FontFamily);
        Editor.FontSize = _settings.FontSize;
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        Editor.WordWrap = _settings.WordWrap;
        Editor.LineNumbersForeground = new SolidColorBrush(Color.Parse("#4B5263"));
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 2;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(70, 78, 205, 196));
        Editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.Parse("#4ECDC4"));

        var tv = Editor.TextArea.TextView;
        tv.BackgroundRenderers.Add(_errorRenderer);
        tv.BackgroundRenderers.Add(_flashRenderer);
        tv.BackgroundRenderers.Add(_miniRenderer);
        tv.ElementGenerators.Add(_inlineGenerator);

        // Registered on the editor (not its text area) so Shift/Ctrl+Enter run code even while a completion popup
        // is open: the popup listens on the text area, which the tunnelling event reaches later.
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.TextArea.Caret.PositionChanged += (_, _) => ShowErrorForCaretLine();
    }

    private void OnFrame(TimeSpan _)
    {
        _frame++;
        var tv = Editor.TextArea.TextView;
        // Keep redrawing highlights while anything is playing or fading.
        if (_settings.HighlightMiniNotation && (_services.Clock.IsPlaying(_services.Time.Now) || _miniRenderer.LastDrawn > 0) || _flashRenderer.IsAnimating)
            tv.InvalidateLayer(KnownLayer.Background);
        if (_frame % 6 == 0) UpdateStatus();
        if (_loadingWatch is not null && _frame % 3 == 0) UpdateLoading();
    }

    // ------------------------------------------------------------------ keyboard

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Key == Key.Enter && mods == KeyModifiers.Shift)
        {
            e.Handled = true;
            _ = RunLineAsync();
        }
        else if (e.Key == Key.Enter && mods == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = RunBlockAsync();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        bool shift = mods.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Escape && _loadingWatch is not null)
        {
            e.Handled = true;
            HideLoading();
        }
        else if (ctrl && shift && !alt && e.Key == Key.Y)
        {
            e.Handled = true;
            OnOpenSynth(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.U)
        {
            e.Handled = true;
            OnOpenDrums(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.J)
        {
            e.Handled = true;
            OnOpenAcid(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.V)
        {
            e.Handled = true;
            OnOpenVj(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.G)
        {
            e.Handled = true;
            OnMuseSuggest(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.M)
        {
            e.Handled = true;
            OnMuseTrainer(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.K)
        {
            e.Handled = true;
            OnOpenFm(this, new RoutedEventArgs());
        }
        else if (ctrl && shift && !alt && e.Key == Key.E)
        {
            e.Handled = true;
            SetExplorerVisible(!Explorer.IsVisible);
        }
        else if (ctrl && !alt && !shift && e.Key == Key.W)
        {
            e.Handled = true;
            if (_active is not null) _ = CloseTabAsync(_active);
        }
        else if (ctrl && !alt && (e.Key == Key.Tab || e.Key is Key.PageDown or Key.PageUp))
        {
            e.Handled = true;
            CycleTab(e.Key == Key.PageUp || (e.Key == Key.Tab && shift) ? -1 : 1);
        }
        else if (ctrl && alt && e.Key == Key.H || ctrl && !alt && e.Key == Key.OemPeriod)
        {
            e.Handled = true;
            _ = HushAsync();
        }
        else if (ctrl && !alt && !shift && e.Key is >= Key.D1 and <= Key.D9)
        {
            e.Handled = true;
            _ = ToggleMuteAsync((e.Key - Key.D0).ToString(CultureInfo.InvariantCulture));
        }
        else if (ctrl && !alt && e.Key == Key.S)
        {
            e.Handled = true;
            _ = shift ? SaveAsAsync() : SaveAsync();
        }
        else if (ctrl && !alt && !shift && e.Key == Key.O)
        {
            e.Handled = true;
            _ = OpenAsync();
        }
        else if (ctrl && !alt && !shift && e.Key == Key.N)
        {
            e.Handled = true;
            _ = NewAsync();
        }
        else if (ctrl && !alt && (e.Key is Key.OemPlus or Key.Add))
        {
            e.Handled = true;
            ChangeFontSize(1);
        }
        else if (ctrl && !alt && (e.Key is Key.OemMinus or Key.Subtract))
        {
            e.Handled = true;
            ChangeFontSize(-1);
        }
    }

    // ------------------------------------------------------------------ running code

    private async Task RunLineAsync()
    {
        var doc = Editor.Document;
        int line = Editor.TextArea.Caret.Line; // 1-based
        var range = CodeBlocks.GetLine(doc.Text, line - 1);
        if (range is null) return;
        await RunRangeAsync(range.StartLine, range.EndLine);
    }

    private async Task RunBlockAsync()
    {
        var doc = Editor.Document;
        var sel = Editor.TextArea.Selection;
        CodeRange? range;
        if (!sel.IsEmpty && sel.SurroundingSegment is { } seg)
            range = CodeBlocks.GetSelection(doc.Text, seg.Offset, seg.EndOffset);
        else
            range = CodeBlocks.GetBlock(doc.Text, Editor.TextArea.Caret.Line - 1);
        if (range is null) return;
        await RunRangeAsync(range.StartLine, range.EndLine);
    }

    /// <summary>Runs whole lines [firstLine, lastLine] (0-based) of the document.</summary>
    private async Task RunRangeAsync(int firstLine, int lastLine)
    {
        _runCount++;
        _completion.Close();
        _lastRunLines = $"{firstLine + 1}-{lastLine + 1}";
        var doc = Editor.Document;
        var first = doc.GetLineByNumber(firstLine + 1);
        var last = doc.GetLineByNumber(lastLine + 1);
        int start = first.Offset;
        string raw = doc.GetText(start, last.EndOffset - start);

        // Without SuperDirt nothing is heard, and nothing reports an error: check (cheaply) and start it if needed.
        _ = EnsureSuperDirtAsync();

        if (!_services.Ghci.IsReady)
        {
            _flashRenderer.Flash(doc, start, raw.Length, success: false);
            if (_services.Ghci.State == GhciState.Starting)
                SetMessage("Tidal is still starting…");
            else
            {
                SetMessage("Tidal is not running — starting it now. Run the code again when it is ready.");
                _ = StartTidalAsync();
            }
            return;
        }

        string code = raw;
        bool wrapped = false;
        IReadOnlyList<LiteralRef> literals = Array.Empty<LiteralRef>();
        if (_bootSupportsVisuals && _settings.HighlightMiniNotation)
        {
            var pre = Preprocessor.Wrap(raw, _services.Literals.NextId);
            literals = pre.Literals;
            foreach (var lit in pre.Literals) _services.Literals.Register(doc, lit.Id, start + lit.Offset, lit.ContentLength);
            code = pre.Code;
            wrapped = pre.Literals.Count > 0;
        }

        EchoInput(raw);
        var result = await _services.Ghci.EvalAsync(code);
        if (!result.Success && wrapped && result.ErrorText.Contains("TsCtx", StringComparison.Ordinal))
        {
            // A string literal ended up somewhere tsctx can't go (e.g. a custom IsString type): run it unchanged.
            _console.Add(OutputKind.Info, "(highlighting is not available for this code; running it unchanged)");
            result = await _services.Ghci.EvalAsync(raw);
            literals = Array.Empty<LiteralRef>();
        }
        _services.CodeLog.Add(raw, result.Success, literals, _services.Time.Now);

        _flashRenderer.Flash(doc, start, raw.Length, result.Success);
        _errorRenderer.ClearRange(doc, start, start + raw.Length);
        if (!result.Success)
        {
            var marks = result.Errors.Select(err => (LineNumber: err.Line >= 0 ? firstLine + 1 + err.Line : firstLine + 1, err.Message)).ToList();
            if (marks.Count == 0) marks.Add((firstLine + 1, result.ErrorText));
            _errorRenderer.Set(doc, marks);
            SetMessage(FirstLine(marks[0].Message));
        }
        else
        {
            SetMessage("");
        }
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void EchoInput(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        // Show the first line of actual code rather than a heading comment.
        string first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)) ?? lines[0].Trim();
        string shown = first + (lines.Length > 1 ? $"  … ({lines.Length} lines)" : "");
        _console.Add(OutputKind.Input, shown);
    }

    private async Task HushAsync()
    {
        _muted.Clear();
        if (!_services.Ghci.IsReady) return;
        EchoInput("hush");
        _services.CodeLog.Hush(_services.Time.Now);
        await _services.Ghci.EvalAsync("hush");
    }

    private async Task<EvalResult> ToggleMuteAsync(string id)
    {
        if (!_services.Ghci.IsReady) return EvalResult.NotRunning;
        bool mute = _muted.Add(id);
        if (!mute) _muted.Remove(id);
        string cmd = mute ? $"mute \"{id}\"" : $"unmute \"{id}\"";
        EchoInput(cmd);
        var result = await _services.Ghci.EvalAsync(cmd);
        if (!result.Success) SetMessage(FirstLine(result.ErrorText));
        UpdateStatus();
        return result;
    }

    private async Task StartTidalAsync()
    {
        string? ghci = ToolLocator.FindGhci(_settings.GhciPath);
        if (ghci is null)
        {
            _console.Add(OutputKind.Error, "Could not find ghci. Install GHC + Tidal, or choose ghci via Tidal ▸ Choose ghci….");
            return;
        }
        string? workDir = _settings.WorkingDirectory;
        var boot = BootFiles.Choose(_settings.BootFilePath, CurrentPath, _settings.UseBuiltInBootFile, workDir);
        _bootSupportsVisuals = boot.SupportsVisuals;
        _bootDescription = boot.Description;
        _console.Add(OutputKind.Info, $"Boot file: {boot.Description}");
        if (!boot.SupportsVisuals)
            _console.Add(OutputKind.Info,
                "This boot file has no TidalStudio additions, so visualizers and highlighting get no events. " +
                "Use Tidal ▸ Boot file ▸ Always use the built-in boot file, or write TidalStudio's boot file next to your .tidal file.");

        _muted.Clear();
        _services.Literals.Clear();
        _services.Clock.Reset();
        try
        {
            var watch = Stopwatch.StartNew();
            await _services.Ghci.StartAsync(ghci, boot.Text, workDir, TimeSpan.FromSeconds(90));
            _settings.LastTidalStartSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
            await _completionCatalog.LoadFromTidalAsync();
            if (_completionCatalog.TidalNameCount > 0)
                _console.Add(OutputKind.Info, $"Autocomplete: {_completionCatalog.TidalNameCount} Tidal functions loaded (Ctrl+Space to show suggestions).");
            // The working directory may have changed while ghci was starting.
            if (_settings.WorkingDirectory is { } now && !string.Equals(now, workDir, StringComparison.OrdinalIgnoreCase))
                await _services.Ghci.EvalAsync(GhciHost.SetDirectoryCommand(now));
        }
        catch (Exception e)
        {
            _console.Add(OutputKind.Error, $"Tidal failed to start: {e.Message}");
        }
        UpdateStatus();
    }

    // ------------------------------------------------------------------ files

    private const string SampleCode =
        """
        -- Welcome to TidalStudio
        --   Shift+Enter  run the current line        Ctrl+Enter  run the block (or selection)
        --   Ctrl+.  or  Ctrl+Alt+H  hush            Ctrl+1..9   mute / unmute d1..d9
        -- Playing parts of the mini-notation light up while they sound.
        -- Add a comment like  -- _punchcard  to a block to see an inline visual for it.

        setcps (128/60/4)

        d1 $ s "bd*4" # gain 1.1
          -- _punchcard

        d2 $ s "~ hh ~ hh" # n "<0 3>"

        d3 $ n "0 [3 7] <5 10> 12" # s "superpiano" # legato 0.8
          # color "violet"
          -- _pianoroll cycles=2

        hush
        """;

    /// <summary>Path of the file in the active tab (null for an untitled tab).</summary>
    private string? CurrentPath => _active?.Path;

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private DocumentTab CreateTab(TextDocument document, string? path, string? untitledName = null)
    {
        var tab = new DocumentTab(document, path, path is not null ? "" : untitledName ?? $"untitled-{++_untitledCounter}");
        document.TextChanged += (_, _) =>
        {
            // The VJ screen shows the block being typed (only while it is open: it costs a scan of the text).
            if (_vj is not null && ReferenceEquals(Editor.Document, document))
            {
                var block = CodeBlocks.GetBlock(document.Text, Editor.TextArea.Caret.Line - 1);
                if (block is not null)
                {
                    var first = document.GetLineByNumber(block.StartLine + 1);
                    var last = document.GetLineByNumber(block.EndLine + 1);
                    _services.CodeLog.SetDraft(document.GetText(first.Offset, last.EndOffset - first.Offset), _services.Time.Now);
                }
            }
            if (tab.Dirty) return;
            tab.Dirty = true;
            if (tab == _active) UpdateTitle();
            RefreshTabs();
        };
        return tab;
    }

    /// <summary>Adds a tab after the active one.</summary>
    private void AddTab(DocumentTab tab, bool activate = true)
    {
        int index = _active is null ? _tabs.Count : _tabs.IndexOf(_active) + 1;
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        if (activate) ActivateTab(tab);
        else RefreshTabs();
    }

    private void ActivateTab(DocumentTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        if (tab == _active && ReferenceEquals(Editor.Document, tab.Document))
        {
            Editor.Focus();
            return;
        }
        _completion.Close();
        if (_active is not null && _tabs.Contains(_active) && ReferenceEquals(Editor.Document, _active.Document))
        {
            _active.CaretOffset = Editor.CaretOffset;
            _active.VerticalOffset = Editor.VerticalOffset;
        }
        _active = tab;
        Editor.Document = tab.Document;
        Editor.CaretOffset = Math.Min(tab.CaretOffset, tab.Document.TextLength);
        double scroll = tab.VerticalOffset;
        Dispatcher.UIThread.Post(() => { if (_active == tab) Editor.ScrollToVerticalOffset(scroll); }, DispatcherPriority.Loaded);
        if (tab.Path is not null) _settings.LastFile = tab.Path;
        Explorer.SetCurrentFile(tab.Path);
        UpdateTitle();
        RefreshTabs();
        Editor.Focus();
    }

    private void RefreshTabs() => Tabs.Update(_tabs, _active);

    private void CycleTab(int direction)
    {
        if (_tabs.Count < 2 || _active is null) return;
        int i = _tabs.IndexOf(_active);
        ActivateTab(_tabs[((i + direction) % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }

    private void NewDocument(bool withSample)
    {
        var tab = CreateTab(new TextDocument(withSample ? SampleCode.Replace("\r\n", "\n") : ""), null);
        AddTab(tab);
    }

    /// <summary>Opens a file in a new tab, or switches to its tab when it is already open.</summary>
    private void OpenFile(string path, bool activate = true)
    {
        path = Path.GetFullPath(path);
        if (_tabs.FirstOrDefault(t => SamePath(t.Path, path)) is { } existing)
        {
            if (activate) ActivateTab(existing);
            return;
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            _console.Add(OutputKind.Error, $"Could not open {path}: {e.Message}");
            return;
        }
        var tab = CreateTab(new TextDocument(text) { FileName = path }, path);
        // An untouched untitled tab (like the welcome sample) is replaced instead of piling up.
        var pristine = activate && _active is { Path: null, Dirty: false } ? _active : null;
        AddTab(tab, activate);
        if (pristine is not null) RemoveTab(pristine);
        _settings.AddRecent(path);
        RebuildRecentMenu();
    }

    private async Task<bool> SaveAsync(DocumentTab? tab = null)
    {
        tab ??= _active;
        if (tab is null) return false;
        if (tab.Path is null) return await SaveAsAsync(tab);
        try
        {
            await File.WriteAllTextAsync(tab.Path, tab.Document.Text, new UTF8Encoding(false));
            tab.Dirty = false;
            UpdateTitle();
            RefreshTabs();
            SetMessage($"Saved {Path.GetFileName(tab.Path)}");
            return true;
        }
        catch (Exception e)
        {
            await Dialogs.ShowMessage(this, "Save failed", e.Message);
            return false;
        }
    }

    private async Task<bool> SaveAsAsync(DocumentTab? tab = null)
    {
        tab ??= _active;
        if (tab is null) return false;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Tidal file",
            DefaultExtension = "tidal",
            SuggestedFileName = tab.Path is null ? tab.UntitledName + ".tidal" : Path.GetFileName(tab.Path),
            FileTypeChoices = new[] { TidalFileType, FilePickerFileTypes.All },
            SuggestedStartLocation = await StartFolderAsync(tab.Path is null ? null : Path.GetDirectoryName(tab.Path)),
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return false;
        // Saving over a file that is open in another tab: that tab is now stale, so close it.
        foreach (var other in _tabs.Where(t => t != tab && SamePath(t.Path, path)).ToList()) RemoveTab(other);
        tab.Path = path;
        tab.Document.FileName = path;
        _settings.LastFile = path;
        _settings.AddRecent(path);
        RebuildRecentMenu();
        if (tab == _active) Explorer.SetCurrentFile(path);
        return await SaveAsync(tab);
    }

    /// <summary>Asks to save a modified tab. Returns false when the user cancels.</summary>
    private async Task<bool> ConfirmDiscardAsync(DocumentTab tab)
    {
        if (!tab.Dirty) return true;
        ActivateTab(tab);
        var answer = await Dialogs.AskSaveChanges(this, tab.DisplayName);
        return answer switch
        {
            DialogResult.Yes => await SaveAsync(tab),
            DialogResult.No => true,
            _ => false,
        };
    }

    private async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (!await ConfirmDiscardAsync(tab)) return false;
        RemoveTab(tab);
        return true;
    }

    private async Task CloseTabsAsync(List<DocumentTab> tabs)
    {
        foreach (var tab in tabs)
            if (!await CloseTabAsync(tab)) return;
    }

    /// <summary>Removes a tab without asking. There is always at least one tab.</summary>
    private void RemoveTab(DocumentTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        _errorRenderer.Clear(tab.Document);
        if (_tabs.Count == 0)
        {
            _active = null;
            NewDocument(withSample: false);
            return;
        }
        if (tab == _active)
        {
            _active = null;
            ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        else RefreshTabs();
    }

    private static readonly FilePickerFileType TidalFileType = new("Tidal files") { Patterns = new[] { "*.tidal", "*.hs" } };

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Tidal file",
            AllowMultiple = true,
            FileTypeFilter = new[] { TidalFileType, FilePickerFileTypes.All },
            SuggestedStartLocation = await StartFolderAsync(),
        });
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>()) OpenFile(path);
    }

    /// <summary>Where file dialogs open: the given folder, else the working directory.</summary>
    private async Task<IStorageFolder?> StartFolderAsync(string? preferred = null)
    {
        string? dir = preferred is not null && Directory.Exists(preferred) ? preferred
            : _settings.WorkingDirectory is { } wd && Directory.Exists(wd) ? wd : null;
        if (dir is null) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(dir); }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ working directory and explorer

    /// <summary>
    /// Makes <paramref name="directory"/> the explorer root and ghci's current directory (without restarting Tidal,
    /// so playing patterns keep playing).
    /// </summary>
    private async Task SetWorkingDirectoryAsync(string directory, bool announce = true)
    {
        try { directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception) { return; }
        if (!Directory.Exists(directory)) return;
        _settings.WorkingDirectory = directory;
        Explorer.SetRoot(directory);
        if (!announce) return;
        _console.Add(OutputKind.Info, $"Working directory: {directory}");
        if (_services.Ghci.IsReady)
        {
            var result = await _services.Ghci.EvalAsync(GhciHost.SetDirectoryCommand(directory));
            if (!result.Success) _console.Add(OutputKind.Error, $"Could not change ghci's directory: {FirstLine(result.ErrorText)}");
        }
    }

    private async Task ChooseWorkingDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose working directory",
            SuggestedStartLocation = await StartFolderAsync(),
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
        {
            await SetWorkingDirectoryAsync(path);
            SetExplorerVisible(true);
        }
    }

    private const long MaxOpenFileBytes = 5 * 1024 * 1024;

    private async void OnExplorerOpen(string path)
    {
        if (_tabs.FirstOrDefault(t => SamePath(t.Path, path)) is { } open)
        {
            ActivateTab(open);
            return;
        }
        try
        {
            if (new FileInfo(path).Length > MaxOpenFileBytes)
            {
                await Dialogs.ShowMessage(this, "File too large", $"{Path.GetFileName(path)} is larger than 5 MB; TidalStudio only opens code files.");
                return;
            }
        }
        catch (IOException) { return; }
        OpenFile(path);
    }

    private async void OnExplorerNewFile(string directory)
    {
        string suggested = FileOperations.UniqueName(directory, "untitled.tidal");
        var name = (await Dialogs.Prompt(this, "New Tidal file", $"File name (in {directory}):", suggested, "Create"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        name = FileOperations.TidalFileName(name);
        string path;
        try
        {
            path = FileOperations.CreateFile(directory, name);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessage(this, "Could not create the file", e.Message);
            return;
        }
        OpenFile(path);
        Explorer.Reveal(path);
    }

    private async void OnExplorerNewFolder(string directory)
    {
        string suggested = FileOperations.UniqueName(directory, "New folder");
        var name = (await Dialogs.Prompt(this, "New folder", $"Folder name (in {directory}):", suggested, "Create"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            Explorer.Reveal(FileOperations.CreateFolder(directory, name));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessage(this, "Could not create the folder", e.Message);
        }
    }

    private async void OnExplorerDelete(string path)
    {
        bool isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path)) { Explorer.Refresh(); return; }
        if (_settings.WorkingDirectory is { } wd && FileOperations.IsSameOrInside(wd, path))
        {
            await Dialogs.ShowMessage(this, "Can't delete the working directory",
                $"{path} is (or contains) the working directory. Choose another working directory first.");
            return;
        }

        var affected = _tabs.Where(t => t.Path is { } p && FileOperations.IsSameOrInside(p, path)).ToList();
        string what = isFolder ? $"the folder \"{Path.GetFileName(path)}\" and everything in it" : $"\"{Path.GetFileName(path)}\"";
        string where = OperatingSystem.IsWindows() ? "It goes to the Recycle Bin, so you can restore it from there." : "This can't be undone.";
        string tabsNote = affected.Count == 0 ? ""
            : affected.Any(t => t.Dirty)
                ? $"\n\n{affected.Count} open tab{(affected.Count > 1 ? "s" : "")} will be closed, including unsaved changes."
                : $"\n\n{affected.Count} open tab{(affected.Count > 1 ? "s" : "")} will be closed.";
        if (!await Dialogs.Confirm(this, isFolder ? "Delete folder" : "Delete file", $"Delete {what}?\n\n{path}\n\n{where}{tabsNote}", "Delete"))
            return;

        try
        {
            bool recycled = await Task.Run(() => FileOperations.Delete(path));
            foreach (var tab in affected) RemoveTab(tab);
            _console.Add(OutputKind.Info, $"Deleted {path}{(recycled ? " (moved to the Recycle Bin)" : "")}");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.ShowMessage(this, "Could not delete", $"{path}\n\n{e.Message}");
        }
        Explorer.Refresh();
    }
    private void SetExplorerVisible(bool visible)
    {
        if (Explorer.IsVisible && !visible && MainGrid.ColumnDefinitions[0].ActualWidth > 0)
            _settings.ExplorerWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        Explorer.IsVisible = visible;
        MainGrid.ColumnDefinitions[0].Width = visible ? new GridLength(Math.Clamp(_settings.ExplorerWidth, 140, 800)) : new GridLength(0);
        MainGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        _settings.ShowExplorer = visible;
        ShowExplorerItem.IsChecked = visible;
    }

    private void OnChooseWorkingDirectory(object? sender, RoutedEventArgs e) => _ = ChooseWorkingDirectoryAsync();
    private void OnToggleExplorer(object? sender, RoutedEventArgs e) => SetExplorerVisible(!Explorer.IsVisible);

    private Task NewAsync()
    {
        NewDocument(withSample: false);
        return Task.CompletedTask;
    }

    private void UpdateTitle()
    {
        if (_active is null) return;
        Title = $"{(_active.Dirty ? "● " : "")}{_active.DisplayName} — TidalStudio";
        StatusFile.Text = _active.Path ?? _active.UntitledName;
    }

    private void RebuildRecentMenu()
    {
        var items = new List<MenuItem>();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = path };
            string p = path;
            item.Click += (_, _) =>
            {
                if (!File.Exists(p)) { SetMessage($"{p} no longer exists"); return; }
                OpenFile(p);
            };
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        RecentMenu.ItemsSource = items;
    }

    // ------------------------------------------------------------------ closing

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        foreach (var tab in _tabs.Where(t => t.Dirty).ToList())
            if (!await ConfirmDiscardAsync(tab)) return;
        _settings.OpenFiles = _tabs.Select(t => t.Path).OfType<string>().ToList();
        if (CurrentPath is not null) _settings.LastFile = CurrentPath;
        _synth?.Close(); // saves the synth's current patch into the settings
        _closeConfirmed = true;
        SaveWindowState();
        try { _settings.Save(); } catch (Exception) { }
        _services.Ticker.Detach();
        await _services.DisposeAsync();
        Close();
    }

    private void RestoreWindow()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        MainGrid.ColumnDefinitions[4].Width = new GridLength(Math.Clamp(_settings.VisualizerPanelWidth, 200, 1200));
        LeftGrid.RowDefinitions[2].Height = new GridLength(Math.Clamp(_settings.ConsoleHeight, 40, 800));
        if (_settings.VisualizerPanelWidth <= 0) SetPanelVisible(false);
        SetExplorerVisible(_settings.ShowExplorer);
    }

    private void SaveWindowState()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        if (VisualizerPanel.IsVisible) _settings.VisualizerPanelWidth = MainGrid.ColumnDefinitions[4].ActualWidth;
        if (Explorer.IsVisible) _settings.ExplorerWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        _settings.ConsoleHeight = LeftGrid.RowDefinitions[2].ActualHeight;
        _settings.FontSize = Editor.FontSize;
    }

    // ------------------------------------------------------------------ visualizer panel

    private void BuildVisualizerPanel()
    {
        VisualizerStack.Children.Clear();
        _tiles.Clear();
        foreach (var config in _settings.Visualizers.ToList()) AddTile(config);
        RebuildBehindCodeLayer();
    }

    private void AddTile(VisualizerConfig config)
    {
        var vis = VisualizerFactory.Create(config.Type, _services);
        if (vis is null) return;
        vis.BindOptions(config.Options);
        var tile = new VisualizerTile(vis, config);
        tile.RemoveRequested += t =>
        {
            _settings.Visualizers.Remove(t.Config);
            _tiles.Remove(t);
            VisualizerStack.Children.Remove(t);
            RebuildBehindCodeLayer();
        };
        tile.MoveRequested += (t, dir) =>
        {
            int i = _settings.Visualizers.IndexOf(t.Config);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= _settings.Visualizers.Count) return;
            (_settings.Visualizers[i], _settings.Visualizers[j]) = (_settings.Visualizers[j], _settings.Visualizers[i]);
            BuildVisualizerPanel();
        };
        tile.BehindCodeChanged += _ => RebuildBehindCodeLayer();
        _tiles.Add(tile);
        VisualizerStack.Children.Add(tile);
    }

    private void RebuildBehindCodeLayer()
    {
        BehindCodeLayer.Children.Clear();
        foreach (var config in _settings.Visualizers.Where(c => c.BehindCode))
        {
            var vis = VisualizerFactory.Create(config.Type, _services);
            if (vis is null) continue;
            vis.BindOptions(config.Options);
            vis.Transparent = true;
            BehindCodeLayer.Children.Add(vis);
        }
    }

    private void SetupMenus()
    {
        var addItems = new List<MenuItem>();
        var flyoutItems = new List<MenuItem>();
        foreach (var (type, title) in VisualizerFactory.Available)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => AddVisualizer(type);
            addItems.Add(item);
            var item2 = new MenuItem { Header = title };
            item2.Click += (_, _) => AddVisualizer(type);
            flyoutItems.Add(item2);
        }
        AddVisualizerMenu.ItemsSource = addItems;
        AddVisualizerButton.Flyout = new MenuFlyout { ItemsSource = flyoutItems };

        HighlightItem.IsChecked = _settings.HighlightMiniNotation;
        InlineItem.IsChecked = true;
        AudioItem.IsChecked = _settings.CaptureAudio;
        LineNumbersItem.IsChecked = _settings.ShowLineNumbers;
        WordWrapItem.IsChecked = _settings.WordWrap;
        AutocompleteItem.IsChecked = _settings.Autocomplete;
        AutoStartItem.IsChecked = _settings.StartTidalOnLaunch;
        AutoDirtItem.IsChecked = _settings.AutoStartSuperDirt;
        ShowPanelItem.IsChecked = true;
        StyleBackgroundItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Background;
        StyleOutlineItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Outline;
        StyleUnderlineItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Underline;
        UpdateBootMenu();
        RebuildRecentMenu();
    }

    private void AddVisualizer(string type)
    {
        var config = new VisualizerConfig { Type = type, Height = VisualizerFactory.IsRound(type) ? 260 : 180 };
        _settings.Visualizers.Add(config);
        AddTile(config);
        SetPanelVisible(true);
    }

    private void SetPanelVisible(bool visible)
    {
        VisualizerPanel.IsVisible = visible;
        MainGrid.ColumnDefinitions[3].Width = visible ? new GridLength(4) : new GridLength(0);
        MainGrid.ColumnDefinitions[4].Width = visible ? new GridLength(Math.Clamp(_settings.VisualizerPanelWidth, 200, 1200)) : new GridLength(0);
        ShowPanelItem.IsChecked = visible;
    }

    private void UpdateBootMenu()
    {
        BootBuiltInItem.IsChecked = _settings.UseBuiltInBootFile;
        BootCustomItem.IsChecked = !_settings.UseBuiltInBootFile && !string.IsNullOrEmpty(_settings.BootFilePath);
        BootAutoItem.IsChecked = !_settings.UseBuiltInBootFile && string.IsNullOrEmpty(_settings.BootFilePath);
        BootCustomItem.Header = string.IsNullOrEmpty(_settings.BootFilePath) ? "Choose a boot file…" : $"Custom: {_settings.BootFilePath}";
    }

    // ------------------------------------------------------------------ audio

    private void StartAudio()
    {
        _services.AudioCapture.Start();
        if (_services.AudioCapture.Error is { } err) _console.Add(OutputKind.Error, $"Audio capture failed: {err}");
        else _console.Add(OutputKind.Info, $"Audio capture: {_services.AudioCapture.Description} (what Windows plays, including SuperCollider).");
    }

    // ------------------------------------------------------------------ status

    private void UpdateStatus()
    {
        var state = _services.Ghci.State;
        double now = _services.Time.Now;
        bool playing = _services.Clock.IsPlaying(now);
        StatusTidal.Text = state switch
        {
            GhciState.Ready => playing ? "Tidal: playing" : "Tidal: ready",
            GhciState.Starting => "Tidal: starting…",
            GhciState.Failed => "Tidal: failed",
            GhciState.Exited => "Tidal: exited",
            _ => "Tidal: stopped",
        };
        StatusDot.Fill = new SolidColorBrush(state switch
        {
            GhciState.Ready => playing ? Color.Parse("#4ECDC4") : Color.Parse("#FFD166"),
            GhciState.Starting => Color.Parse("#60A5FA"),
            GhciState.Failed or GhciState.Exited => Color.Parse("#FF5370"),
            _ => Color.Parse("#8A93A6"),
        });
        if (_services.Clock.HasReference)
        {
            double cycle = _services.Clock.CycleAt(now);
            double cps = _services.Clock.Cps;
            StatusCycle.Text = $"cycle {Math.Floor(cycle),6:0} · {cycle - Math.Floor(cycle):0.00}   cps {cps:0.000} ({cps * 240:0.#} bpm)";
        }
        else StatusCycle.Text = "";
        StatusDirt.Text = _dirtState switch
        {
            DirtState.Running => "SuperDirt: running",
            DirtState.Starting => "SuperDirt: starting…",
            DirtState.NotRunning => "SuperDirt: not running",
            _ => "SuperDirt: ?",
        };
        StatusDirtDot.Fill = new SolidColorBrush(_dirtState switch
        {
            DirtState.Running => Color.Parse("#4ECDC4"),
            DirtState.Starting => Color.Parse("#60A5FA"),
            DirtState.NotRunning => Color.Parse("#FF5370"),
            _ => Color.Parse("#8A93A6"),
        });
        StatusMuted.Text = _muted.Count > 0 ? "muted: " + string.Join(", ", _muted.OrderBy(m => m).Select(m => "d" + m)) : "";
        StatusEvents.Text = $"{Interlocked.Read(ref _services.Osc.PacketsReceived)} events";
        StatusAudio.Text = "audio: " + _services.AudioCapture.Description;
    }

    private void SetMessage(string text) => StatusMessage.Text = text;

    private static string FirstLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "";
        // Compile errors: "<interactive>:12:3: error: …" then the useful part ("    • Couldn't match …").
        if (lines[0].StartsWith("<interactive>", StringComparison.Ordinal) && lines.Length > 1)
            return lines[1].Trim().TrimStart('•', '*', ' ');
        // Runtime errors (e.g. mini-notation syntax): "*** Exception: Syntax error in sequence:" + the string.
        string first = lines[0].Replace("*** Exception:", "").Trim();
        return lines.Length > 1 ? $"{first} {lines[1].Trim()}" : first;
    }

    private void ShowErrorForCaretLine()
    {
        var msg = _errorRenderer.MessageAt(Editor.Document, Editor.TextArea.Caret.Line);
        if (msg is not null) SetMessage(FirstLine(msg));
    }

    private void ChangeFontSize(double delta)
    {
        Editor.FontSize = Math.Clamp(Editor.FontSize + delta, 8, 48);
        _settings.FontSize = Editor.FontSize;
    }

    // ------------------------------------------------------------------ menu handlers

    private void OnNew(object? sender, RoutedEventArgs e) => _ = NewAsync();
    private void OnOpen(object? sender, RoutedEventArgs e) => _ = OpenAsync();
    private void OnSave(object? sender, RoutedEventArgs e) => _ = SaveAsync();
    private void OnSaveAs(object? sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnExit(object? sender, RoutedEventArgs e) => Close();
    private void OnRunLine(object? sender, RoutedEventArgs e) => _ = RunLineAsync();
    private void OnRunBlock(object? sender, RoutedEventArgs e) => _ = RunBlockAsync();
    private void OnHush(object? sender, RoutedEventArgs e) => _ = HushAsync();
    private void OnRestartTidal(object? sender, RoutedEventArgs e) => _ = StartTidalAsync();

    private async void OnStopTidal(object? sender, RoutedEventArgs e)
    {
        await _services.Ghci.StopAsync();
        UpdateStatus();
    }

    private void OnBootAuto(object? sender, RoutedEventArgs e)
    {
        _settings.UseBuiltInBootFile = false;
        _settings.BootFilePath = null;
        UpdateBootMenu();
        SetMessage("Boot file changed — restart Tidal to use it.");
    }

    private void OnBootBuiltIn(object? sender, RoutedEventArgs e)
    {
        _settings.UseBuiltInBootFile = true;
        UpdateBootMenu();
        SetMessage("Boot file changed — restart Tidal to use it.");
    }

    private async void OnBootChoose(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose BootTidal.hs",
            FileTypeFilter = new[] { new FilePickerFileType("Haskell") { Patterns = new[] { "*.hs" } }, FilePickerFileTypes.All },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
        {
            _settings.BootFilePath = path;
            _settings.UseBuiltInBootFile = false;
            SetMessage("Boot file changed — restart Tidal to use it.");
        }
        UpdateBootMenu();
    }

    private async void OnInstallBoot(object? sender, RoutedEventArgs e)
    {
        if (CurrentPath is null)
        {
            await Dialogs.ShowMessage(this, "Save the file first", "Save your .tidal file first, so TidalStudio knows which folder to write BootTidal.hs to.");
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(CurrentPath)!, BootFiles.BootFileName);
        string message = File.Exists(target)
            ? $"Replace {target} with TidalStudio's boot file?\n\nThe current file is kept as BootTidal.hs.bak. TidalStudio's version is the standard boot file plus: events are also sent to the editor (UDP 57130), and the helpers for highlighting (tsctx, color). It keeps working in VS Code and other editors."
            : $"Write TidalStudio's boot file to {target}?";
        if (!await Dialogs.Confirm(this, "Boot file", message, "Write boot file")) return;
        try
        {
            if (File.Exists(target)) File.Copy(target, target + ".bak", overwrite: true);
            await File.WriteAllTextAsync(target, BootFiles.BuiltIn, new UTF8Encoding(false));
            SetMessage("Boot file written — restart Tidal to use it.");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Could not write boot file", ex.Message);
        }
    }

    private async void OnChooseGhci(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose ghci executable",
            FileTypeFilter = new[] { new FilePickerFileType("Programs") { Patterns = new[] { "ghci*", "*.exe" } }, FilePickerFileTypes.All },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        _settings.GhciPath = path;
        SetMessage($"ghci set to {path} — restart Tidal to use it.");
    }

    private void OnToggleAutoStart(object? sender, RoutedEventArgs e)
    {
        _settings.StartTidalOnLaunch = !_settings.StartTidalOnLaunch;
        AutoStartItem.IsChecked = _settings.StartTidalOnLaunch;
    }

    private async void OnStartSuperDirt(object? sender, RoutedEventArgs e)
    {
        if (await SuperDirtProbe.IsRunningAsync())
        {
            SetDirtState(DirtState.Running);
            SetMessage("SuperDirt is already running.");
            return;
        }
        await StartSuperDirtAsync(interactive: true);
    }

    private Synth.DrumWindow? _drums;

    private void OnOpenDrums(object? sender, RoutedEventArgs e)
    {
        if (_drums is { IsVisible: true })
        {
            _drums.Activate();
            return;
        }
        _drums = new Synth.DrumWindow(_services, () => _completionCatalog.InvalidateSamples(), (kind, text) => _console.Add(kind, text), RestartSuperDirtAsync, InsertSuggestionAsync);
        _drums.Closed += (_, _) => _drums = null;
        _drums.Show();
    }

    private void OnOpenExample(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is string file) OpenExampleScript(file);
    }

    /// <summary>Opens a built-in example script in a new (unsaved) tab.</summary>
    internal void OpenExampleScript(string fileName)
    {
        string text = TidalStudio.Core.Synth.ExampleScripts.Read(fileName);
        if (text.Length == 0)
        {
            SetMessage($"Example {fileName} is missing from this build.");
            return;
        }
        var tab = CreateTab(new TextDocument(text), null, Path.GetFileNameWithoutExtension(fileName));
        AddTab(tab);
        tab.Dirty = false;
        RefreshTabs();
        Activate();
    }

    private Synth.AcidWindow? _acid;

    private void OnOpenAcid(object? sender, RoutedEventArgs e)
    {
        if (_acid is { IsVisible: true })
        {
            _acid.Activate();
            return;
        }
        _acid = new Synth.AcidWindow(_services, () => _completionCatalog.InvalidateSamples(), (kind, text) => _console.Add(kind, text), RestartSuperDirtAsync, InsertSuggestionAsync);
        _acid.Closed += (_, _) => _acid = null;
        _acid.Show();
    }
    private Vj.VjWindow? _vj;

    private void OnOpenVj(object? sender, RoutedEventArgs e)
    {
        if (_vj is { IsVisible: true })
        {
            _vj.Activate();
            return;
        }
        _vj = new Vj.VjWindow(_services);
        _vj.Closed += (_, _) => { _vj = null; _settings.Save(); };
        _vj.Show();
    }

    private bool _renderingPresets;

    /// <summary>Renders the factory presets of all four synths into the samples folder and loads them into SuperDirt.</summary>
    private async void OnRenderAllPresets(object? sender, RoutedEventArgs e)
    {
        if (_renderingPresets) return;
        string root = _settings.SamplesFolder is { Length: > 0 } f ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "samples");
        var plan = Core.Synth.PresetSamples.Plan();
        if (!await Dialogs.Confirm(this, "Render all presets",
                $"Render the {plan.Count} factory presets of Tidepool, Drums, Acid and FM as samples into {root}?\n\n" +
                "Sounds that already have samples there are left alone, so nothing you saved yourself is replaced.", "Render"))
            return;
        _renderingPresets = true;
        try
        {
            _console.Add(OutputKind.Info, $"Rendering {plan.Count} factory presets into {root}…");
            var result = await Task.Run(() => Core.Synth.PresetSamples.RenderAll(root, replace: false,
                (i, n, s) => { if (i < n && i % 10 == 0) Dispatcher.UIThread.Post(() => _console.Add(OutputKind.Info, $"  {i + 1}/{n}: {s.Synth} · {s.Preset}")); }));
            foreach (var (s, error) in result.Failed) _console.Add(OutputKind.Error, $"  {s.Synth} · {s.Preset} failed: {error}");
            if (result.Skipped.Count > 0)
                _console.Add(OutputKind.Info, $"  Left alone (already saved): {string.Join(", ", result.Skipped.Select(s => s.Sound))}");
            _completionCatalog.InvalidateSamples();
            int loaded = 0;
            if (_services.SuperDirt.IsRunning)
                foreach (var dir in result.Written.SelectMany(Core.Synth.PresetSamples.FoldersOf))
                    if (_services.SuperDirt.TrySendCode($"~dirt.loadSoundFiles(\"{Path.Combine(root, dir).Replace('\\', '/')}\");")) loaded++;
            _console.Add(OutputKind.Info, $"Rendered {result.Written.Count} presets ({result.Files} samples)." +
                (loaded > 0 ? " They are loaded into SuperDirt: try the example scripts (Synth ▸ Example scripts)." : " Start SuperDirt to load them."));
        }
        finally { _renderingPresets = false; }
    }

    // ------------------------------------------------------------------ Muse (AI part suggestions)

    private Muse.MuseService? _museService;
    private Muse.MuseTrainerWindow? _museTrainer;

    internal Muse.MuseService MuseService => _museService ??= new Muse.MuseService(_services);

    private void OnMuseSuggest(object? sender, RoutedEventArgs e)
    {
        if (_active is null) NewDocument(withSample: false);
        var popup = new Muse.SuggestPopup(MuseService, _services, InsertSuggestionAsync);
        popup.ShowAt(this, CaretScreenPoint());
    }

    private void OnMuseTrainer(object? sender, RoutedEventArgs e)
    {
        if (_museTrainer is { IsVisible: true })
        {
            _museTrainer.Activate();
            return;
        }
        _museTrainer = new Muse.MuseTrainerWindow(MuseService, _services, (kind, text) => _console.Add(kind, text));
        _museTrainer.Closed += (_, _) => { _museTrainer = null; _settings.Save(); };
        _museTrainer.Show();
    }

    /// <summary>The screen position just below the editor's cursor.</summary>
    private PixelPoint CaretScreenPoint()
    {
        try
        {
            var view = Editor.TextArea.TextView;
            var caret = Editor.TextArea.Caret;
            var p = view.GetVisualPosition(caret.Position, VisualYPosition.LineBottom) - view.ScrollOffset;
            return view.PointToScreen(new Point(Math.Max(0, p.X), Math.Max(0, p.Y)));
        }
        catch (Exception)
        {
            return this.PointToScreen(new Point(80, 120));
        }
    }

    /// <summary>
    /// Pastes suggested code as its own block: at the cursor when the line is empty, else after the block the
    /// cursor is in. With <paramref name="run"/> it is run straight away.
    /// </summary>
    internal async Task InsertSuggestionAsync(string code, bool run)
    {
        Activate();
        var doc = Editor.Document;
        int lineNumber = Math.Clamp(Editor.TextArea.Caret.Line, 1, doc.LineCount);
        var line = doc.GetLineByNumber(lineNumber);
        string current = doc.GetText(line.Offset, line.Length);
        int offset;
        string text;
        int firstLine; // 0-based line where the code starts
        if (current.Trim().Length == 0)
        {
            bool blankBefore = lineNumber == 1 || doc.GetText(doc.GetLineByNumber(lineNumber - 1)).Trim().Length == 0;
            offset = line.Offset;
            text = (blankBefore ? "" : "\n") + code + "\n";
            firstLine = lineNumber - 1 + (blankBefore ? 0 : 1);
        }
        else
        {
            var block = CodeBlocks.GetBlock(doc.Text, lineNumber - 1);
            int endLine = (block?.EndLine ?? lineNumber - 1) + 1; // 1-based
            offset = doc.GetLineByNumber(endLine).EndOffset;
            text = "\n\n" + code;
            firstLine = endLine + 1;
        }
        doc.Insert(offset, text);
        int lastLine = firstLine + code.Split('\n').Length - 1;
        var codeLine = doc.GetLineByNumber(Math.Min(doc.LineCount, firstLine + 1));
        Editor.CaretOffset = codeLine.Offset;
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
        if (run) await RunRangeAsync(firstLine, Math.Min(lastLine, doc.LineCount - 1));
    }

    private Synth.FmWindow? _fm;

    private void OnOpenFm(object? sender, RoutedEventArgs e)
    {
        if (_fm is { IsVisible: true })
        {
            _fm.Activate();
            return;
        }
        _fm = new Synth.FmWindow(_services, () => _completionCatalog.InvalidateSamples(), (kind, text) => _console.Add(kind, text), RestartSuperDirtAsync, InsertSuggestionAsync);
        _fm.Closed += (_, _) => _fm = null;
        _fm.Show();
    }
    private Synth.SynthWindow? _synth;

    private void OnOpenSynth(object? sender, RoutedEventArgs e)
    {
        if (_synth is { } open)
        {
            open.Activate();
            return;
        }
        _synth = new Synth.SynthWindow(_services, () => _completionCatalog.InvalidateSamples(), (kind, text) => _console.Add(kind, text), RestartSuperDirtAsync, InsertSuggestionAsync);
        _synth.Closed += (_, _) => _synth = null;
        _synth.Show();
    }

    private void OnToggleAutoDirt(object? sender, RoutedEventArgs e)
    {
        _settings.AutoStartSuperDirt = !_settings.AutoStartSuperDirt;
        AutoDirtItem.IsChecked = _settings.AutoStartSuperDirt;
        if (_settings.AutoStartSuperDirt) _ = EnsureSuperDirtAsync();
    }

    // ------------------------------------------------------------------ SuperDirt

    private void SetDirtState(DirtState state)
    {
        _dirtState = state;
        UpdateStatus();
    }

    /// <summary>
    /// Makes sure SuperDirt is running: asks it (handshake) and starts SuperCollider when nobody answers.
    /// Cheap enough to call on every run: a running SuperDirt is re-checked at most every 10 seconds.
    /// </summary>
    private async Task EnsureSuperDirtAsync()
    {
        if (_dirtState == DirtState.Starting) return;
        if (_dirtState == DirtState.Running && DateTime.UtcNow - _lastDirtProbe < TimeSpan.FromSeconds(10)) return;
        _lastDirtProbe = DateTime.UtcNow;

        if (await SuperDirtProbe.IsRunningAsync())
        {
            SetDirtState(DirtState.Running);
            return;
        }
        if (_dirtState == DirtState.Starting) return; // started meanwhile
        SetDirtState(DirtState.NotRunning);

        if (_settings.AutoStartSuperDirt) await StartSuperDirtAsync(interactive: false);
        else if (!_dirtHintShown)
        {
            _dirtHintShown = true;
            _console.Add(OutputKind.Error, "SuperDirt is not running, so nothing will be heard. Start it with SuperCollider ▸ Start SuperDirt.");
            SetMessage("SuperDirt is not running — nothing will be heard.");
        }
    }

    /// <summary>Starts sclang + SuperDirt and waits until SuperDirt answers.</summary>
    private async Task StartSuperDirtAsync(bool interactive)
    {
        if (_dirtState == DirtState.Starting) return;
        string? sclang = ToolLocator.FindSclang(_settings.SclangPath);
        if (sclang is null)
        {
            const string message = "Could not find SuperCollider's sclang, so SuperDirt can't be started. Install SuperCollider, or use SuperCollider ▸ Choose sclang….";
            if (interactive) await Dialogs.ShowMessage(this, "sclang not found", message);
            else if (!_dirtHintShown) { _dirtHintShown = true; _console.Add(OutputKind.Error, message); }
            return;
        }

        if (!_services.SuperDirt.IsRunning)
        {
            // Another SuperCollider (e.g. the IDE) without SuperDirt: a second sclang would fight it for port 57120.
            if (Process.GetProcessesByName("sclang").Length > 0)
            {
                const string message = "SuperCollider is already running, but SuperDirt isn't answering. Start SuperDirt there (e.g. run your startup file), or close SuperCollider and TidalStudio will start it for you.";
                if (!interactive)
                {
                    if (!_dirtHintShown) { _dirtHintShown = true; _console.Add(OutputKind.Error, message); }
                    SetMessage("SuperDirt is not running — nothing will be heard.");
                    return;
                }
                if (!await Dialogs.Confirm(this, "SuperCollider is already running", message + "\n\nStart another one anyway?", "Start anyway")) return;
            }
            try
            {
                _services.SuperDirt.Start(sclang, _settings.SuperDirtScript);
            }
            catch (Exception ex)
            {
                _console.Add(OutputKind.Error, $"Could not start sclang: {ex.Message}");
                SetDirtState(DirtState.NotRunning);
                return;
            }
        }

        SetDirtState(DirtState.Starting);
        _console.Add(OutputKind.Info, "Starting SuperCollider and SuperDirt — loading samples can take a little while…");
        SetMessage("Starting SuperDirt…");

        // Wait until SuperDirt answers (server boot + loading samples), or sclang gives up.
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(1000);
            if (!_services.SuperDirt.IsRunning) break;
            if (await SuperDirtProbe.IsRunningAsync())
            {
                _lastDirtProbe = DateTime.UtcNow;
                _dirtHintShown = false;
                SetDirtState(DirtState.Running);
                LoadTidepoolSamples();
                _console.Add(OutputKind.Info, $"SuperDirt is ready ({watch.Elapsed.TotalSeconds:0} s).");
                SetMessage("SuperDirt is ready.");
                return;
            }
        }
        SetDirtState(DirtState.NotRunning);
        _console.Add(OutputKind.Error, _services.SuperDirt.IsRunning
            ? "SuperDirt did not answer within 2 minutes. Check the [sc] lines above for errors."
            : "SuperCollider stopped before SuperDirt was ready. Check the [sc] lines above for errors.");
    }

    /// <summary>
    /// Sounds saved with Tidepool must survive a SuperDirt restart even when startup.scd doesn't load the samples
    /// folder: load the folders it misses into the SuperDirt TidalStudio just started.
    /// </summary>
    private void LoadTidepoolSamples()
    {
        if (!_services.SuperDirt.IsRunning) return;
        string root = _settings.SamplesFolder is { Length: > 0 } f ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "samples");
        if (!Directory.Exists(root)) return;
        string? startup = Core.Completion.SampleLibrary.StartupScriptPath();
        string script = "";
        try { if (startup is not null && File.Exists(startup)) script = File.ReadAllText(startup); }
        catch (IOException) { }
        var missing = Directory.EnumerateDirectories(root)
            .Where(d => Directory.EnumerateFiles(d, "*.wav").Any() && !StartupScript.LoadsFolder(script, d))
            .ToList();
        if (missing.Count == 0) return;
        foreach (var dir in missing)
            _services.SuperDirt.TrySendCode($"~dirt.loadSoundFiles(\"{dir.Replace('\\', '/')}\");");
        _console.Add(OutputKind.Info, $"Loaded your Tidepool sounds: {string.Join(", ", missing.Select(Path.GetFileName))}");
    }

    /// <summary>
    /// Restarts SuperCollider + SuperDirt (with the user's startup file) and makes sure <paramref name="soundFolder"/>
    /// is loaded. Used after Tidepool saves a sound. A SuperCollider started elsewhere (e.g. the IDE) is stopped and
    /// TidalStudio runs it from then on, so later sounds can be loaded without a restart.
    /// </summary>
    internal async Task<(bool Ok, string Message)> RestartSuperDirtAsync(string soundFolder)
    {
        if (_dirtState == DirtState.Starting) return (false, "SuperDirt is still starting; save again when it is ready.");
        if (ToolLocator.FindSclang(_settings.SclangPath) is null)
            return (false, "Could not find SuperCollider's sclang to restart it (SuperCollider ▸ Choose sclang…).");

        _console.Add(OutputKind.Info, "Restarting SuperCollider so SuperDirt loads the new samples…");
        if (_services.SuperDirt.IsRunning) await _services.SuperDirt.StopAsync();
        bool external = false;
        foreach (var name in new[] { "sclang", "scsynth" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                external = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }
        }
        SetDirtState(DirtState.NotRunning);
        await Task.Delay(300); // let the ports close

        await StartSuperDirtAsync(interactive: false);
        if (_dirtState != DirtState.Running) return (false, "SuperDirt did not come back up; see the console for SuperCollider's messages.");

        // The startup file loads the folder itself when it is set up to; otherwise load it into the fresh SuperDirt.
        string? startup = Core.Completion.SampleLibrary.StartupScriptPath();
        bool atStartup = startup is not null && StartupScript.LoadsFolder(await File.ReadAllTextAsync(startup), soundFolder);
        if (!atStartup && !_services.SuperDirt.TrySendCode($"~dirt.loadSoundFiles(\"{soundFolder.Replace('\\', '/')}\");"))
            return (false, "SuperDirt restarted, but the new sound could not be loaded into it.");
        return (true, external
            ? "SuperCollider restarted with the new sound. (The SuperCollider you had open was stopped; TidalStudio runs it now, so the next sounds load without a restart.)"
            : "SuperDirt restarted with the new sound — play it now.");
    }

    private async void OnStopSuperDirt(object? sender, RoutedEventArgs e) => await _services.SuperDirt.StopAsync();

    private async void OnChooseSclang(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose sclang executable",
            FileTypeFilter = new[] { new FilePickerFileType("Programs") { Patterns = new[] { "sclang*", "*.exe" } }, FilePickerFileTypes.All },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) _settings.SclangPath = path;
    }

    private void OnResetVisualizers(object? sender, RoutedEventArgs e)
    {
        _settings.Visualizers = AppSettings.DefaultVisualizers();
        BuildVisualizerPanel();
    }

    private void OnTogglePanel(object? sender, RoutedEventArgs e) => SetPanelVisible(!VisualizerPanel.IsVisible);

    private void OnToggleHighlight(object? sender, RoutedEventArgs e)
    {
        _settings.HighlightMiniNotation = !_settings.HighlightMiniNotation;
        HighlightItem.IsChecked = _settings.HighlightMiniNotation;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void OnHighlightStyle(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<HighlightStyle>(tag, out var style))
            _settings.HighlightStyle = style;
        StyleBackgroundItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Background;
        StyleOutlineItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Outline;
        StyleUnderlineItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Underline;
    }

    private void OnToggleInline(object? sender, RoutedEventArgs e)
    {
        _inlineGenerator.Enabled = !_inlineGenerator.Enabled;
        InlineItem.IsChecked = _inlineGenerator.Enabled;
        Editor.TextArea.TextView.Redraw();
    }

    private void OnToggleAudio(object? sender, RoutedEventArgs e)
    {
        _settings.CaptureAudio = !_settings.CaptureAudio;
        AudioItem.IsChecked = _settings.CaptureAudio;
        if (_settings.CaptureAudio) StartAudio();
        else _services.AudioCapture.Stop();
    }

    private void OnToggleLineNumbers(object? sender, RoutedEventArgs e)
    {
        _settings.ShowLineNumbers = !_settings.ShowLineNumbers;
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        LineNumbersItem.IsChecked = _settings.ShowLineNumbers;
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e) { if (_active is not null) _ = CloseTabAsync(_active); }
    private void OnCloseAllTabs(object? sender, RoutedEventArgs e) => _ = CloseTabsAsync(_tabs.ToList());
    private void OnNextTab(object? sender, RoutedEventArgs e) => CycleTab(1);
    private void OnPreviousTab(object? sender, RoutedEventArgs e) => CycleTab(-1);

    private void OnToggleAutocomplete(object? sender, RoutedEventArgs e)
    {
        _settings.Autocomplete = !_settings.Autocomplete;
        _completion.Enabled = _settings.Autocomplete;
        AutocompleteItem.IsChecked = _settings.Autocomplete;
    }

    private void OnToggleWordWrap(object? sender, RoutedEventArgs e)
    {
        _settings.WordWrap = !_settings.WordWrap;
        Editor.WordWrap = _settings.WordWrap;
        WordWrapItem.IsChecked = _settings.WordWrap;
    }

    private void OnFontBigger(object? sender, RoutedEventArgs e) => ChangeFontSize(1);
    private void OnFontSmaller(object? sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void OnClearConsole(object? sender, RoutedEventArgs e) => _console.Clear();

    private void OnOpenSettingsFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveWindowState();
            _settings.Save();
            Process.Start(new ProcessStartInfo(AppSettings.DefaultPath) { UseShellExecute = true });
            SetMessage("Changes to settings.json take effect after restarting TidalStudio.");
        }
        catch (Exception ex)
        {
            _console.Add(OutputKind.Error, $"Could not open settings: {ex.Message}");
        }
    }

    private async void OnShowHelp(object? sender, RoutedEventArgs e) => await Dialogs.ShowMessage(this, "Keyboard shortcuts & tips",
        """
        Running code
          Shift+Enter        run the current line
          Ctrl+Enter         run the block around the cursor (lines between blank lines), or the selection
          Ctrl+.  /  Ctrl+Alt+H   hush (stop everything)
          Ctrl+1 … Ctrl+9    mute / unmute d1 … d9

        Files
          Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S
          Ctrl+= / Ctrl+-    bigger / smaller font
          Ctrl+Shift+E       show / hide the file explorer
          Ctrl+W             close the tab;  Ctrl+Tab / Ctrl+Shift+Tab  next / previous tab
          Middle-click a tab to close it. Open tabs are reopened next time.

        Autocomplete
          Suggestions appear while typing: Tidal functions (with their types), effects after #,
          sample names inside s "…", and inline visuals after -- _ (with their options).
          Ctrl+Space shows suggestions; Enter or Tab accepts; Esc closes.

        Working directory
          The explorer shows the working directory; it is also ghci's current directory and where file dialogs
          open. Change it with File ▸ Set working directory…, the folder button in the explorer, or right-click a
          folder ▸ Set as working directory. Tidal keeps playing while it changes.

        Visual feedback
          While a pattern plays, the parts of its mini-notation that sound light up in the code.
          Colours: one per pattern (d1, d2, …). Override per pattern with  # color "orange"  or  # color "ff8800"
          (hex without '#', because '#' is not allowed inside Tidal's mini-notation strings).
          Style (background / outline / underline): View ▸ Highlight style.

        Inline visuals (like Strudel's _pianoroll())
          Put a comment in a block:   -- _punchcard   -- _pianoroll   -- _spiral   -- _pitchwheel
                                      -- _scope   -- _spectrum   -- _cycle   -- _rings   -- _params
                                      -- _channels   -- _stereo   -- _spectrogram   -- _meter
          Options after it:  -- _pianoroll cycles=2 height=120 rows=pitch playhead=0.5

        Visualizer panel
          + Add to add one; ⚙ for its options; drag the bar under a tile to resize;
          "behind code" also draws it behind the editor text.
          Scope and spectrum listen to what Windows plays (including SuperCollider).

        Boot file
          TidalStudio needs its own additions in BootTidal.hs to receive events. By default it uses
          BootTidal.hs next to your file if it has them, otherwise its built-in one.
        """, 640);

    private async void OnAbout(object? sender, RoutedEventArgs e) => await Dialogs.ShowMessage(this, "About TidalStudio",
        $"TidalStudio — a live-coding editor for TidalCycles with visual feedback.\n\n" +
        $"ghci: {_services.Ghci.GhciPath ?? ToolLocator.FindGhci(_settings.GhciPath) ?? "not found"}\n" +
        $"Boot file: {_bootDescription ?? "(Tidal not started)"}\n" +
        $"Working directory: {_settings.WorkingDirectory ?? "(none)"}\n" +
        $"Settings: {AppSettings.DefaultPath}\n" +
        $"Editor event port: UDP {_settings.EditorOscPort}");
}
