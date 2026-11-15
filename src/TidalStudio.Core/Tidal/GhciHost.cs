using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TidalStudio.Core.Tidal;

public enum OutputKind { Output, Error, Info, Input }

public sealed record OutputLine(OutputKind Kind, string Text);

/// <summary>An error reported by ghci, mapped back to a line of the evaluated block.</summary>
/// <param name="Line">0-based line within the evaluated block, or -1 when unknown.</param>
/// <param name="Column">1-based column as reported by ghci (columns may be shifted by preprocessing).</param>
public sealed record EvalError(int Line, int Column, string Message);

public sealed record EvalResult(bool Success, IReadOnlyList<EvalError> Errors, string ErrorText)
{
    public static EvalResult NotRunning { get; } = new(false, new[] { new EvalError(-1, 0, "Tidal is not running") }, "Tidal is not running");
}

public enum GhciState { Stopped, Starting, Ready, Exited, Failed }

/// <summary>
/// Runs ghci with a Tidal boot file and evaluates code in it, like the editor plugins do: code goes to stdin
/// (multi-line code wrapped in :{ :}), output and errors are read back and reported per evaluation.
/// </summary>
public sealed partial class GhciHost : IAsyncDisposable
{
    private const string ReadyMarker = "#TSREADY#";
    private const string DoneMarkerPrefix = "#TSDONE#";

    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly object _writeLock = new();
    private Process? _process;
    private int _linesSent;
    private TaskCompletionSource<bool>? _ready;
    private PendingEval? _pending;
    private int _evalCounter;

    public GhciState State { get; private set; } = GhciState.Stopped;
    public string? GhciPath { get; private set; }

    public event Action<OutputLine>? Output;
    public event Action<GhciState>? StateChanged;

    public bool IsReady => State == GhciState.Ready;

    private sealed class PendingEval
    {
        public required int Id { get; init; }
        public required int FirstCodeLine { get; init; } // ghci's global input line number of the block's first line
        public required int LineCount { get; init; }
        public List<string> ErrorLines { get; } = new();
        public TaskCompletionSource<EvalResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Starts ghci and sends the boot file. Completes when Tidal is ready.</summary>
    public async Task StartAsync(string ghciPath, string bootText, string? workingDirectory, TimeSpan timeout, CancellationToken token = default)
    {
        await StopAsync().ConfigureAwait(false);

        GhciPath = ghciPath;
        SetState(GhciState.Starting);
        Emit(OutputKind.Info, $"Starting {ghciPath}");

        var psi = new ProcessStartInfo(ghciPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory)
                ? Environment.CurrentDirectory : workingDirectory,
        };
        psi.Environment["PATH"] = ToolLocator.FreshPath();

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnExited(process);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ghci did not start");
        }
        catch (Exception e)
        {
            SetState(GhciState.Failed);
            Emit(OutputKind.Error, $"Could not start ghci: {e.Message}");
            throw;
        }

