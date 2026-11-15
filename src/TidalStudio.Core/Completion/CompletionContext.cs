using System.Text.RegularExpressions;

namespace TidalStudio.Core.Completion;

public enum CompletionKind
{
    None,
    /// <summary>Tidal functions, effects and words from the document.</summary>
    Code,
    /// <summary>Sample / synth names inside <c>s "…"</c> or <c>sound "…"</c>.</summary>
    Sample,
    /// <summary>An inline visual name after <c>-- _</c>.</summary>
    VisualName,
    /// <summary>An option key after <c>-- _pianoroll </c>.</summary>
    VisualOptionKey,
    /// <summary>An option value after <c>-- _pianoroll key=</c>.</summary>
    VisualOptionValue,
}

/// <param name="Start">Column where the text being completed starts (it is replaced by the completion).</param>
/// <param name="Prefix">What has been typed of it so far.</param>
public sealed record CompletionRequest(
    CompletionKind Kind,
    int Start,
    string Prefix,
    string? VisualType = null,
    string? OptionKey = null,
    IReadOnlySet<string>? UsedKeys = null,
    bool AfterHash = false)
{
    public static CompletionRequest None { get; } = new(CompletionKind.None, 0, "");
}

/// <summary>Works out what kind of completion fits the text in front of the caret on one line.</summary>
public static partial class CompletionContext
{
    [GeneratedRegex(@"^--\s*(_\w*)$")]
    private static partial Regex VisualName();

    [GeneratedRegex(@"^--\s*_(\w+)(?:\s+\S+)*?\s+(\w+)=([^\s=]*)$")]
    private static partial Regex OptionValue();

    [GeneratedRegex(@"^--\s*_(\w+)(?:\s+\S+)*\s+(\w*)$")]
    private static partial Regex OptionKey();

    [GeneratedRegex(@"(\w+)=")]
    private static partial Regex UsedKey();

    [GeneratedRegex(@"(?:^|[^\w'.])(s|sound)\s*$")]
    private static partial Regex SoundFunction();

    [GeneratedRegex(@"[A-Za-z_][\w']*$")]
    private static partial Regex Identifier();

    public static CompletionRequest Analyze(string line, int caret)
    {
        caret = Math.Clamp(caret, 0, line.Length);
        string before = line[..caret];

        // Find whether the caret is in a string or a comment.
        bool inString = false;
        int stringStart = -1, commentStart = -1;
        for (int i = 0; i < before.Length; i++)
        {
            char c = before[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"')
            {
                inString = true;
                stringStart = i;
            }
            else if (c == '-' && i + 1 < before.Length && before[i + 1] == '-')
            {
                commentStart = i;
                break;
            }
        }

        if (commentStart >= 0) return AnalyzeComment(before, commentStart);
        if (inString) return AnalyzeString(before, stringStart);

        var id = Identifier().Match(before);
        if (!id.Success)
            return new CompletionRequest(CompletionKind.Code, caret, "", AfterHash: PrecededByHash(before, caret));
        // Not part of a number (1e3) or a qualified name's module (Foo.bar is fine, completes "bar").
        if (id.Index > 0 && char.IsDigit(before[id.Index - 1])) return CompletionRequest.None;
        return new CompletionRequest(CompletionKind.Code, id.Index, id.Value, AfterHash: PrecededByHash(before, id.Index));
    }

    private static bool PrecededByHash(string before, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (before[i] == ' ' || before[i] == '\t') continue;
            return before[i] == '#';
        }
        return false;
    }

    private static CompletionRequest AnalyzeComment(string before, int commentStart)
    {
        string comment = before[commentStart..];

        var name = VisualName().Match(comment);
        if (name.Success)
            return new CompletionRequest(CompletionKind.VisualName, commentStart + name.Groups[1].Index, name.Groups[1].Value);

        var value = OptionValue().Match(comment);
        if (value.Success)
            return new CompletionRequest(CompletionKind.VisualOptionValue, commentStart + value.Groups[3].Index, value.Groups[3].Value,
                VisualType: value.Groups[1].Value, OptionKey: value.Groups[2].Value);

        var key = OptionKey().Match(comment);
        if (key.Success)
        {
            var used = UsedKey().Matches(comment).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            return new CompletionRequest(CompletionKind.VisualOptionKey, commentStart + key.Groups[2].Index, key.Groups[2].Value,
                VisualType: key.Groups[1].Value, UsedKeys: used);
        }
        return CompletionRequest.None;
    }

    private static CompletionRequest AnalyzeString(string before, int stringStart)
    {
        if (!SoundFunction().IsMatch(before[..stringStart])) return CompletionRequest.None;
        int start = before.Length;
        while (start > stringStart + 1 && IsSampleChar(before[start - 1])) start--;
        // After "bd:" comes a sample number, not a name.
        if (start > stringStart + 1 && before[start - 1] == ':') return CompletionRequest.None;
        return new CompletionRequest(CompletionKind.Sample, start, before[start..]);
    }

    private static bool IsSampleChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
}
