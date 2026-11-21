using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App;

/// <summary>
/// "--selftest &lt;outdir&gt;": waits for Tidal, runs every block of the open file one by one, takes screenshots
/// of the window, writes the console log, then hushes and exits. Used to check the app end-to-end.
/// </summary>
public partial class MainWindow
{
    private async Task RunSelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        // Called from the test and from ghci's reader threads: one writer at a time.
        void Log(string s)
        {
            lock (log)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
                try { File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log); }
                catch (IOException) { }
            }
        }

        _services.Ghci.Output += l => Log($"[{l.Kind}] {l.Text}");
        Log("selftest started");
        for (int i = 0; i < 120 && !_services.Ghci.IsReady; i++)
        {
            if (_services.Ghci.State is GhciState.Failed or GhciState.Exited) break;
            await Task.Delay(500);
        }
        Log($"ghci state: {_services.Ghci.State}");
        if (_services.Ghci.IsReady)
        {
            var doc = Editor.Document;
            int line = 0;
            while (line < doc.LineCount)
            {
                var block = CodeBlocks.GetBlock(doc.Text, line);
                if (block is null) { line++; continue; }
                string text = block.Text.Trim();
                if (text != "hush")
                {
                    Log($"run lines {block.StartLine + 1}-{block.EndLine + 1}: {text.Split('\n')[0]}");
                    Editor.TextArea.Caret.Line = block.StartLine + 1;
                    await RunRangeAsync(block.StartLine, block.EndLine);
                    Log($"status: {StatusMessage.Text}");
                }
                line = block.EndLine + 1;
            }

            for (int shot = 1; shot <= 3; shot++)
            {
                await Task.Delay(1500);
                Log($"events received: {Interlocked.Read(ref _services.Osc.PacketsReceived)}, store: {_services.Events.Count}, active now: {_services.Events.Active(_services.Time.Now).Count}, highlights drawn: {_miniRenderer.LastDrawn}");
                await Dispatcher.UIThread.InvokeAsync(() => Screenshot(Path.Combine(outDir, $"shot{shot}.png")));
            }
            Log($"audio samples captured so far: {_services.Audio.TotalWritten} at {_services.Audio.SampleRate} Hz ({_services.AudioCapture.Description})");

            var mute = await ToggleMuteAsync("1");
            Log($"mute 1: success={mute.Success} {mute.ErrorText}");
            long before = _services.Events.Count;
            await Task.Delay(2500);
            int d1Events = _services.Events.Query(_services.Time.Now - 1.5, _services.Time.Now + 1).Count(e => e.PatternId == "1");
            Log($"d1 events in the last 1.5 s while muted: {d1Events}");
            var unmute = await ToggleMuteAsync("1");
            Log($"unmute 1: success={unmute.Success} {unmute.ErrorText}");

            // Real key events through the editor: they must run code and must not insert newlines.
            string textBefore = Editor.Document.Text;
            int runsBefore = _runCount;
            await SendKeyAsync(12, Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.Shift);
            Log($"Shift+Enter on line 12: runs +{_runCount - runsBefore}, text unchanged={Editor.Document.Text == textBefore}, last run lines={_lastRunLines}");
            runsBefore = _runCount;
            await SendKeyAsync(15, Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.Control);
            Log($"Ctrl+Enter on line 15: runs +{_runCount - runsBefore}, text unchanged={Editor.Document.Text == textBefore}, last run lines={_lastRunLines}");
            await SendKeyAsync(1, Avalonia.Input.Key.OemPeriod, Avalonia.Input.KeyModifiers.Control);
            Log($"Ctrl+. (hush): muted cleared={_muted.Count == 0}");

            var bad = await _services.Ghci.EvalAsync("d4 $ s \"bd\" # n (1 +)");
            Log($"deliberate error: success={bad.Success}, errors={bad.Errors.Count}, first line={bad.Errors.FirstOrDefault()?.Line}");
            await HushAsync();

            // Every block of every example script must compile and run in Tidal (the silent boot file plays nothing).
            foreach (var (_, file) in TidalStudio.Core.Synth.ExampleScripts.All)
            {
                string script = TidalStudio.Core.Synth.ExampleScripts.Read(file);
                int ok = 0, failed = 0, at = 0, lines = script.Split('\n').Length;
                while (at < lines)
                {
                    var block = CodeBlocks.GetBlock(script, at);
                    if (block is null) { at++; continue; }
                    at = block.EndLine + 1;
                    bool onlyComments = block.Text.Split('\n').All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("--", StringComparison.Ordinal));
                    if (onlyComments || block.Text.Trim() == "hush") continue;
                    var r = await _services.Ghci.EvalAsync(block.Text);
                    if (r.Success) ok++;
                    else { failed++; Log($"example {file} lines {block.StartLine + 1}-{block.EndLine + 1} FAILED: {r.ErrorText.Replace('\n', ' ')}"); }
                }
                Log($"example {file}: {ok} blocks ran, {failed} failed");
                await HushAsync();
            }

            await RunEditorChecksAsync(Log, outDir);
            if (_settings.SamplesFolder is not null) await RunSynthChecksAsync(Log, outDir);
            await RunVjChecksAsync(Log, outDir);
            await RunMuseChecksAsync(Log, outDir);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() => Screenshot(Path.Combine(outDir, "failed.png")));
        }
        Log("selftest done");
        foreach (var tab in _tabs) tab.Dirty = false;
        Close();
    }

    /// <summary>Autocomplete and tabs: types into a new tab and checks what the popup offers.</summary>
    private async Task RunEditorChecksAsync(Action<string> log, string outDir)
    {
        for (int i = 0; i < 40 && _completionCatalog.TidalNameCount == 0; i++) await Task.Delay(250);
        log($"completion: {_completionCatalog.TidalNameCount} Tidal names loaded");

        await Dispatcher.UIThread.InvokeAsync(() => NewDocument(withSample: false));

        async Task<IReadOnlyList<string>> TypeAsync(string text, bool newLine = true)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _completion.Close();
                Editor.Focus();
                var doc = Editor.Document;
                if (newLine && doc.TextLength > 0) doc.Insert(doc.TextLength, "\n");
                Editor.CaretOffset = doc.TextLength;
                foreach (char c in text) Editor.TextArea.PerformTextInput(c.ToString());
            });
            await Task.Delay(250);
            return await Dispatcher.UIThread.InvokeAsync(() => _completion.VisibleItems);
        }

        string Show(IReadOnlyList<string> items) =>
            $"{items.Count} items, selected={_completion.SelectedItem ?? "-"}: {string.Join(" ", items.Take(8))}";

        log("complete 'd1 $ s \"bd\" # roo' -> " + Show(await TypeAsync("d1 $ s \"bd\" # roo")));
        log("complete 'd1 $ every 2 (fa' -> " + Show(await TypeAsync("d1 $ every 2 (fa")));
        log("complete 'd1 $ s \"sup' -> " + Show(await TypeAsync("d1 $ s \"sup")));
        log("complete 'd2 $ s \"bd*2 h' -> " + Show(await TypeAsync("d2 $ s \"bd*2 h")));
        log("complete '  -- _pi' -> " + Show(await TypeAsync("  -- _pi")));
        log("complete '  -- _pianoroll ' -> " + Show(await TypeAsync("  -- _pianoroll ")));
        log("complete '  -- _pianoroll rows=' -> " + Show(await TypeAsync("  -- _pianoroll rows=")));
        log("plain typing 'd1 $ n' -> " + Show(await TypeAsync("d1 $ n")));

        // Screenshot of the popup (it is a separate window, so it is rendered on its own).
        await TypeAsync("d1 $ s \"bd\" # roo");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_completion.PopupContent is { Bounds.Width: > 0 } popup) Render(popup, Path.Combine(outDir, "completion.png"));
        });

        // Enter accepts a suggestion ...
        await TypeAsync("d3 $ s \"bd\" # spe");
        string state = await Dispatcher.UIThread.InvokeAsync(() => $"open={_completion.IsOpen} selected={_completion.SelectedItem}");
        await SendKeyToEndAsync(Avalonia.Input.Key.Enter);
        log($"Enter on '# spe' ({state}): lines = '{await Dispatcher.UIThread.InvokeAsync(() => LastLine(2))}' / '{await Dispatcher.UIThread.InvokeAsync(() => LastLine(1))}', open after={await Dispatcher.UIThread.InvokeAsync(() => _completion.IsOpen)}");
        // ... but not after a word that is already complete: then it is a new line.
        await TypeAsync("d4 $ fast");
        state = await Dispatcher.UIThread.InvokeAsync(() => $"open={_completion.IsOpen} selected={_completion.SelectedItem}");
        await SendKeyToEndAsync(Avalonia.Input.Key.Enter);
        log($"Enter after complete word 'fast' ({state}): lines = '{await Dispatcher.UIThread.InvokeAsync(() => LastLine(2))}' / '{await Dispatcher.UIThread.InvokeAsync(() => LastLine(1))}'");
        // Ctrl+Enter runs code even while a suggestion popup is open.
        await TypeAsync("d5 $ s \"bd\" # gai");
        state = await Dispatcher.UIThread.InvokeAsync(() => $"open={_completion.IsOpen}");
        int runs = _runCount;
        await SendKeyToEndAsync(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.Control);
        log($"Ctrl+Enter with popup ({state}): runs +{_runCount - runs}, line = '{await Dispatcher.UIThread.InvokeAsync(() => LastLine(1))}', open after={await Dispatcher.UIThread.InvokeAsync(() => _completion.IsOpen)}");
        await HushAsync();

        // Tabs
        string dir = Path.Combine(outDir, "tabs");
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.tidal"), b = Path.Combine(dir, "b.tidal");
        File.WriteAllText(a, "d1 $ s \"bd*4\"\n");
        File.WriteAllText(b, "d2 $ s \"hh*8\"\n");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            int before = _tabs.Count;
            OpenFile(a);
            OpenFile(b);
            log($"tabs: opened a and b: {before} -> {_tabs.Count}, active={_active?.DisplayName}");
            OpenFile(a);
            log($"tabs: opening a again switches to it: count={_tabs.Count}, active={_active?.DisplayName}, editor shows a={Editor.Document.Text.Contains("bd*4")}");
            CycleTab(1);
            log($"tabs: Ctrl+Tab -> active={_active?.DisplayName}");
            Editor.Document.Insert(0, "-- edited\n");
            log($"tabs: editing b marks only b dirty: b={_active?.Dirty}, a={_tabs.First(t => t.Path == a).Dirty}");
        });
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => Screenshot(Path.Combine(outDir, "tabs.png")));
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var tabA = _tabs.First(t => t.Path == a);
            bool closed = await CloseTabAsync(tabA);
            log($"tabs: close a (unmodified, no prompt): closed={closed}, count={_tabs.Count}, active={_active?.DisplayName}");
        });
    }

    /// <summary>Tidepool: open the window, screenshot it, and save the sound into the samples folder (no playback).</summary>
    private async Task RunSynthChecksAsync(Action<string> log, string outDir)
    {
        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Only restart a real SuperCollider when asked (--allow-restart): it stops whatever SuperCollider is running.
            Func<string, Task<(bool Ok, string Message)>> restart = _selfTestAllowRestart
                ? RestartSuperDirtAsync
                : folder => Task.FromResult((false, $"(self-test: SuperDirt restart skipped for {Path.GetFileName(folder)})"));
            var w = new Synth.SynthWindow(_services, () => _completionCatalog.InvalidateSamples(), (_, text) => log("[synth] " + text), restart);
            w.Show();
            return w;
        });
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() => Render(window, Path.Combine(outDir, "synth.png")));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Dispatcher.UIThread.InvokeAsync(window.SaveAsync);
        log($"synth: save took {watch.ElapsedMilliseconds} ms; status: {await Dispatcher.UIThread.InvokeAsync(() => window.StatusText)}");
        string folder = Path.Combine(_settings.SamplesFolder!, "sawlead"); // the default patch is the Supersaw lead
        if (Directory.Exists(folder))
            foreach (var f in Directory.GetFiles(folder))
                log($"synth: wrote {Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)");
        else log($"synth: nothing written to {folder}");
        await Dispatcher.UIThread.InvokeAsync(() => Render(window, Path.Combine(outDir, "synth-saved.png")));
        await Dispatcher.UIThread.InvokeAsync(window.Close);

        // Tidepool Drums: open, screenshot, save the kit (no playback).
        var drums = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = new Synth.DrumWindow(_services, () => _completionCatalog.InvalidateSamples(), (_, text) => log("[drums] " + text),
                folder => Task.FromResult((false, $"(self-test: SuperDirt restart skipped for {Path.GetFileName(folder)})")));
            w.Show();
            return w;
        });
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() => Render(drums, Path.Combine(outDir, "drums.png")));
        watch.Restart();
        await Dispatcher.UIThread.InvokeAsync(drums.SaveAsync);
        log($"drums: save took {watch.ElapsedMilliseconds} ms; status: {await Dispatcher.UIThread.InvokeAsync(() => drums.StatusText)}");
        foreach (var dir in Directory.GetDirectories(_settings.SamplesFolder!).Where(d => Path.GetFileName(d).StartsWith("mykit", StringComparison.Ordinal)))
            log($"drums: {Path.GetFileName(dir)}: {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}");
        await Dispatcher.UIThread.InvokeAsync(() => Render(drums, Path.Combine(outDir, "drums-saved.png")));
        await Dispatcher.UIThread.InvokeAsync(drums.Close);

        // Tidepool Acid: open, screenshot, save the loops (no playback).
        var acid = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = new Synth.AcidWindow(_services, () => _completionCatalog.InvalidateSamples(), (_, text) => log("[acid] " + text),
                folder => Task.FromResult((false, $"(self-test: SuperDirt restart skipped for {Path.GetFileName(folder)})")));
            w.Show();
            return w;
        });
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() => Render(acid, Path.Combine(outDir, "acid.png")));
        watch.Restart();
        await Dispatcher.UIThread.InvokeAsync(acid.SaveAsync);
        log($"acid: save took {watch.ElapsedMilliseconds} ms; status: {await Dispatcher.UIThread.InvokeAsync(() => acid.StatusText)}");
        string acidFolder = Path.Combine(_settings.SamplesFolder!, "acidloop");
        if (Directory.Exists(acidFolder))
            log($"acid: {string.Join(", ", Directory.GetFiles(acidFolder).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)"))}");
        else log($"acid: nothing written to {acidFolder}");
        await Dispatcher.UIThread.InvokeAsync(() => Render(acid, Path.Combine(outDir, "acid-saved.png")));
        await Dispatcher.UIThread.InvokeAsync(acid.Close);

        // Tidepool FM: open, screenshot, save the default patch (no playback).
        var fm = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = new Synth.FmWindow(_services, () => _completionCatalog.InvalidateSamples(), (_, text) => log("[fm] " + text),
                folder => Task.FromResult((false, $"(self-test: SuperDirt restart skipped for {Path.GetFileName(folder)})")));
            w.Show();
            return w;
        });
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() => Render(fm, Path.Combine(outDir, "fm.png")));
        watch.Restart();
        await Dispatcher.UIThread.InvokeAsync(fm.SaveAsync);
        log($"fm: save took {watch.ElapsedMilliseconds} ms; status: {await Dispatcher.UIThread.InvokeAsync(() => fm.StatusText)}");
        string fmFolder = Path.Combine(_settings.SamplesFolder!, "fmepiano"); // the default patch is the Tine piano
        if (Directory.Exists(fmFolder))
            log($"fm: {string.Join(", ", Directory.GetFiles(fmFolder).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)"))}");
        else log($"fm: nothing written to {fmFolder}");
        await Dispatcher.UIThread.InvokeAsync(() => Render(fm, Path.Combine(outDir, "fm-saved.png")));
        await Dispatcher.UIThread.InvokeAsync(fm.Close);

        if (_selfTestAllowRestart)
        {
            // Ask SuperDirt itself how many samples it has under the new name (proves the restart and the load).
            string answer = Path.Combine(outDir, "superdirt-buffers.txt").Replace('\\', '/');
            bool sent = _services.SuperDirt.TrySendCode(
                $"File.use(\"{answer}\", \"w\", {{ |f| f.write((~dirt.soundLibrary.buffers['sawlead'] ? []).size.asString) }});");
            for (int i = 0; i < 20 && !File.Exists(answer); i++) await Task.Delay(250);
            log($"synth: SuperDirt state={_dirtState}, code sent={sent}, buffers loaded for sawlead = {(File.Exists(answer) ? File.ReadAllText(answer) : "(no answer)")}");
        }
    }

    /// <summary>
    /// VJ screen: a fake performance (code in the code log, a groove of events tagged with that code's mini-notation
    /// positions, nothing played), then a screenshot of every scene, a transition and the controls.
    /// </summary>
    private async Task RunVjChecksAsync(Action<string> log, string outDir)
    {
        const string code = "do\n  d1 $ s \"bd*4\" # gain 1.1\n  d2 $ s \"~ sn ~ [sn cp]\"\n  d3 $ s \"hh*8\" # gain 0.8\n" +
                            "  d4 $ note \"c a f g\" # s \"fmpopbass\" # cut 1";
        var vj = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vj.VjRenderer.RasterMaxWidth = 960;
            _settings.Vj.Code = Core.Vj.VjCodeMode.Panel;
            _settings.Vj.Auto = false;
            _settings.Vj.Source = Core.Vj.VjSource.Events;
            var w = new Vj.VjWindow(_services) { Width = 1280, Height = 720 };
            w.Show();
            return w;
        });
        await Task.Delay(800);
        log($"vj: shader compile errors: {(Vj.VjRenderer.CompileErrors.Length == 0 ? "none" : Vj.VjRenderer.CompileErrors.Replace('\n', ' '))}");

        // The fake performance: 120 bpm, starting now, for 40 s.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            double now = _services.Time.Now;
            var pre = Preprocessor.Wrap(code, _services.Literals.NextId);
            _services.CodeLog.Add(code, true, pre.Literals, now - 3);
            _services.CodeLog.Add("setcps (120/60/4)", true, Array.Empty<LiteralRef>(), now - 0.5);
            var lits = pre.Literals;
            string Content(int i) => code.Substring(lits[i].Offset + 1, lits[i].ContentLength);
            var tokens = Enumerable.Range(0, lits.Count)
                .Select(i => System.Text.RegularExpressions.Regex.Matches(Content(i), "[a-z0-9]+").Select(m => (m.Index, m.Length, m.Value)).ToList())
                .ToList();
            const double cps = 0.5;
            var events = new List<Core.Events.TidalEvent>();
            for (int step = 0; step < 40 * 4; step++)
            {
                double t = now + step * 0.125, cycle = step / 16.0;
                void Add(int lit, int token, string sound, double gain, double? note = null) => events.Add(new Core.Events.TidalEvent
                {
                    Time = t, Duration = 0.12, Cycle = cycle, Cps = cps, PatternId = (lit + 1).ToString(), Sound = sound, Gain = gain, Note = note,
                    Pan = 0.5 + 0.3 * Math.Sin(step * 0.7),
                    Spans = new[] { new Core.Events.SourceSpan(lits[lit].Id, tokens[lit][token].Index, tokens[lit][token].Index + tokens[lit][token].Length) },
                });
                if (step % 4 == 0) Add(0, 0, "bd", 1.1);
                if (step % 16 == 4) Add(1, 0, "sn", 1);
                if (step % 16 == 12) Add(1, 1, "sn", 1);
                if (step % 16 == 14) Add(1, 2, "cp", 0.9);
                if (step % 2 == 0) Add(2, 0, "hh", 0.8);
                if (step % 4 == 2) Add(3, step / 4 % 4, "fmpopbass", 1, new[] { 0.0, -3, -7, -5 }[step / 16 % 4]);
            }
            foreach (var e in events)
            {
                _services.Events.Add(e);
                _services.Clock.Update(e);
            }
        });
        await Task.Delay(1500);
        log($"vj: running at {await Dispatcher.UIThread.InvokeAsync(() => vj.Canvas.Fps):0} fps (GPU path while visible); beats seen {await Dispatcher.UIThread.InvokeAsync(() => vj.Canvas.Signals.Beats)}");

        string dir = Path.Combine(outDir, "vj");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < Core.Vj.VjDirector.Scenes.Count; i++)
        {
            int scene = i;
            await Dispatcher.UIThread.InvokeAsync(() => { vj.Canvas.Director.Jump(scene); vj.ShowControls(false); });
            await Task.Delay(500);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string path = Path.Combine(dir, $"{i + 1}-{Core.Vj.VjDirector.Scenes[i].Replace(' ', '-').ToLowerInvariant()}.png");
            // A few frames so the trails build up in the software renderer too.
            for (int k = 0; k < 3; k++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { vj.Canvas.Step(); Render(vj.Canvas, path); });
                await Task.Delay(60);
            }
            log($"vj: scene {Core.Vj.VjDirector.Scenes[i]}: {new FileInfo(path).Length / 1024} KB, {watch.ElapsedMilliseconds / 3} ms per software frame");
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vj.Canvas.Options.TransitionSeconds = 3;
            vj.Canvas.Options.Transition = 4; // glitch slices
            vj.Canvas.Director.Jump(2);
            vj.Canvas.Director.Go(0, _services.Time.Now);
        });
        await Task.Delay(1400);
        await Dispatcher.UIThread.InvokeAsync(() => { vj.Canvas.Step(); Render(vj.Canvas, Path.Combine(dir, "transition.png")); });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vj.Canvas.Options.Code = Core.Vj.VjCodeMode.Overlay;
            vj.Canvas.Options.Kaleido = 6;
            vj.Canvas.Director.Jump(1);
            _services.CodeLog.SetDraft("d5 $ jux rev $ s \"arpy*8\" # n (irand 8)", _services.Time.Now);
            vj.ShowControls(true);
        });
        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() => { vj.Canvas.Step(); Render(vj, Path.Combine(dir, "controls-overlay.png")); });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vj.Canvas.Options.Kaleido = 0;
            vj.Canvas.Options.Code = Core.Vj.VjCodeMode.Panel;
            vj.Canvas.Options.Auto = true;
            vj.Canvas.Options.Source = Core.Vj.VjSource.Auto;
            vj.Close();
        });
        _services.Events.Clear();
        _services.Clock.Reset();
    }

    private string LastLine(int fromEnd)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByNumber(Math.Max(1, doc.LineCount - fromEnd + 1));
        return doc.GetText(line.Offset, line.Length);
    }

    /// <summary>
    /// Muse: a groove plays (silently), the suggestion popup makes ideas that fit it, Enter pastes one; the trainer
    /// makes an A/B pair. Screenshots of each.
    /// </summary>
    private async Task RunMuseChecksAsync(Action<string> log, string outDir)
    {
        const string groove = "do\n  setcps (118/60/4)\n  d1 $ s \"[gatedbd*4, ~ gatedsd ~ gatedsd, gatedhh*8]\"\n" +
                              "  d3 $ note \"<c'min af4'maj ef'maj bf4'maj>\" # s \"poppad\" # legato 1\n" +
                              "  d5 $ note \"<[c ~ c6 ~]*2 [af4 ~ af ~]*2 [ef ~ ef6 ~]*2 [bf4 ~ bf ~]*2>\" # s \"popbass\" # n 1";
        var r = await _services.Ghci.EvalAsync(groove);
        log($"muse: groove ran: {r.Success} {r.ErrorText.Replace('\n', ' ')}");
        await Task.Delay(500);
        var (context, bpm) = await MuseService.LiveContextAsync(4);
        log($"muse: context: {context.Describe()} · {bpm} bpm · model: {MuseService.ModelStatus}");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            NewDocument(withSample: false);
            Editor.Document.Text = "-- my track\n\n";
            Editor.TextArea.Caret.Line = 2;
        });
        foreach (var (kind, key) in new[] { ("melody", Avalonia.Input.Key.M), ("bass", Avalonia.Input.Key.B), ("newpart", Avalonia.Input.Key.P), ("drums", Avalonia.Input.Key.D) })
        {
            var popup = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var p = new Muse.SuggestPopup(MuseService, _services, InsertSuggestionAsync);
                p.ShowAt(this, this.PointToScreen(new Point(140, 160)));
                return p;
            });
            await Task.Delay(600);
            await Dispatcher.UIThread.InvokeAsync(() => popup.PressKey(key));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 20000 && (await Dispatcher.UIThread.InvokeAsync(() => popup.ItemCount)) == 0) await Task.Delay(200);
            await Task.Delay(1500); // a second refresh may follow the key
            while (watch.ElapsedMilliseconds < 30000 && (await Dispatcher.UIThread.InvokeAsync(() => popup.ItemCount)) == 0) await Task.Delay(200);
            log($"muse: {kind}: {await Dispatcher.UIThread.InvokeAsync(() => popup.ItemCount)} ideas · {await Dispatcher.UIThread.InvokeAsync(() => popup.StatusText)}");
            await Dispatcher.UIThread.InvokeAsync(() => Render((Control)popup.Content!, Path.Combine(outDir, $"muse-{kind}.png")));
            if (kind == "bass")
            {
                // Listen (Space), then paste (Enter).
                await Dispatcher.UIThread.InvokeAsync(() => popup.PressKey(Avalonia.Input.Key.Space));
                await Task.Delay(800);
                log($"muse: listening: {await Dispatcher.UIThread.InvokeAsync(() => popup.StatusText)}");
                await Dispatcher.UIThread.InvokeAsync(() => popup.PressKey(Avalonia.Input.Key.Enter));
                await Task.Delay(800);
                string text = await Dispatcher.UIThread.InvokeAsync(() => Editor.Document.Text);
                log($"muse: pasted: {text.Contains("-- suggestion: bassline")} ({text.Split('\n').Length} lines)");
            }
            else await Dispatcher.UIThread.InvokeAsync(() => popup.PressKey(Avalonia.Input.Key.Escape));
            await Task.Delay(300);
        }
        await Dispatcher.UIThread.InvokeAsync(() => Screenshot(Path.Combine(outDir, "muse-pasted.png")));

        var trainer = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = new Muse.MuseTrainerWindow(MuseService, _services, (_, t) => log("[muse] " + t)) { Width = 1000, Height = 780 };
            w.Show();
            return w;
        });
        await Task.Delay(800);
        await Dispatcher.UIThread.InvokeAsync(() => Render(trainer, Path.Combine(outDir, "muse-trainer-data.png")));
        await Dispatcher.UIThread.InvokeAsync(() => trainer.SelectTab(1));
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(trainer.NextPairForTestAsync);
        await Task.Delay(800);
        log($"muse: train by ear: {await Dispatcher.UIThread.InvokeAsync(() => trainer.EarStatusText)}");
        await Dispatcher.UIThread.InvokeAsync(() => Render(trainer, Path.Combine(outDir, "muse-trainer-ear.png")));
        await Dispatcher.UIThread.InvokeAsync(() => trainer.SelectTab(2));
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => Render(trainer, Path.Combine(outDir, "muse-trainer-training.png")));
        await Dispatcher.UIThread.InvokeAsync(() => trainer.SelectTab(3));
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => Render(trainer, Path.Combine(outDir, "muse-trainer-versions.png")));
        await Dispatcher.UIThread.InvokeAsync(trainer.Close);
        await HushAsync();
    }

    private async Task SendKeyToEndAsync(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None)
    {
        await Dispatcher.UIThread.InvokeAsync(() => Editor.TextArea.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = Editor.TextArea,
        }));
        await Task.Delay(250);
    }

    private static void Render(Control control, string path)
    {
        var size = new PixelSize(Math.Max(1, (int)control.Bounds.Width), Math.Max(1, (int)control.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(control);
#pragma warning disable CS0618
        bitmap.Save(path);
#pragma warning restore CS0618
    }

    private async Task SendKeyAsync(int line, Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Editor.Focus();
            Editor.TextArea.Caret.Line = line;
            Editor.TextArea.Caret.Column = 3;
            Editor.TextArea.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
                Source = Editor.TextArea,
            });
        });
        await Task.Delay(400);
    }

    /// <summary>
    /// "--sequencer-shots &lt;outdir&gt;": opens the sequencer of Tidepool, Tidepool FM and Tidepool Drums, renders each loop
    /// offline (nothing is played), screenshots the windows and writes the generated code. Settings are not saved.
    /// </summary>
    private async Task RunSequencerShotsAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        Func<string, Task<(bool Ok, string Message)>> noRestart = _ => Task.FromResult((false, "(skipped)"));
        try
        {
            var makers = new (string Name, Func<Window> Create, Func<Window, Synth.SequencerWindow> Open)[]
            {
                ("synth", () => new Synth.SynthWindow(_services, () => { }, (_, t) => log.Add("[synth] " + t), noRestart), w => ((Synth.SynthWindow)w).OpenSequencer()),
                ("fm", () => new Synth.FmWindow(_services, () => { }, (_, t) => log.Add("[fm] " + t), noRestart), w => ((Synth.FmWindow)w).OpenSequencer()),
                ("drums", () => new Synth.DrumWindow(_services, () => { }, (_, t) => log.Add("[drums] " + t), noRestart), w => ((Synth.DrumWindow)w).OpenSequencer()),
            };
            foreach (var (name, create, open) in makers)
            {
                var (owner, seq) = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var w = create();
                    w.Show();
                    return (w, open(w));
                });
                await Task.Delay(1500);
                await Dispatcher.UIThread.InvokeAsync(() => Render(owner, Path.Combine(outDir, $"window-{name}.png")));
                await Dispatcher.UIThread.InvokeAsync(() => Render(seq, Path.Combine(outDir, $"seq-{name}.png")));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var loop = await Dispatcher.UIThread.InvokeAsync(seq.RenderLoopAsync);
                float peak = loop.Left.Concat(loop.Right).Select(Math.Abs).DefaultIfEmpty(0).Max();
                log.Add($"{name}: loop {loop.Seconds:0.00} s rendered in {watch.ElapsedMilliseconds} ms, peak {peak:0.00}");
                log.Add($"{name} code:\n{await Dispatcher.UIThread.InvokeAsync(() => seq.CodeText)}");
                await Dispatcher.UIThread.InvokeAsync(seq.Randomise);
                await Task.Delay(300);
                await Dispatcher.UIThread.InvokeAsync(() => Render(seq, Path.Combine(outDir, $"seq-{name}-random.png")));
                log.Add($"{name} random code:\n{await Dispatcher.UIThread.InvokeAsync(() => seq.CodeText)}");
                await Dispatcher.UIThread.InvokeAsync(owner.Close);
            }

            // Tidepool Acid (its own step editor, no sequencer window).
            var acid = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var w = new Synth.AcidWindow(_services, () => { }, (_, t) => log.Add("[acid] " + t), noRestart);
                w.Show();
                return w;
            });
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() => Render(acid, Path.Combine(outDir, "window-acid.png")));
            await Dispatcher.UIThread.InvokeAsync(acid.Close);

            // Two sequencers open together share one tempo.
            var (synthOwner, drumOwner, synthSeq, drumSeq) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var s = new Synth.SynthWindow(_services, () => { }, (_, _) => { }, noRestart);
                var d = new Synth.DrumWindow(_services, () => { }, (_, _) => { }, noRestart);
                s.Show();
                d.Show();
                var ss = s.OpenSequencer();
                ss.Tempo = 111;
                var ds = d.OpenSequencer();
                return (s, d, ss, ds);
            });
            await Task.Delay(800);
            log.Add($"sync: synth sequencer {synthSeq.Tempo} bpm, drum sequencer opened after it {drumSeq.Tempo} bpm, shared {Synth.SequencerTransport.Shared.Bpm}");
            await Dispatcher.UIThread.InvokeAsync(() => drumSeq.Tempo = 127);
            log.Add($"sync: after setting 127 in the drums: synth sequencer {synthSeq.Tempo} bpm, code: {synthSeq.CodeText.Split('\n')[2].Trim()}");
            await Dispatcher.UIThread.InvokeAsync(() => Render(synthSeq, Path.Combine(outDir, "seq-sync.png")));
            await Dispatcher.UIThread.InvokeAsync(() => { synthOwner.Close(); drumOwner.Close(); });
        }
        catch (Exception e) { log.Add("FAILED: " + e); }
        File.WriteAllLines(Path.Combine(outDir, "sequencer.log"), log);
        _closeConfirmed = true; // leave the settings (open files, patches, sequences) as they were
        Close();
    }

    private void Screenshot(string path)
    {
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(this);
#pragma warning disable CS0618 // simple PNG save is all we need here
        bitmap.Save(path);
#pragma warning restore CS0618
    }
}
