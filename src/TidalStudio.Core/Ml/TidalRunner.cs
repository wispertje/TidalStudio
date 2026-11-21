using System.Globalization;
using TidalStudio.Core.Tidal;

namespace TidalStudio.Core.Ml;

/// <summary>
/// A second, silent Tidal (its own ghci) for working out what code plays without making a sound: it sends to
/// unused ports and doesn't listen for remote control. Used to import .tidal files and to check written code.
/// </summary>
public sealed class TidalRunner : IAsyncDisposable
{
    private readonly GhciHost _ghci = new();

    public event Action<OutputLine>? Output
    {
        add => _ghci.Output += value;
        remove => _ghci.Output -= value;
    }

    public bool IsReady => _ghci.IsReady;

    /// <summary>TidalStudio's boot file, with the sound and editor ports moved to nowhere.</summary>
    public static string SilentBoot() => BootFiles.BuiltIn
        .Replace("oPort = 57120", "oPort = 57197")
        .Replace("oPort = 57130", "oPort = 57198")
        .Replace("cVerbose = True", "cVerbose = False, cCtrlListen = False");

    public async Task StartAsync(string ghciPath, string? workingDirectory = null, CancellationToken token = default)
    {
        await _ghci.StartAsync(ghciPath, SilentBoot(), workingDirectory, TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
        foreach (var def in TidalContext.HelperDefinitions)
        {
            var r = await _ghci.EvalAsync(def).ConfigureAwait(false);
            if (!r.Success) throw new InvalidOperationException("tsDump helper: " + r.ErrorText);
        }
    }

    /// <summary>Defines tsDump / tsDumpNow in a ghci session (the user's live one or this runner's).</summary>
    public static async Task<bool> DefineHelpersAsync(GhciHost ghci)
    {
        bool ok = true;
        foreach (var def in TidalContext.HelperDefinitions)
        {
            var r = await ghci.EvalQuietAsync(def).ConfigureAwait(false);
            ok &= r;
        }
        return ok;
    }

    public Task<EvalResult> EvalAsync(string code) => _ghci.EvalAsync(code, TimeSpan.FromSeconds(30));

    /// <summary>The events of the playing patterns in cycles [from, to).</summary>
    public async Task<List<DumpEvent>> DumpAsync(double from, double to)
    {
        var lines = await _ghci.QueryAsync(
            $"tsDump {from.ToString(CultureInfo.InvariantCulture)} {to.ToString(CultureInfo.InvariantCulture)}",
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return TidalContext.Parse(lines, out _);
    }

    public Task<IReadOnlyList<string>> QueryAsync(string command) => _ghci.QueryAsync(command, TimeSpan.FromSeconds(60));

    public async Task<double> CpsAsync()
    {
        var lines = await _ghci.QueryAsync("getcps >>= print").ConfigureAwait(false);
        foreach (var l in lines)
            if (double.TryParse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cps) && cps > 0) return cps;
        return 0.5625;
    }

    public async Task HushAsync() => await _ghci.EvalAsync("hush").ConfigureAwait(false);

    /// <summary>
    /// Runs a .tidal file block by block and takes snapshots of what plays: after every block that changes the
    /// patterns (16 cycles), and once at the end (<paramref name="finalCycles"/>). Returns songs for training.
    /// </summary>
    public async Task<List<MSong>> ImportFileAsync(string path, SampleCatalog catalog, int finalCycles = 128, Action<string>? log = null)
    {
        string text = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var songs = new List<MSong>();
        await HushAsync().ConfigureAwait(false);
        await _ghci.EvalAsync("resetCycles").ConfigureAwait(false);
        int line = 0, failed = 0, ran = 0;
        string name = Path.GetFileNameWithoutExtension(path);
        while (line < lines.Length)
        {
            var block = CodeBlocks.GetBlock(text, line);
            if (block is null) { line++; continue; }
            line = block.EndLine + 1;
            string code = block.Text.Trim();
            if (code.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0)) continue;
            var result = await EvalAsync(code).ConfigureAwait(false);
            ran++;
            if (!result.Success) { failed++; continue; }
            if (CodeLogPatternIds(code) == 0) continue;
            var song = await SnapshotAsync(catalog, $"{name}:{block.StartLine + 1}", 16).ConfigureAwait(false);
            if (song is not null) songs.Add(song);
        }
        var final = await SnapshotAsync(catalog, $"{name}:end", finalCycles).ConfigureAwait(false);
        if (final is not null) songs.Add(final);
        await HushAsync().ConfigureAwait(false);
        log?.Invoke($"{name}: {ran} blocks run ({failed} failed), {songs.Count} snapshots");
        return songs;
    }

    private static int CodeLogPatternIds(string code) => Vj.CodeLog.PatternIdsIn(code).Count;

    private async Task<MSong?> SnapshotAsync(SampleCatalog catalog, string source, int cycles)
    {
        var events = await DumpAsync(0, cycles).ConfigureAwait(false);
        if (events.Count == 0) return null;
        // Find how long the music really is (an arrangement may end early, or loop).
        double last = events.Max(e => e.Start);
        int bars = Math.Max(1, (int)Math.Ceiling(last + 1e-6));
        var ctx = TidalContext.Build(events, catalog, 0, bars);
        if (ctx.Parts.Count == 0) return null;
        double bpm = Math.Round(await CpsAsync().ConfigureAwait(false) * 240);
        return new MSong { Source = source, Bpm = bpm, Bars = bars, Parts = ctx.Parts.Select(p => p.Part).ToList() };
    }

    public ValueTask DisposeAsync() => _ghci.DisposeAsync();
}
