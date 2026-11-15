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
using TidalStudio.App.Services;
using TidalStudio.App.Visualizers;
using TidalStudio.Core.Settings;
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

    private string? _currentPath;
    private bool _dirty;
    private bool _bootSupportsVisuals;
    private bool _closeConfirmed;
    private string? _bootDescription;
    private int _frame;
    private int _runCount;
    private string _lastRunLines = "";

    // Parameterless constructor for the XAML previewer.
    public MainWindow() : this(new AppServices(), Array.Empty<string>()) { }

    public MainWindow(AppServices services, string[] args)
    {
        _services = services;
        _settings = services.Settings;
        InitializeComponent();

        _console = new ConsoleController(ConsoleView);
        _miniRenderer = new MiniNotationRenderer(services, () => Editor.Document);
        _inlineGenerator = new InlineVisualGenerator(services);

        SetupEditor();
        SetupMenus();
        RestoreWindow();
        BuildVisualizerPanel();

        _services.Ghci.Output += line => _console.Add(line);
        _services.Ghci.StateChanged += _ => Dispatcher.UIThread.Post(UpdateStatus);
        _services.SuperDirt.Output += line => _console.Add(line with { Text = "[sc] " + line.Text });
        _services.Osc.Error += msg => _console.Add(OutputKind.Error, msg);

        Opened += async (_, _) => await OnOpenedAsync(args);
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    // ------------------------------------------------------------------ startup

    private async Task OnOpenedAsync(string[] args)
    {
        _services.Ticker.Attach(this);
        _services.Ticker.Subscribe(OnFrame);

        try
        {
            _services.Osc.Start(_settings.EditorOscPort);
            _console.Add(OutputKind.Info, $"Listening for Tidal events on UDP {_settings.EditorOscPort}.");
        }
        catch (Exception e)
        {
            _console.Add(OutputKind.Error, $"Could not listen on UDP port {_settings.EditorOscPort}: {e.Message}. Visualizers will stay empty. (Is another TidalStudio running?)");
        }

        if (_settings.CaptureAudio) StartAudio();

        int selfTest = Array.IndexOf(args, "--selftest");
        string? selfTestDir = selfTest >= 0 && selfTest + 1 < args.Length ? args[selfTest + 1] : null;

        string? file = args.FirstOrDefault(File.Exists) ?? (selfTestDir is null && _settings.LastFile is { } last && File.Exists(last) ? last : null);
        if (file is not null) LoadFile(file);
        else NewDocument(withSample: true);

        if (selfTestDir is not null)
        {
            _ = RunSelfTestAsync(selfTestDir);
            await StartTidalAsync();
            return;
        }

        if (_settings.StartTidalOnLaunch) await StartTidalAsync();
        else _console.Add(OutputKind.Info, "Tidal is not started. Use Tidal ▸ Start / restart Tidal.");
    }

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

        Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.TextChanged += (_, _) =>
        {
            if (_dirty) return;
            _dirty = true;
            UpdateTitle();
        };
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

        if (ctrl && alt && e.Key == Key.H || ctrl && !alt && e.Key == Key.OemPeriod)
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
        _lastRunLines = $"{firstLine + 1}-{lastLine + 1}";
        var doc = Editor.Document;
        var first = doc.GetLineByNumber(firstLine + 1);
        var last = doc.GetLineByNumber(lastLine + 1);
        int start = first.Offset;
        string raw = doc.GetText(start, last.EndOffset - start);

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
        if (_bootSupportsVisuals && _settings.HighlightMiniNotation)
        {
            var pre = Preprocessor.Wrap(raw, _services.Literals.NextId);
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
        }

        _flashRenderer.Flash(doc, start, raw.Length, result.Success);
        _errorRenderer.ClearRange(start, start + raw.Length);
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
        var boot = BootFiles.Choose(_settings.BootFilePath, _currentPath, _settings.UseBuiltInBootFile);
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
            string? workDir = _currentPath is null ? null : Path.GetDirectoryName(_currentPath);
            await _services.Ghci.StartAsync(ghci, boot.Text, workDir, TimeSpan.FromSeconds(90));
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

    private void NewDocument(bool withSample)
    {
        Editor.Document = new TextDocument(withSample ? SampleCode.Replace("\r\n", "\n") : "");
        Editor.Document.FileName = null;
        _currentPath = null;
        _errorRenderer.Clear();
        _dirty = false;
        UpdateTitle();
    }

    private void LoadFile(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            Editor.Document = new TextDocument(text) { FileName = path };
            _currentPath = path;
            _errorRenderer.Clear();
            _dirty = false;
            _settings.LastFile = path;
            _settings.AddRecent(path);
            RebuildRecentMenu();
            UpdateTitle();
        }
        catch (Exception e)
        {
            _console.Add(OutputKind.Error, $"Could not open {path}: {e.Message}");
        }
    }

    private async Task<bool> SaveAsync()
    {
        if (_currentPath is null) return await SaveAsAsync();
        try
        {
            await File.WriteAllTextAsync(_currentPath, Editor.Document.Text, new UTF8Encoding(false));
            _dirty = false;
            UpdateTitle();
            SetMessage($"Saved {Path.GetFileName(_currentPath)}");
            return true;
        }
        catch (Exception e)
        {
            await Dialogs.ShowMessage(this, "Save failed", e.Message);
            return false;
        }
    }

    private async Task<bool> SaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Tidal file",
            DefaultExtension = "tidal",
            SuggestedFileName = _currentPath is null ? "untitled.tidal" : Path.GetFileName(_currentPath),
            FileTypeChoices = new[] { TidalFileType, FilePickerFileTypes.All },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return false;
        _currentPath = path;
        Editor.Document.FileName = path;
        _settings.LastFile = path;
        _settings.AddRecent(path);
        RebuildRecentMenu();
        return await SaveAsync();
    }

    private static readonly FilePickerFileType TidalFileType = new("Tidal files") { Patterns = new[] { "*.tidal", "*.hs" } };

    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Tidal file",
            AllowMultiple = false,
            FileTypeFilter = new[] { TidalFileType, FilePickerFileTypes.All },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) LoadFile(path);
    }

    private async Task NewAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        NewDocument(withSample: false);
    }

    /// <summary>Returns false when the user cancels.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var answer = await Dialogs.AskSaveChanges(this, _currentPath is null ? "untitled" : Path.GetFileName(_currentPath));
        return answer switch
        {
            DialogResult.Yes => await SaveAsync(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void UpdateTitle()
    {
        string name = _currentPath is null ? "untitled" : Path.GetFileName(_currentPath);
        Title = $"{(_dirty ? "● " : "")}{name} — TidalStudio";
        StatusFile.Text = _currentPath ?? "untitled";
    }

    private void RebuildRecentMenu()
    {
        var items = new List<MenuItem>();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = path };
            string p = path;
            item.Click += async (_, _) =>
            {
                if (!File.Exists(p)) { SetMessage($"{p} no longer exists"); return; }
                if (await ConfirmDiscardAsync()) LoadFile(p);
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
        if (!await ConfirmDiscardAsync()) return;
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
        MainGrid.ColumnDefinitions[2].Width = new GridLength(Math.Clamp(_settings.VisualizerPanelWidth, 200, 1200));
        LeftGrid.RowDefinitions[2].Height = new GridLength(Math.Clamp(_settings.ConsoleHeight, 40, 800));
        if (_settings.VisualizerPanelWidth <= 0) SetPanelVisible(false);
    }

    private void SaveWindowState()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        if (VisualizerPanel.IsVisible) _settings.VisualizerPanelWidth = MainGrid.ColumnDefinitions[2].ActualWidth;
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
        AutoStartItem.IsChecked = _settings.StartTidalOnLaunch;
        ShowPanelItem.IsChecked = true;
        StyleBackgroundItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Background;
        StyleOutlineItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Outline;
        StyleUnderlineItem.IsChecked = _settings.HighlightStyle == HighlightStyle.Underline;
        UpdateBootMenu();
        RebuildRecentMenu();
    }

    private void AddVisualizer(string type)
    {
        var config = new VisualizerConfig { Type = type, Height = type is "spiral" or "pitchwheel" ? 260 : 180 };
        _settings.Visualizers.Add(config);
        AddTile(config);
        SetPanelVisible(true);
    }

    private void SetPanelVisible(bool visible)
    {
        VisualizerPanel.IsVisible = visible;
        MainGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        MainGrid.ColumnDefinitions[2].Width = visible ? new GridLength(Math.Clamp(_settings.VisualizerPanelWidth, 200, 1200)) : new GridLength(0);
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
        if (_currentPath is null)
        {
            await Dialogs.ShowMessage(this, "Save the file first", "Save your .tidal file first, so TidalStudio knows which folder to write BootTidal.hs to.");
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(_currentPath)!, BootFiles.BootFileName);
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
        string? sclang = ToolLocator.FindSclang(_settings.SclangPath);
        if (sclang is null)
        {
            await Dialogs.ShowMessage(this, "sclang not found", "Could not find SuperCollider's sclang. Use SuperCollider ▸ Choose sclang….");
            return;
        }
        if (Process.GetProcessesByName("scsynth").Length > 0 &&
            !await Dialogs.Confirm(this, "SuperCollider is already running",
                "A SuperCollider server (scsynth) is already running, probably with SuperDirt started from the SuperCollider IDE. Starting another may fail because port 57120 is taken.\n\nStart anyway?", "Start anyway"))
            return;
        try
        {
            _services.SuperDirt.Start(sclang, _settings.SuperDirtScript);
        }
        catch (Exception ex)
        {
            _console.Add(OutputKind.Error, $"Could not start sclang: {ex.Message}");
        }
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

        Visual feedback
          While a pattern plays, the parts of its mini-notation that sound light up in the code.
          Colours: one per pattern (d1, d2, …). Override per pattern with  # color "orange"  or  # color "ff8800"
          (hex without '#', because '#' is not allowed inside Tidal's mini-notation strings).
          Style (background / outline / underline): View ▸ Highlight style.

        Inline visuals (like Strudel's _pianoroll())
          Put a comment in a block:   -- _punchcard   -- _pianoroll   -- _spiral   -- _pitchwheel
                                      -- _scope   -- _spectrum   -- _cycle
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
        $"Settings: {AppSettings.DefaultPath}\n" +
        $"Editor event port: UDP {_settings.EditorOscPort}");
}
