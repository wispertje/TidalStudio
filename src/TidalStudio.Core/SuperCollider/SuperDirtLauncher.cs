using System.Diagnostics;
using System.Text;
using TidalStudio.Core.Tidal;

namespace TidalStudio.Core.SuperCollider;

/// <summary>Starts sclang with a script that boots the server and starts SuperDirt.</summary>
public sealed class SuperDirtLauncher : IAsyncDisposable
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<OutputLine>? Output;
    public event Action? Exited;

    public const string DefaultStartupScript =
        """
        (
        s.reboot {
            s.options.numBuffers = 1024 * 256;
            s.options.memSize = 8192 * 32;
            s.options.numWireBufs = 2048;
            s.options.maxNodes = 1024 * 32;
            s.options.numOutputBusChannels = 2;
            s.options.numInputBusChannels = 2;
            s.waitForBoot {
                ~dirt.stop;
                ~dirt = SuperDirt(2, s);
                ~dirt.loadSoundFiles;
                s.sync;
                ~dirt.start(57120, 0 ! 12);
                "TidalStudio: SuperDirt started".postln;
            };
            s.latency = 0.3;
        };
        )
        """;

    public void Start(string sclangPath, string? script = null)
    {
        if (IsRunning) return;
        string scriptPath = Path.Combine(Path.GetTempPath(), "TidalStudio-superdirt.scd");
        File.WriteAllText(scriptPath, string.IsNullOrWhiteSpace(script) ? DefaultStartupScript : script, new UTF8Encoding(false));

        var psi = new ProcessStartInfo(sclangPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sclangPath) ?? Environment.CurrentDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(scriptPath);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(new OutputLine(OutputKind.Output, e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(new OutputLine(OutputKind.Error, e.Data)); };
        process.Exited += (_, _) =>
        {
            Output?.Invoke(new OutputLine(OutputKind.Info, "sclang exited."));
            Exited?.Invoke();
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        Output?.Invoke(new OutputLine(OutputKind.Info, $"Started {sclangPath}"));
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    // Quit the server cleanly, then sclang.
                    process.StandardInput.Write("Server.killAll; 0.exit;\x0c");
                    process.StandardInput.Flush();
                }
                catch (IOException) { }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
