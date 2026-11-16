using System.Text.RegularExpressions;
using TidalStudio.Core.Events;
using TidalStudio.Core.Tidal;

namespace TidalStudio.Core.Vj;

/// <summary>One evaluated block of code.</summary>
public sealed class CodeBlock
{
    public required int Seq { get; init; }
    /// <summary>When it was run, in TimeBase seconds.</summary>
    public required double Time { get; init; }
    public required string Text { get; init; }
    public required bool Success { get; init; }
    /// <summary>The wrapped string literals (offsets into <see cref="Text"/>), for highlighting what sounds.</summary>
    public IReadOnlyList<LiteralRef> Literals { get; init; } = Array.Empty<LiteralRef>();
    /// <summary>Pattern ids it sets ("1" for d1, or a name for p "name").</summary>
    public IReadOnlyList<string> PatternIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// What the performer has run, for the VJ screen: the "program" (the latest block for every pattern that is
/// playing), plus the last error and the last block that sets no pattern (setcps, let, â€¦).
/// </summary>
public sealed partial class CodeLog
{
    public const int MaxBlocks = 6;

    private readonly object _lock = new();
    private readonly List<CodeBlock> _program = new();
    private int _seq;

    public CodeBlock? LastError { get; private set; }
    public CodeBlock? LastOther { get; private set; }
    /// <summary>When hush was last run (TimeBase seconds), or -inf.</summary>
    public double LastHush { get; private set; } = double.NegativeInfinity;
    public long Version { get; private set; }
    /// <summary>The block being typed in the editor (not run yet), and when it last changed.</summary>
    public string? Draft { get; private set; }
    public double DraftTime { get; private set; } = double.NegativeInfinity;

    public void SetDraft(string? text, double time)
    {
        lock (_lock)
        {
            Draft = text?.Replace("\r\n", "\n").TrimEnd();
            DraftTime = time;
        }
    }

    [GeneratedRegex(@"\bd(\d{1,2})\s*\$")]
    private static partial Regex NumberedPattern();
    [GeneratedRegex(@"\bp\s+(?:""([^""]+)""|(\d+))")]
    private static partial Regex NamedPattern();
    [GeneratedRegex(@"\bd(\d{1,2})\s*\$\s*silence\b")]
    private static partial Regex SilencedPattern();
    [GeneratedRegex(@"^\s*hush\s*$")]
    private static partial Regex HushLine();

    public static IReadOnlyList<string> PatternIdsIn(string code)
    {
        var ids = new List<string>();
        foreach (var line in code.Split('\n'))
        {
            string l = line.TrimStart();
            if (l.StartsWith("--", StringComparison.Ordinal)) continue;
            foreach (Match m in NumberedPattern().Matches(line)) ids.Add(m.Groups[1].Value);
            foreach (Match m in NamedPattern().Matches(line)) ids.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        return ids.Distinct().ToList();
    }

    public CodeBlock Add(string text, bool success, IEnumerable<LiteralRef> literals, double time)
    {
        text = text.Replace("\r\n", "\n").TrimEnd();
        lock (_lock)
        {
            var block = new CodeBlock
            {
                Seq = ++_seq, Time = time, Text = text, Success = success,
                Literals = literals.ToList(), PatternIds = PatternIdsIn(text),
            };
            Version++;
            if (Draft == text) Draft = null; // it has been run
            if (!success)
            {
                LastError = block;
                return block;
            }
            if (HushLine().IsMatch(text))
            {
                ClearProgram(time);
                return block;
            }
            if (block.PatternIds.Count == 0)
            {
                LastOther = block;
                return block;
            }
            _program.RemoveAll(b => b.PatternIds.Any(block.PatternIds.Contains));
            var silenced = SilencedPattern().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            if (!block.PatternIds.All(silenced.Contains)) _program.Add(block);
            while (_program.Count > MaxBlocks) _program.RemoveAt(0);
            return block;
        }
    }

    public void Hush(double time)
    {
        lock (_lock)
        {
            ClearProgram(time);
            Version++;
        }
    }

    private void ClearProgram(double time)
    {
        _program.Clear();
        LastHush = time;
    }

    public IReadOnlyList<CodeBlock> Program
    {
        get { lock (_lock) return _program.ToList(); }
    }
}

/// <summary>A run of characters in a code line that is sounding right now.</summary>
public readonly record struct CodeHighlight(int Start, int Length, double Strength, string PatternId);

/// <summary>A line of code to draw on the VJ screen.</summary>
public sealed record CodeLine(
    string Text,
    string? PatternId,
    IReadOnlyList<CodeHighlight> Highlights,
    double Age,
    int CharOffset,
    bool IsError,
    bool IsTransient,
    bool IsDraft = false);

/// <summary>Turns the code log plus the sounding events into lines with highlights.</summary>
public static class CodeView
{
    /// <summary>How long an error and a setcps-style block stay on screen, in seconds.</summary>
    public const double ErrorSeconds = 4, TransientSeconds = 5;
    /// <summary>A draft disappears this long after the last keystroke.</summary>
    public const double DraftSeconds = 10;
    public const int DraftLines = 6;

