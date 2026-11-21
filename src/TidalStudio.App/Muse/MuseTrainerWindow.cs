using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TidalStudio.App.Services;
using TidalStudio.Core.Ml;
using TidalStudio.Core.Tidal;
using Path = System.IO.Path;

namespace TidalStudio.App.Muse;

/// <summary>
/// The Muse trainer: which sounds and music it learns from, train by ear (A or B?), training runs and model versions.
/// </summary>
public sealed class MuseTrainerWindow : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#171A21"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#20242E"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D7DCE5"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4ECDC4"));
    private static readonly IBrush Warm = new SolidColorBrush(Color.Parse("#FFB86B"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    private readonly MuseService _muse;
    private readonly AppServices _services;
    private readonly Action<OutputKind, string> _log;
    private readonly TabControl _tabs = new();

    public MuseTrainerWindow(MuseService muse, AppServices services, Action<OutputKind, string> log)
    {
        _muse = muse;
        _services = services;
        _log = log;
        Title = "TidalStudio — Muse trainer";
        Width = 980;
        Height = 760;
        MinWidth = 720;
        MinHeight = 520;
        Background = Bg;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TidalStudio/Assets/TidalStudio.ico"))); }
        catch (Exception) { }
        _tabs.Items.Add(new TabItem { Header = "Sounds & data", Content = Scroll(BuildData()) });
        _tabs.Items.Add(new TabItem { Header = "Train by ear", Content = Scroll(BuildEar()) });
        _tabs.Items.Add(new TabItem { Header = "Training", Content = Scroll(BuildTraining()) });
        _tabs.Items.Add(new TabItem { Header = "Versions", Content = Scroll(BuildVersions()) });
        Content = _tabs;
        KeyDown += OnKey;
        Closing += (_, _) =>
        {
            _ = _muse.StopAuditionAsync();
            _ = StopBackingAsync();
        };
    }

    private static ScrollViewer Scroll(Control c) => new() { Content = c, Padding = new Thickness(16, 12) };

    private static TextBlock H(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Accent, Margin = new Thickness(0, 8, 0, 2) };
    private static TextBlock P(string text) => new() { Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private static TextBlock T(string text = "") => new() { Text = text, FontSize = 12, Foreground = Text, TextWrapping = TextWrapping.Wrap };
    private static Button B(string text, string? tip = null)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(12, 5) };
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }
    private static StackPanel Row(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }
    private static Border Box(Control child) => new() { Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Child = child, Margin = new Thickness(0, 4) };

    // ================================================================== Sounds & data

    private readonly TextBlock _catalogSummary = T();
    private readonly StackPanel _soundRows = new() { Spacing = 2 };
    private readonly StackPanel _sources = new() { Spacing = 4 };
    private readonly TextBlock _ownStatus = T();
    private readonly TextBlock _midiStatus = T();
    private bool _dataBusy;

    private Control BuildData()
    {
        var refresh = B("Scan samples again");
        refresh.Click += (_, _) => { _muse.RefreshCatalog(); RefreshCatalog(); };
        var addFolder = B("Add folder…");
        addFolder.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Folder with .tidal files" });
            foreach (var f in folders)
                if (f.TryGetLocalPath() is { } path && !Sources.Contains(path)) Sources.Add(path);
            RefreshSources();
        };
        var import = B("Import my music", "Runs your .tidal files in a silent Tidal and learns what they play (nothing is heard).");
        import.Click += async (_, _) => await ImportOwnAsync();
        var download = B("Download public MIDI (≈240 MB)", "Lakh MIDI 'clean' (17,000 songs) and Groove MIDI (drums), both CC BY 4.0.");
        download.Click += async (_, _) => await DownloadMidiAsync();
        var build = B("Build pretraining data");
        build.Click += async (_, _) => await BuildPretrainAsync();

        RefreshCatalog();
        RefreshSources();
        RefreshDataStatus();
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                H("Sounds Muse may use"),
                P("Only the sounds in your samples folder are used: the ones the Tidepool synths made. Each sound has a role; change it when the guess is wrong."),
                Box(new StackPanel { Spacing = 6, Children = { Row(_catalogSummary, refresh), new ScrollViewer { Content = _soundRows, MaxHeight = 260 } } }),
                H("Your music"),
                P("Your .tidal files are the most valuable training data. Import runs them in a second, silent Tidal and records what they play, block by block."),
                Box(new StackPanel { Spacing = 6, Children = { _sources, Row(addFolder, import), _ownStatus } }),
                H("Public MIDI (pretraining)"),
                P("To learn music in general, Muse first trains on public MIDI files. This is needed once."),
                Box(new StackPanel { Spacing = 6, Children = { _midiStatus, Row(download, build) } }),
            },
        };
    }

    private List<string> Sources
    {
        get
        {
            var s = _services.Settings.Muse.ImportSources;
            if (s.Count == 0)
            {
                if (_services.Settings.WorkingDirectory is { } wd && Directory.Exists(wd)) s.Add(wd);
                if (PythonJob.ScriptsFolder() is { } ml && Path.Combine(Path.GetDirectoryName(ml)!, "examples") is var ex && Directory.Exists(ex)) s.Add(ex);
            }
            return s;
        }
    }

    private void RefreshSources()
    {
        _sources.Children.Clear();
        foreach (var src in Sources.ToList())
        {
            int count = Directory.Exists(src) ? Directory.EnumerateFiles(src, "*.tidal", SearchOption.AllDirectories).Count() : File.Exists(src) ? 1 : 0;
            var remove = B("Remove");
            remove.Padding = new Thickness(8, 2);
            remove.Click += (_, _) => { Sources.Remove(src); RefreshSources(); };
            _sources.Children.Add(Row(T($"{src}  ({count} .tidal files)"), remove));
        }
    }

    private void RefreshCatalog()
    {
        var c = _muse.Catalog;
        int Count(SoundKind k) => c.Sounds.Count(s => s.Kind == k);
        string roles = string.Join(", ", Enum.GetValues<PartRole>().Where(r => r != PartRole.Drums).Select(r => $"{c.ForRole(r).Count()} {r.ToString().ToLowerInvariant()}"));
        _catalogSummary.Text = $"{c.Root}: {Count(SoundKind.Pitched)} pitched sounds ({roles}), {c.Kits.Count} drum kits, {Count(SoundKind.Loop)} loops";
        _soundRows.Children.Clear();
        var overrides = SampleCatalog.LoadOverrides(MlPaths.RoleOverrides);
        foreach (var s in c.Sounds.Where(s => s.Kind == SoundKind.Pitched))
        {
            var combo = new ComboBox { FontSize = 11, Width = 110, ItemsSource = new[] { "Bass", "Melody", "Chords" }, SelectedItem = s.Role.ToString() };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not string r || !Enum.TryParse<PartRole>(r, out var role)) return;
                overrides[s.Name] = role;
                SampleCatalog.SaveOverrides(MlPaths.RoleOverrides, overrides);
                _muse.RefreshCatalog();
            };
            string pitches = string.Join(" ", s.Roots.Select(SynthPitch));
            _soundRows.Children.Add(Row(new TextBlock { Text = s.Name, Width = 150, FontFamily = Mono, FontSize = 12, Foreground = Text, VerticalAlignment = VerticalAlignment.Center }, combo,
                new TextBlock { Text = $"n 0..{s.Variants - 1}: {pitches}", FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center }));
        }
    }

    private static string SynthPitch(int midi) => Core.Synth.SynthPatch.NoteName(midi);

    private static long SequenceCount(string folder)
    {
        string index = System.IO.Path.Combine(folder, "index.bin");
        return File.Exists(index) ? Math.Max(0, new FileInfo(index).Length / 8 - 1) : 0;
    }

    private void RefreshDataStatus()
    {
        long own = SequenceCount(MlPaths.OwnData), pre = SequenceCount(MlPaths.PretrainData);
        _ownStatus.Text = own > 0 ? $"Imported: {own:N0} training sequences from your music." : "Not imported yet.";
        int midi = Directory.Exists(MlPaths.Downloads) ? Directory.EnumerateFiles(MlPaths.Downloads, "*.mid", SearchOption.AllDirectories).Take(100_000).Count() : 0;
        _midiStatus.Text = (midi > 0 ? $"Downloaded: {midi:N0} MIDI files. " : "Not downloaded yet. ") +
                           (pre > 0 ? $"Pretraining data: {pre:N0} sequences." : "No pretraining data yet.");
    }

    private async Task ImportOwnAsync()
    {
        if (_dataBusy) return;
        string? ghci = ToolLocator.FindGhci(_services.Settings.GhciPath);
        if (ghci is null) { _ownStatus.Text = "ghci was not found (Tidal ▸ Choose ghci…)."; return; }
        _dataBusy = true;
        _ownStatus.Text = "Starting a silent Tidal…";
        try
        {
            var files = Sources.SelectMany(s => Directory.Exists(s) ? Directory.EnumerateFiles(s, "*.tidal", SearchOption.AllDirectories) : File.Exists(s) ? new[] { s } : Array.Empty<string>()).Distinct().ToList();
            var catalog = _muse.Catalog;
            long count = await Task.Run(async () =>
            {
                await using var runner = new TidalRunner();
                await runner.StartAsync(ghci);
                var random = new Random(1234);
                var seen = new HashSet<ulong>();
                using var writer = new Dataset.Writer(MlPaths.OwnData);
                foreach (var f in files)
                {
                    Dispatcher.UIThread.Post(() => _ownStatus.Text = $"Importing {System.IO.Path.GetFileName(f)}…");
                    foreach (var song in await runner.ImportFileAsync(f, catalog))
                        foreach (var seq in Dataset.Sequences(song, random))
                            if (seen.Add(Dataset.Hash(seq))) writer.Add(seq);
                }
                return writer.Count;
            });
            _log(OutputKind.Info, $"Muse: imported {files.Count} files ({count} sequences).");
        }
        catch (Exception e) { _ownStatus.Text = "Import failed: " + e.Message; _dataBusy = false; return; }
        _dataBusy = false;
        RefreshDataStatus();
    }

    private async Task DownloadMidiAsync()
    {
        if (_dataBusy) return;
        _dataBusy = true;
        try
        {
            Directory.CreateDirectory(MlPaths.Downloads);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            foreach (var (url, file, folder, kind) in new[]
            {
                ("http://hog.ee.columbia.edu/craffel/lmd/clean_midi.tar.gz", "clean_midi.tar.gz", "lmd", "tar"),
                ("https://storage.googleapis.com/magentadata/datasets/groove/groove-v1.0.0-midionly.zip", "groove.zip", "groove", "zip"),
            })
            {
                string target = System.IO.Path.Combine(MlPaths.Downloads, folder);
                if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.mid", SearchOption.AllDirectories).Any()) continue;
                string path = System.IO.Path.Combine(MlPaths.Downloads, file);
                _midiStatus.Text = $"Downloading {file}…";
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    await using var src = await response.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(path);
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    var watch = Stopwatch.StartNew();
                    while ((read = await src.ReadAsync(buffer)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read));
                        done += read;
                        if (watch.ElapsedMilliseconds > 400)
                        {
                            watch.Restart();
                            _midiStatus.Text = $"Downloading {file}: {done / 1_000_000} MB" + (total is > 0 ? $" of {total / 1_000_000} MB" : "");
                        }
                    }
                }
                _midiStatus.Text = $"Unpacking {file}…";
                Directory.CreateDirectory(target);
                await Task.Run(() =>
                {
                    if (kind == "zip") System.IO.Compression.ZipFile.ExtractToDirectory(path, target, overwriteFiles: true);
                    else
                    {
                        using var fs = File.OpenRead(path);
                        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                        System.Formats.Tar.TarFile.ExtractToDirectory(gz, target, overwriteFiles: true);
                    }
                });
            }
        }
        catch (Exception e) { _midiStatus.Text = "Download failed: " + e.Message; _dataBusy = false; return; }
        _dataBusy = false;
        RefreshDataStatus();
    }

    private async Task BuildPretrainAsync()
    {
        if (_dataBusy) return;
        _dataBusy = true;
        try
        {
            var files = Directory.Exists(MlPaths.Downloads)
                ? Directory.EnumerateFiles(MlPaths.Downloads, "*.mid", SearchOption.AllDirectories).ToList() : new List<string>();
            if (files.Count == 0) { _midiStatus.Text = "Download the MIDI files first."; return; }
            long count = await Task.Run(() =>
            {
                var random = new Random(1234);
                var seen = new HashSet<ulong>();
                using var writer = new Dataset.Writer(MlPaths.PretrainData);
                int done = 0;
                foreach (var song in files.AsParallel().AsOrdered().Select(f => MidiReader.Read(f)))
                {
                    if (++done % 200 == 0) Dispatcher.UIThread.Post(() => _midiStatus.Text = $"Reading MIDI: {done:N0} of {files.Count:N0}…");
                    if (song is null) continue;
                    foreach (var seq in Dataset.Sequences(song, random))
                        if (seen.Add(Dataset.Hash(seq))) writer.Add(seq);
                }
                return writer.Count;
            });
            _log(OutputKind.Info, $"Muse: pretraining data has {count:N0} sequences.");
        }
        catch (Exception e) { _midiStatus.Text = "Failed: " + e.Message; }
        finally { _dataBusy = false; }
        RefreshDataStatus();
    }

    // ================================================================== Train by ear

    private readonly TextBlock _earContext = T();
    private readonly TextBlock _earStatus = T();
    private readonly TextBlock _earStats = T();
    private readonly SelectableTextBlock _codeA = new() { FontFamily = Mono, FontSize = 11.5, Foreground = Text, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _codeB = new() { FontFamily = Mono, FontSize = 11.5, Foreground = Text, TextWrapping = TextWrapping.Wrap };
    private readonly Border _cardA = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), BorderThickness = new Thickness(2) };
    private readonly Border _cardB = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), BorderThickness = new Thickness(2) };
    private readonly ComboBox _earRole = new() { ItemsSource = new[] { "Melody", "Bass", "Drums", "Chords" }, SelectedIndex = 0, Width = 110 };
    private readonly ComboBox _earBars = new() { ItemsSource = new[] { "2 bars", "4 bars" }, SelectedIndex = 1, Width = 90 };
    private readonly ComboBox _earBacking = new() { ItemsSource = new[] { "What's playing in Tidal", "Built-in groove (C minor)" }, SelectedIndex = 1, Width = 210 };
    private readonly List<ToggleButton> _reasons = new();
    private Suggestion? _a, _b;
    private char _playing = ' ';
    private bool _earBusy;
    private bool _backingOn;
    private const string BackingIds = "museback";

    private Control BuildEar()
    {
        var next = B("Next pair  (N)");
        next.Click += async (_, _) => await NextPairAsync();
        var playA = B("Play A  (A)");
        playA.Click += async (_, _) => await PlayAsync('A');
        var playB = B("Play B  (B)");
        playB.Click += async (_, _) => await PlayAsync('B');
        var stop = B("Stop  (S)");
        stop.Click += async (_, _) => await StopAllAsync();
        var aBetter = B("A is better  (1)");
        aBetter.Click += async (_, _) => await ChooseAsync(1);
        var bBetter = B("B is better  (2)");
        bBetter.Click += async (_, _) => await ChooseAsync(2);
        var both = B("Both good  (3)");
        both.Click += async (_, _) => await ChooseAsync(3);
        var neither = B("Neither  (0)");
        neither.Click += async (_, _) => await ChooseAsync(0);
        var undo = B("Undo last  (Backspace)");
        undo.Click += (_, _) => Undo();
        var reasons = new WrapPanel();
        foreach (var r in new[] { "great groove", "catchy", "fits the chords", "boring", "too busy", "too empty", "wrong notes", "too high", "too low" })
        {
            var t = new ToggleButton { Content = r, FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 6) };
            _reasons.Add(t);
            reasons.Children.Add(t);
        }
        _cardA.Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "A", FontWeight = FontWeight.Bold, Foreground = Accent }, _codeA } };
        _cardB.Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "B", FontWeight = FontWeight.Bold, Foreground = Warm }, _codeB } };
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        cards.Children.Add(_cardA);
        Grid.SetColumn(_cardB, 2);
        cards.Children.Add(_cardB);
        UpdateCards();
        UpdateEarStats();
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                H("Train by ear"),
                P("Muse makes two ideas, A and B, over a backing. Listen to both and pick the better one: every choice teaches it your taste straight away, and after every 50 ratings the model itself learns from them (Training tab). Tip: rate over different backings and take a break after ~20 minutes."),
                Box(new StackPanel { Spacing = 6, Children = { Row(T("Part"), _earRole, T("Length"), _earBars, T("Backing"), _earBacking, next), _earContext } }),
                cards,
                Row(playA, playB, stop),
                Row(aBetter, bBetter, both, neither, undo),
                P("Why? (optional, applies to your next choice)"),
                reasons,
                _earStatus,
                H("What Muse has learned"),
                Box(_earStats),
            },
        };
    }

    // For the self-test.
    internal void SelectTab(int index) => _tabs.SelectedIndex = index;
    internal Task NextPairForTestAsync() => NextPairAsync();
    internal string EarStatusText => _earStatus.Text ?? "";

    private void UpdateCards()
    {
        _codeA.Text = _a?.Code ?? "Press Next pair (N) to start.";
        _codeB.Text = _b?.Code ?? "";
        _cardA.Background = Card;
        _cardB.Background = Card;
        _cardA.BorderBrush = _playing == 'A' ? Accent : Brushes.Transparent;
        _cardB.BorderBrush = _playing == 'B' ? Warm : Brushes.Transparent;
    }

    private PartRole EarRole => Enum.TryParse<PartRole>(_earRole.SelectedItem as string, out var r) ? r : PartRole.Melody;

    private void UpdateEarStats()
    {
        var lines = new List<string>();
        int total = _muse.RatingCount;
        lines.Add($"{total} ratings in total" + (total - _services.Settings.Muse.RatingsAtLastUpdate is > 0 and var n ? $", {n} since the model last learned from them." : "."));
        foreach (var role in Enum.GetValues<PartRole>())
        {
            int count = _muse.Taste.RatingCount(role);
            if (count == 0) continue;
            double acc = _muse.Taste.Accuracy.GetValueOrDefault(role, 0.5);
            var strongest = _muse.Taste.Strongest(role).Select(x => (x.Weight > 0 ? "more " : "less ") + x.Feature);
            string likes = count >= 10 ? " · you like " + string.Join(", ", strongest) : " · rate at least 10 to see what it learned";
            lines.Add($"{role}: {count} rating{(count == 1 ? "" : "s")} · guesses your choice {acc:P0} of the time{likes}");
        }
        _earStats.Text = string.Join("\n", lines);
    }

    private async Task<TidalContext> EarContextAsync(int bars)
    {
        if (_earBacking.SelectedIndex == 0 && _services.Ghci.IsReady)
        {
            await StopBackingAsync();
            var (ctx, _) = await _muse.LiveContextAsync(bars);
            return ctx;
        }
        // The built-in backing: drums, bass and chords in C minor (Cm Ab Eb Bb), made by the rules.
        var request = new PartRequest { Key = MKey.CMinor, Bars = 4, Chords = new[] { new MChord(0, ChordQuality.Minor), new MChord(8, ChordQuality.Major), new MChord(3, ChordQuality.Major), new MChord(10, ChordQuality.Major) } };
        var random = new Random(7);
        var parts = new List<(string, MPart)>
        {
            ("museback-drums", RuleGenerator.Generate(request with { Role = PartRole.Drums, Density = 2 }, random)),
            ("museback-chords", RuleGenerator.Generate(request with { Role = PartRole.Chords, Density = 1 }, random)),
        };
        if (EarRole != PartRole.Bass) parts.Add(("museback-bass", RuleGenerator.Generate(request with { Role = PartRole.Bass, Density = 2 }, random)));
        parts.RemoveAll(p => p.Item2.Role == EarRole);
        var pitched = parts.Where(p => p.Item2.Role != PartRole.Drums).SelectMany(p => p.Item2.Notes).ToList();
        var ctxBuilt = new TidalContext { From = 0, Bars = 4, Parts = parts, Key = MKey.CMinor, Chords = Theory.DetectChords(pitched, 4) };
        if (_services.Ghci.IsReady && !_backingOn) await StartBackingAsync(parts);
        return ctxBuilt;
    }

    private async Task StartBackingAsync(List<(string Id, MPart Part)> parts)
    {
        var catalog = _muse.Catalog;
        var lines = new List<string> { "do", "  setcps (120/60/4)" };
        foreach (var (id, part) in parts)
        {
            string code;
            if (part.Role == PartRole.Drums)
            {
                string kit = catalog.Kits.ContainsKey("gated") ? "gated" : catalog.Kits.Keys.FirstOrDefault() ?? "";
                if (kit.Length == 0) continue;
                code = TidalWriter.DrumPart(part, catalog, kit, 1);
            }
            else
            {
                var sound = catalog.ForRole(part.Role).FirstOrDefault(s => s.Name is "poppad" or "popbass") ?? catalog.ForRole(part.Role).FirstOrDefault();
                if (sound is null) continue;
                code = TidalWriter.Pitched(part, sound, 1);
            }
            string body = code.Split('\n').Last();
            body = body[(body.IndexOf(" $ ", StringComparison.Ordinal) + 3)..];
            lines.Add($"  p \"{id}\" $ {body} # gain 0.8");
        }
        var r = await _services.Ghci.EvalAsync(string.Join("\n", lines));
        _backingOn = r.Success;
        if (!r.Success) _earStatus.Text = "Couldn't start the backing: " + r.ErrorText;
    }

    private async Task StopBackingAsync()
    {
        if (!_backingOn || !_services.Ghci.IsReady) return;
        _backingOn = false;
        await _services.Ghci.EvalQuietAsync("do\n  p \"museback-drums\" silence\n  p \"museback-chords\" silence\n  p \"museback-bass\" silence");
    }

    private async Task NextPairAsync()
    {
        if (_earBusy) return;
        _earBusy = true;
        try
        {
            await _muse.StopAuditionAsync();
            _playing = ' ';
            int bars = _earBars.SelectedIndex == 0 ? 2 : 4;
            var context = await EarContextAsync(bars);
            _earContext.Text = context.Describe();
            var kind = EarRole switch { PartRole.Bass => SuggestKind.Bass, PartRole.Drums => SuggestKind.Drums, PartRole.Chords => SuggestKind.Chords, _ => SuggestKind.Melody };
            var s = _services.Settings.Muse;
            var options = new SuggestOptions { Kind = kind, Bars = bars, Density = -1, Surprise = Math.Max(0.35, s.Surprise), UseModel = s.UseModel, Count = 2, Bpm = 120 };
            var suggester = _muse.CreateSuggester();
            // A and B use the same sound, so you compare the music, not the sound.
            var random = new Random();
            var (sound, kit) = suggester.ChooseSound(EarRole, context, null, random);
            options = options with { Sound = sound?.Name ?? kit };
            var items = await Task.Run(() => suggester.Suggest(context, options, random));
            if (items.Count < 2) { _earStatus.Text = "Couldn't make two ideas (check the sounds tab)."; return; }
            (_a, _b) = (items[0], items[1]);
            // Random order, so the taste model's favourite isn't always A.
            if (random.Next(2) == 0) (_a, _b) = (_b, _a);
            UpdateCards();
            _earStatus.Text = $"Ideas from the {_a.Source}. Listen to A and B, then choose.";
            if (_services.Ghci.IsReady) await PlayAsync('A');
            else _earStatus.Text = "Start Tidal (and SuperDirt) to hear the ideas.";
        }
        catch (Exception e) { _earStatus.Text = "Failed: " + e.Message; }
        finally { _earBusy = false; }
    }

    private async Task PlayAsync(char which)
    {
        var s = which == 'A' ? _a : _b;
        if (s is null) return;
        var r = await _muse.AuditionAsync(s);
        _playing = r.Success ? which : ' ';
        if (!r.Success) _earStatus.Text = "Couldn't play it: " + r.ErrorText;
        UpdateCards();
    }

    private async Task StopAllAsync()
    {
        await _muse.StopAuditionAsync();
        _playing = ' ';
        UpdateCards();
    }

    private List<string> TakeReasons()
    {
        var list = _reasons.Where(r => r.IsChecked == true).Select(r => (string)r.Content!).ToList();
        foreach (var r in _reasons) r.IsChecked = false;
        return list;
    }

    private async Task ChooseAsync(int choice)
    {
        if (_a is null || _b is null) return;
        var reasons = TakeReasons();
        switch (choice)
        {
            case 1: _muse.Prefer(_a, _b, reasons); break;
            case 2: _muse.Prefer(_b, _a, reasons); break;
            case 3: _muse.Like(_a, "like", reasons); _muse.Like(_b, "like", reasons); break;
            default: _muse.Dislike(_a, reasons); _muse.Dislike(_b, reasons); break;
        }
        UpdateEarStats();
        MaybeAutoUpdate();
        await NextPairAsync();
    }

    private void Undo()
    {
        RatingStore.RemoveLast();
        _earStatus.Text = "Removed the last rating from the training data (the instant taste keeps it until it learns more).";
        UpdateEarStats();
    }

    /// <summary>After enough new ratings, let the model learn from them in the background.</summary>
    private void MaybeAutoUpdate()
    {
        var s = _services.Settings.Muse;
        int total = _muse.RatingCount;
        if (s.AutoUpdateEvery <= 0 || total - s.RatingsAtLastUpdate < s.AutoUpdateEvery) return;
        if (_job is { IsRunning: true } || _muse.ActiveVersion is null || !PythonJob.PythonReady) return;
        _earStatus.Text = $"{total - s.RatingsAtLastUpdate} new ratings: the model is learning from them in the background (Training tab).";
        StartRatingsUpdate();
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        if (_tabs.SelectedIndex != 1 || e.Source is TextBox or ComboBox) return;
        switch (e.Key)
        {
            case Key.N: e.Handled = true; await NextPairAsync(); break;
            case Key.A: e.Handled = true; await PlayAsync('A'); break;
            case Key.B: e.Handled = true; await PlayAsync('B'); break;
            case Key.S: e.Handled = true; await StopAllAsync(); break;
            case Key.Space: e.Handled = true; await PlayAsync(_playing == 'A' ? 'B' : 'A'); break;
            case Key.D1 or Key.NumPad1: e.Handled = true; await ChooseAsync(1); break;
            case Key.D2 or Key.NumPad2: e.Handled = true; await ChooseAsync(2); break;
            case Key.D3 or Key.NumPad3: e.Handled = true; await ChooseAsync(3); break;
            case Key.D0 or Key.NumPad0: e.Handled = true; await ChooseAsync(0); break;
            case Key.Back: e.Handled = true; Undo(); break;
        }
    }

    // ================================================================== Training

    private readonly TextBlock _pythonStatus = T();
    private readonly TextBlock _jobStatus = T();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 8 };
    private readonly Canvas _chart = new() { Height = 150, Background = new SolidColorBrush(Color.Parse("#12151B")) };
    private readonly TextBox _jobLog = new() { IsReadOnly = true, FontFamily = Mono, FontSize = 11, Height = 160, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
    private readonly List<(int Step, double Loss)> _losses = new();
    private readonly List<(int Step, double Loss)> _valLosses = new();
    private PythonJob? _job;
    private string? _jobFolder;
    private string _jobKind = "";
    private int _jobStartRatings;

    private Control BuildTraining()
    {
        var setup = B("Set up Python (PyTorch, ≈3 GB)", "Creates a Python environment just for Muse and installs PyTorch (with CUDA for NVIDIA cards).");
        setup.Click += async (_, _) => await SetupPythonAsync();
        var pretrain = B("Pretrain on public MIDI (≈1–2 h)");
        pretrain.Click += (_, _) => StartTraining("pretrain");
        var fine = B("Fine-tune on my music (≈2–5 min)");
        fine.Click += (_, _) => StartTraining("finetune");
        var ratings = B("Learn from my ratings (≈1–5 min)");
        ratings.Click += (_, _) => StartRatingsUpdate();
        var stop = B("Stop");
        stop.Click += (_, _) => { _job?.Stop(); _jobStatus.Text = "Stopped."; };
        RefreshPython();
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                H("Python"),
                Box(new StackPanel { Spacing = 6, Children = { _pythonStatus, Row(setup) } }),
                H("Train"),
                P("1. Pretrain once on public MIDI (Sounds & data: download + build first).  2. Fine-tune on your music whenever you've made new tracks.  3. Learn from your ratings after rating by ear (this also happens by itself every 50 ratings). Every run is saved as a new version; the newest is used."),
                Box(new StackPanel { Spacing = 8, Children = { Row(pretrain, fine, ratings, stop), _progress, _jobStatus, _chart, _jobLog } }),
            },
        };
    }

    private void RefreshPython()
    {
        string? scripts = PythonJob.ScriptsFolder();
        _pythonStatus.Text = (PythonJob.PythonReady ? $"Ready: {MlPaths.Venv}" : "Not set up yet.") +
                             (scripts is null ? "  (training scripts not found!)" : $"  Scripts: {scripts}");
    }

    private async Task SetupPythonAsync()
    {
        string? python = ToolLocator.FindOnPath("python") ?? ToolLocator.FindOnPath("py");
        string local = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        if (Directory.Exists(local))
            python = Directory.EnumerateFiles(local, "python.exe", SearchOption.AllDirectories).OrderByDescending(p => p).FirstOrDefault() ?? python;
        if (python is null) { _pythonStatus.Text = "Install Python 3.12 from python.org first."; return; }
        bool cuda = ToolLocator.FindOnPath("nvidia-smi") is not null || File.Exists(@"C:\Windows\System32\nvidia-smi.exe");
        _pythonStatus.Text = $"Setting up ({(cuda ? "CUDA" : "CPU")}), this takes a while…";
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(PythonJob.SetupScript(python, cuda));
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) Dispatcher.UIThread.Post(() => AppendLog(l)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) Dispatcher.UIThread.Post(() => AppendLog(l)); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        RefreshPython();
        if (p.ExitCode != 0) _pythonStatus.Text += $"  (setup failed with code {p.ExitCode}; see the log below)";
    }

    private void StartTraining(string kind)
    {
        if (_job is { IsRunning: true }) { _jobStatus.Text = "A training run is already going."; return; }
        if (!PythonJob.PythonReady) { _jobStatus.Text = "Set up Python first."; return; }
        var args = new List<string>();
        var active = _muse.ActiveVersion;
        if (kind == "pretrain")
        {
            if (SequenceCount(MlPaths.PretrainData) == 0) { _jobStatus.Text = "Build the pretraining data first (Sounds & data)."; return; }
            args.AddRange(new[] { "--data", MlPaths.PretrainData, "--steps", "30000" });
            if (SequenceCount(MlPaths.OwnData) > 0) args.AddRange(new[] { "--own", MlPaths.OwnData, "--own-weight", "2" });
        }
        else
        {
            if (active is null || !File.Exists(active.Checkpoint)) { _jobStatus.Text = "Pretrain first: fine-tuning starts from a trained model."; return; }
            if (SequenceCount(MlPaths.OwnData) == 0) { _jobStatus.Text = "Import your music first (Sounds & data)."; return; }
            args.AddRange(new[] { "--init", active.Checkpoint, "--own", MlPaths.OwnData, "--own-share", "0.5", "--steps", "400", "--lr", "1e-4", "--warmup", "50", "--eval-every", "100" });
            if (SequenceCount(MlPaths.PretrainData) > 0) args.AddRange(new[] { "--data", MlPaths.PretrainData });
        }
        _jobFolder = PythonJob.NewVersionFolder(kind);
        args.AddRange(new[] { "--out", _jobFolder });
        Run("train.py", args, kind);
    }

    private void StartRatingsUpdate()
    {
        if (_job is { IsRunning: true }) { _jobStatus.Text = "A training run is already going."; return; }
        if (!PythonJob.PythonReady) { _jobStatus.Text = "Set up Python first."; return; }
        var active = _muse.ActiveVersion;
        if (active is null || !File.Exists(active.Checkpoint)) { _jobStatus.Text = "Train a model first; until then your ratings already steer the choices (taste)."; return; }
        if (!File.Exists(MlPaths.Ratings)) { _jobStatus.Text = "No ratings yet."; return; }
        _jobFolder = PythonJob.NewVersionFolder("ratings");
        _jobStartRatings = _muse.RatingCount;
        Run("dpo.py", new[] { "--init", active.Checkpoint, "--ratings", MlPaths.Ratings, "--out", _jobFolder }, "ratings");
    }

    private void Run(string script, IEnumerable<string> args, string kind)
    {
        _losses.Clear();
        _valLosses.Clear();
        _jobKind = kind;
        _progress.Value = 0;
        _jobLog.Text = "";
        _job?.Dispose();
        _job = new PythonJob();
        _job.Message += m => Dispatcher.UIThread.Post(() => OnJobMessage(m));
        _job.Text += t => Dispatcher.UIThread.Post(() => AppendLog(t));
        _job.Exited += code => Dispatcher.UIThread.Post(() => OnJobExited(code));
        try
        {
            _job.Start(script, args);
            _jobStatus.Text = $"Running {kind}…";
            _log(OutputKind.Info, $"Muse: {kind} started.");
        }
        catch (Exception e) { _jobStatus.Text = "Couldn't start: " + e.Message; }
    }

    private void OnJobMessage(JsonElement m)
    {
        string ev = m.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
        switch (ev)
        {
            case "progress":
                int step = m.GetProperty("step").GetInt32(), steps = m.GetProperty("steps").GetInt32();
                double loss = m.GetProperty("loss").GetDouble();
                _losses.Add((step, loss));
                _progress.Value = steps > 0 ? (double)step / steps : 0;
                string eta = m.TryGetProperty("eta_seconds", out var etaV) ? $" · about {TimeSpan.FromSeconds(etaV.GetInt32()):h\\:mm\\:ss} left" : "";
                _jobStatus.Text = $"{_jobKind}: step {step:N0} of {steps:N0} · loss {loss:0.000}{eta}";
                DrawChart();
                break;
            case "eval":
                _valLosses.Add((m.GetProperty("step").GetInt32(), m.GetProperty("val_loss").GetDouble()));
                DrawChart();
                AppendLog($"validation loss {m.GetProperty("val_loss").GetDouble():0.000} at step {m.GetProperty("step").GetInt32()}");
                break;
            default:
                AppendLog(m.ToString());
                break;
        }
    }

    private void OnJobExited(int code)
    {
        if (code == 0 && _jobFolder is not null && File.Exists(System.IO.Path.Combine(_jobFolder, "model.onnx")))
        {
            var name = System.IO.Path.GetFileName(_jobFolder);
            File.WriteAllText(System.IO.Path.Combine(_jobFolder, "kind.txt"), _jobKind);
            _muse.UseVersion(name);
            if (_jobKind == "ratings") _services.Settings.Muse.RatingsAtLastUpdate = _jobStartRatings;
            _services.Settings.Save();
            _jobStatus.Text = $"Done: version {name} is now in use.";
            _progress.Value = 1;
            _log(OutputKind.Info, $"Muse: {_jobKind} finished, using version {name}.");
            RefreshVersions();
            UpdateEarStats();
        }
        else
        {
            _jobStatus.Text = $"The {_jobKind} run ended without a model (code {code}). See the log.";
            try { if (_jobFolder is not null && !File.Exists(System.IO.Path.Combine(_jobFolder, "model.onnx"))) Directory.Delete(_jobFolder, true); }
            catch (IOException) { }
        }
    }

    private void AppendLog(string line)
    {
        var text = (_jobLog.Text ?? "") + line + "\n";
        if (text.Length > 20000) text = text[^20000..];
        _jobLog.Text = text;
        _jobLog.CaretIndex = text.Length;
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        double w = Math.Max(100, _chart.Bounds.Width), h = _chart.Height;
        var all = _losses.Concat(_valLosses).ToList();
        if (all.Count < 2) return;
        double maxStep = all.Max(x => x.Step), lo = all.Min(x => x.Loss), hi = all.Max(x => x.Loss);
        if (hi - lo < 1e-6) hi = lo + 1;
        Point P(int step, double loss) => new(8 + (w - 16) * step / Math.Max(1, maxStep), 8 + (h - 16) * (1 - (loss - lo) / (hi - lo)));
        _chart.Children.Add(new Polyline { Points = _losses.Select(x => P(x.Step, x.Loss)).ToList(), Stroke = Accent, StrokeThickness = 1.5 });
        if (_valLosses.Count > 1) _chart.Children.Add(new Polyline { Points = _valLosses.Select(x => P(x.Step, x.Loss)).ToList(), Stroke = Warm, StrokeThickness = 2 });
        var label = new TextBlock { Text = $"loss {lo:0.00}–{hi:0.00}   (blue: training, orange: validation)", Foreground = Dim, FontSize = 10 };
        Canvas.SetLeft(label, 10);
        Canvas.SetTop(label, 4);
        _chart.Children.Add(label);
    }

    // ================================================================== Versions

    private readonly StackPanel _versionRows = new() { Spacing = 4 };
    private readonly CheckBox _useModel = new() { Content = "Use the model for suggestions (off: the rules only)", Foreground = Text };

    private Control BuildVersions()
    {
        _useModel.IsChecked = _services.Settings.Muse.UseModel;
        _useModel.IsCheckedChanged += (_, _) => _services.Settings.Muse.UseModel = _useModel.IsChecked == true;
        RefreshVersions();
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                H("Model versions"),
                P("Every training run is saved. If a new version is worse, switch back to an older one."),
                _useModel,
                _versionRows,
            },
        };
    }

    private void RefreshVersions()
    {
        _versionRows.Children.Clear();
        var active = _muse.ActiveVersion;
        var all = ModelVersion.All();
        if (all.Count == 0) _versionRows.Children.Add(P("No versions yet: train one in the Training tab. Until then suggestions come from the rules (and your taste)."));
        foreach (var v in all)
        {
            bool current = active?.Name == v.Name;
            var use = B(current ? "In use" : "Use");
            use.IsEnabled = !current;
            use.Click += (_, _) => { _muse.UseVersion(v.Name); _services.Settings.Save(); RefreshVersions(); };
            var delete = B("Delete");
            delete.IsEnabled = !current;
            delete.Click += (_, _) =>
            {
                try { Directory.Delete(v.Folder, true); }
                catch (IOException e) { _jobStatus.Text = e.Message; }
                RefreshVersions();
            };
            string kind = File.Exists(System.IO.Path.Combine(v.Folder, "kind.txt")) ? File.ReadAllText(System.IO.Path.Combine(v.Folder, "kind.txt")) : "";
            var text = new StackPanel
            {
                Width = 560,
                Children =
                {
                    new TextBlock { Text = v.Name + (current ? "   ✓" : ""), Foreground = current ? Accent : Text, FontFamily = Mono, FontSize = 12 },
                    new TextBlock { Text = $"{v.Created:g} · {kind} {v.Description}", Foreground = Dim, FontSize = 11 },
                },
            };
            _versionRows.Children.Add(Box(Row(text, use, delete)));
        }
    }
}
