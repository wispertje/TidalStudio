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
        void Log(string s) { log.Add($"{DateTime.Now:HH:mm:ss.fff} {s}"); File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log); }

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

            await RunEditorChecksAsync(Log, outDir);
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

    private string LastLine(int fromEnd)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByNumber(Math.Max(1, doc.LineCount - fromEnd + 1));
        return doc.GetText(line.Offset, line.Length);
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