    public static List<CodeLine> Layout(CodeLog log, IEnumerable<TidalEvent> active, double now, int maxLines = 40)
    {
        var program = log.Program;
        var blocks = new List<(CodeBlock Block, bool Error, bool Transient)>();
        foreach (var b in program) blocks.Add((b, false, false));
        if (log.LastOther is { } other && now - other.Time < TransientSeconds && other.Time > log.LastHush) blocks.Add((other, false, true));
        if (log.LastError is { } error && now - error.Time < ErrorSeconds) blocks.Add((error, true, false));

        // Literal id -> (block, quote offset, content length).
        var literals = new Dictionary<int, (CodeBlock Block, int Quote, int Length)>();
        foreach (var (b, _, _) in blocks)
            foreach (var l in b.Literals) literals[l.Id] = (b, l.Offset, l.ContentLength);

        // Absolute highlight ranges per block.
        var ranges = new Dictionary<int, List<(int Start, int End, double Strength, string Pattern)>>();
        foreach (var e in active)
        {
            double strength = Math.Clamp(1 - (now - e.Time) / Math.Max(e.Duration, 0.12), 0, 1);
            if (strength <= 0) continue;
            foreach (var span in e.Spans)
            {
                if (!literals.TryGetValue(span.LiteralId, out var lit)) continue;
                int start = lit.Quote + 1 + Math.Max(0, span.Start);
                int end = lit.Quote + 1 + Math.Min(span.End, lit.Length);
                if (end <= start) continue;
                if (!ranges.TryGetValue(lit.Block.Seq, out var list)) ranges[lit.Block.Seq] = list = new();
                list.Add((start, end, strength, e.PatternId));
            }
        }

        var lines = new List<CodeLine>();
        foreach (var (block, isError, isTransient) in blocks)
        {
            ranges.TryGetValue(block.Seq, out var blockRanges);
            string? blockPattern = block.PatternIds.FirstOrDefault();
            int offset = 0, visible = 0;
            foreach (var raw in block.Text.Split('\n'))
            {
                int lineStart = offset;
                offset += raw.Length + 1;
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
                var highlights = new List<CodeHighlight>();
                if (blockRanges is not null)
                    foreach (var (s, e, strength, pattern) in blockRanges)
                    {
                        int a = Math.Max(s, lineStart), z = Math.Min(e, lineStart + raw.Length);
                        if (z > a) highlights.Add(new CodeHighlight(a - lineStart, z - a, strength, pattern));
                    }
                // A line without its own dN takes the block's pattern only when the block has just one (not "do").
                string? linePattern = PatternIdsIn(raw) ?? (block.PatternIds.Count == 1 ? blockPattern : null);
                lines.Add(new CodeLine(raw.TrimEnd(), linePattern, highlights, now - block.Time, visible, isError, isTransient));
                visible += raw.Length + 1;
            }
        }
        // What is being typed, below the program (unless it is the same as a block that is playing).
        if (log.Draft is { Length: > 0 } draft && now - log.DraftTime < DraftSeconds && blocks.All(b => b.Block.Text != draft))
        {
            var draftLines = draft.Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList();
            foreach (var l in draftLines.TakeLast(DraftLines))
                lines.Add(new CodeLine(l.TrimEnd(), PatternIdsIn(l), Array.Empty<CodeHighlight>(), now - log.DraftTime, 0, false, false, IsDraft: true));
        }
        // Keep the newest lines when there are too many.
        if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
        return lines;
    }

    private static string? PatternIdsIn(string line)
    {
        var ids = CodeLog.PatternIdsIn(line);
        return ids.Count > 0 ? ids[0] : null;
    }
}
