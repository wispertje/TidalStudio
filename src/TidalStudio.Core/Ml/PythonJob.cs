using System.Diagnostics;
using System.Text.Json;

namespace TidalStudio.Core.Ml;

/// <summary>
/// Runs a training script (train.py, dpo.py) in the Python environment and reports its JSON-line progress.
/// </summary>
public sealed class PythonJob : IDisposable
{
    private Process? _process;

    /// <summary>One JSON line from the script (event = data, model, progress, eval, done, error).</summary>
    public event Action<JsonElement>? Message;
    /// <summary>Anything else the script printed (warnings, tracebacks).</summary>
    public event Action<string>? Text;
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The folder with train.py and dpo.py: next to the app (published), or the repo's ml folder.</summary>
    public static string? ScriptsFolder()
    {
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "ml"), FindRepoMl() })
            if (dir is not null && File.Exists(Path.Combine(dir, "train.py"))) return dir;
        return null;
    }

    private static string? FindRepoMl()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ml", "train.py"))) return Path.Combine(dir.FullName, "ml");
        return null;
    }

    public static bool PythonReady => File.Exists(MlPaths.Python);

    public void Start(string script, IEnumerable<string> args)
    {
        if (IsRunning) throw new InvalidOperationException("A training job is already running.");
        string folder = ScriptsFolder() ?? throw new InvalidOperationException("The training scripts (ml\\train.py) were not found.");
        var psi = new ProcessStartInfo(MlPaths.Python)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(Path.Combine(folder, script));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } line) Handle(line); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Text?.Invoke(line); };
        p.Exited += (_, _) => Exited?.Invoke(SafeExitCode(p));
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private void Handle(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                Message?.Invoke(doc.RootElement.Clone());
                return;
            }
            catch (JsonException) { }
        }
        Text?.Invoke(line);
    }

    public void Stop()
    {
        try { if (IsRunning) _process!.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }

    /// <summary>A new, empty model version folder: models\yyyyMMdd-HHmmss-kind.</summary>
    public static string NewVersionFolder(string kind)
    {
        string dir = Path.Combine(MlPaths.Models, $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Commands that set up the Python environment (venv + PyTorch with CUDA when there's an NVIDIA GPU), as one
    /// script for <c>cmd /c</c>. Needs a Python 3.10–3.12 on the computer.
    /// </summary>
    public static string SetupScript(string basePython, bool cuda)
    {
        string venvPython = MlPaths.Python;
        string index = cuda ? "https://download.pytorch.org/whl/cu124" : "https://download.pytorch.org/whl/cpu";
        return $"\"{basePython}\" -m venv \"{MlPaths.Venv}\" && \"{venvPython}\" -m pip install --upgrade pip && " +
               $"\"{venvPython}\" -m pip install torch --index-url {index} && \"{venvPython}\" -m pip install numpy onnx onnxscript";
    }
}
