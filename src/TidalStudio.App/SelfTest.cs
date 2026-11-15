using Avalonia;
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

            var bad = await _services.Ghci.EvalAsync("d4 $ s \"bd\" # n (1 +)");
            Log($"deliberate error: success={bad.Success}, errors={bad.Errors.Count}, first line={bad.Errors.FirstOrDefault()?.Line}");
            await HushAsync();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() => Screenshot(Path.Combine(outDir, "failed.png")));
        }
        Log("selftest done");
        _dirty = false;
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