        _process = process;
        _linesSent = 0;
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoop(process, process.StandardOutput, isError: false));
        _ = Task.Run(() => ReadLoop(process, process.StandardError, isError: true));

        var lines = new List<string> { ":set prompt \"\"", ":set prompt-cont \"\"" };
        lines.AddRange(bootText.Replace("\r\n", "\n").Split('\n'));
        lines.Add(":set prompt \"\"");
        lines.Add(":set prompt-cont \"\"");
        lines.Add($"Prelude.putStrLn \"{ReadyMarker}\"");
        WriteLines(lines);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        var ready = _ready.Task;
        var finished = await Task.WhenAny(ready, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
        if (finished != ready || !ready.Result)
        {
            if (State == GhciState.Starting)
            {
                SetState(GhciState.Failed);
                Emit(OutputKind.Error, "Tidal did not become ready in time. Check the output above for errors.");
            }
            throw new TimeoutException("ghci/Tidal did not become ready");
        }
        SetState(GhciState.Ready);
        Emit(OutputKind.Info, "Tidal is ready.");
    }

    /// <summary>Evaluates code. Waits for earlier evaluations to finish first.</summary>
    public async Task<EvalResult> EvalAsync(string code, TimeSpan? timeout = null)
    {
        code = code.Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');
        if (code.Length == 0) return new EvalResult(true, Array.Empty<EvalError>(), "");

        await _evalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is null || State != GhciState.Ready) return EvalResult.NotRunning;

            var codeLines = code.Split('\n');
            bool multi = codeLines.Length > 1;
            var pending = new PendingEval
            {
                Id = Interlocked.Increment(ref _evalCounter),
                FirstCodeLine = _linesSent + 1 + (multi ? 1 : 0),
                LineCount = codeLines.Length,
            };
            _pending = pending;

            var toSend = new List<string>();
            if (multi) toSend.Add(":{");
            toSend.AddRange(codeLines);
            if (multi) toSend.Add(":}");
            toSend.Add($"Prelude.putStrLn \"{DoneMarkerPrefix}{pending.Id}\"");
            WriteLines(toSend);

            var t = pending.Completion.Task;
            var done = await Task.WhenAny(t, Task.Delay(timeout ?? TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            if (done != t)
            {
                _pending = null;
                return new EvalResult(false, new[] { new EvalError(-1, 0, "Timed out waiting for ghci") }, "Timed out waiting for ghci");
            }
            return t.Result;
        }
        finally
        {
            _evalLock.Release();
        }
    }

    private void WriteLines(IEnumerable<string> lines)
    {
        var process = _process;
        if (process is null) return;
        lock (_writeLock)
        {
            try
            {
                foreach (var line in lines)
                {
                    process.StandardInput.Write(line);
                    process.StandardInput.Write('\n');
                    _linesSent++;
                }
                process.StandardInput.Flush();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Emit(OutputKind.Error, $"Could not write to ghci: {e.Message}");
            }
        }
    }

    private async Task ReadLoop(Process process, StreamReader reader, bool isError)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                HandleLine(line, isError);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // process went away
        }
    }

    private void HandleLine(string line, bool isError)
    {
        // Prompts ("ghci> ", "ghci| ", "tidal> ") are printed without a newline and end up in front of output.
        line = StripPrompts(line);

        if (!isError)
        {
            if (line.EndsWith(ReadyMarker, StringComparison.Ordinal))
            {
                _ready?.TrySetResult(true);
                return;
            }
            int done = line.IndexOf(DoneMarkerPrefix, StringComparison.Ordinal);
            if (done >= 0)
            {
                if (int.TryParse(line.AsSpan(done + DoneMarkerPrefix.Length), out int id)) _ = CompleteAsync(id);
                string before = line[..done];
                if (before.Length > 0) Emit(OutputKind.Output, before);
                return;
            }
            if (line.Length == 0) return;
        }

        if (isError) _pending?.ErrorLines.Add(line);
        Emit(isError ? OutputKind.Error : OutputKind.Output, line);
    }

    private async Task CompleteAsync(int id)
    {
        // stderr is read on another thread; give it a moment to catch up before collecting errors.
        await Task.Delay(40).ConfigureAwait(false);
        var pending = _pending;
        if (pending is null || pending.Id != id) return;
        _pending = null;
        var errors = ParseErrors(pending.ErrorLines, pending.FirstCodeLine, pending.LineCount);
        string text = string.Join('\n', pending.ErrorLines);
        bool success = errors.Count == 0 && !pending.ErrorLines.Any(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("*** Exception", StringComparison.Ordinal));
        pending.Completion.TrySetResult(new EvalResult(success, errors, text));
    }

    [GeneratedRegex(@"^(?:(?:ghci|tidal|Prelude|\*?[A-Z][\w.]*)[>|] ?)+")]
    private static partial Regex PromptPrefix();

    public static string StripPrompts(string line) => PromptPrefix().Replace(line, "");

    [GeneratedRegex(@"^<interactive>:(?:(\d+):(\d+)(?:-\d+)?|\((\d+),(\d+)\)-\(\d+,\d+\)):\s*(error|warning)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLocation();

    /// <summary>Groups ghci's stderr into errors with their line within the evaluated block.</summary>
    public static List<EvalError> ParseErrors(IReadOnlyList<string> lines, int firstCodeLine, int lineCount)
    {
        var result = new List<EvalError>();
        EvalError? current = null;
        var message = new StringBuilder();

        void Flush()
        {
            if (current is not null) result.Add(current with { Message = message.ToString().TrimEnd() });
            current = null;
            message.Clear();
        }

        foreach (var line in lines)
        {
            var m = ErrorLocation().Match(line);
            if (m.Success)
            {
                Flush();
                if (m.Groups[5].Value == "warning") continue;
                int ghciLine = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value);
                int col = int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value);
                int blockLine = ghciLine - firstCodeLine;
                if (blockLine < 0 || blockLine >= lineCount) blockLine = -1;
                current = new EvalError(blockLine, col, "");
                message.AppendLine(line);
            }
            else if (current is not null)
            {
                message.AppendLine(line);
            }
            else if (line.Contains("*** Exception", StringComparison.Ordinal) || line.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                current = new EvalError(-1, 0, "");
                message.AppendLine(line);
            }
        }
        Flush();
        return result;
    }

    private void OnExited(Process process)
    {
        if (!ReferenceEquals(process, _process)) return;
        int code = -1;
        try { code = process.ExitCode; } catch (InvalidOperationException) { }
        _ready?.TrySetResult(false);
        _pending?.Completion.TrySetResult(EvalResult.NotRunning);
        _pending = null;
        _process = null;
        SetState(GhciState.Exited);
        Emit(OutputKind.Info, $"ghci exited (code {code}).");
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null) return;
        _process = null;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    lock (_writeLock)
                    {
                        process.StandardInput.WriteLine("hush");
                        process.StandardInput.WriteLine(":quit");
                        process.StandardInput.Flush();
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException) { }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
            _ready?.TrySetResult(false);
            _pending?.Completion.TrySetResult(EvalResult.NotRunning);
            _pending = null;
            SetState(GhciState.Stopped);
        }
    }

    private void SetState(GhciState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Emit(OutputKind kind, string text) => Output?.Invoke(new OutputLine(kind, text));

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
