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

    /// <summary>True when a SuperCollider script creates and starts SuperDirt itself (ignoring // comment lines).</summary>
    public static bool StartsSuperDirt(string script)
    {
        var code = string.Join('\n', script.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimStart()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
        return code.Contains("SuperDirt(", StringComparison.Ordinal) && code.Contains(".start(", StringComparison.Ordinal);
    }

    public void Start(string sclangPath, string? script = null)
    {
        if (IsRunning) return;

        // sclang always runs the user's startup.scd first. If that already starts SuperDirt (with the user's own
        // sample folders), running our script as well would reboot the server and start a second SuperDirt with
        // only the default samples. So let the startup file do the work.
        string? startup = Completion.SampleLibrary.StartupScriptPath();
        bool useStartupFile = string.IsNullOrWhiteSpace(script) && startup is not null && StartsSuperDirt(SafeRead(startup));
        string? scriptPath = null;
        if (!useStartupFile)
        {
            scriptPath = Path.Combine(Path.GetTempPath(), "TidalStudio-superdirt.scd");
            File.WriteAllText(scriptPath, string.IsNullOrWhiteSpace(script) ? DefaultStartupScript : script, new UTF8Encoding(false));
        }

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
        if (scriptPath is not null) psi.ArgumentList.Add(scriptPath);
        // sclang boots the server through the shell by bare name ("scsynth.exe"). Started as a child process that
        // name is not found ("'scsynth.exe' is not recognized") and the server exits with code 1, so put
        // SuperCollider's folder on PATH.
        if (Path.GetDirectoryName(sclangPath) is { Length: > 0 } scDir)
            psi.Environment["PATH"] = scDir + Path.PathSeparator + ToolLocator.FreshPath();

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
        if (useStartupFile)
            Output?.Invoke(new OutputLine(OutputKind.Info, $"Your SuperCollider startup file starts SuperDirt, so it is used as is: {startup}"));
    }

    /// <summary>
    /// Runs SuperCollider code in the sclang this launcher started (sclang executes stdin input on a form feed).
    /// Returns false when TidalStudio didn't start SuperCollider itself.
    /// </summary>
    public bool TrySendCode(string code)
    {
        var process = _process;
        if (process is null || process.HasExited) return false;
        try
        {
            process.StandardInput.Write(code + "\x0c");
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
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
