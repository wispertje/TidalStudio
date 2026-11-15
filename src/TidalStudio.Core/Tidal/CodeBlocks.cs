namespace TidalStudio.Core.Tidal;

/// <summary>A range of whole lines in a document.</summary>
/// <param name="StartLine">0-based first line.</param>
/// <param name="EndLine">0-based last line (inclusive).</param>
/// <param name="StartOffset">Character offset of the first line's start.</param>
/// <param name="Text">The text of the lines, joined with '\n'.</param>
public sealed record CodeRange(int StartLine, int EndLine, int StartOffset, string Text)
{
    public int Length => Text.Length;
    public int EndOffset => StartOffset + Text.Length;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Finds the line or block (paragraph between blank lines) to run, like the VS Code Tidal extension.</summary>
public static class CodeBlocks
{
    /// <summary>The single line at <paramref name="caretLine"/> (Shift+Enter).</summary>
    public static CodeRange? GetLine(string text, int caretLine)
    {
        var lines = SplitLines(text);
        if (caretLine < 0 || caretLine >= lines.Count) return null;
        var range = Make(text, lines, caretLine, caretLine);
        return range.IsEmpty ? null : range;
    }

    /// <summary>The block around <paramref name="caretLine"/>, delimited by blank lines (Ctrl+Enter).</summary>
    public static CodeRange? GetBlock(string text, int caretLine)
    {
        var lines = SplitLines(text);
        if (caretLine < 0 || caretLine >= lines.Count) return null;
        if (IsBlank(text, lines[caretLine])) return null;

        int start = caretLine;
        while (start > 0 && !IsBlank(text, lines[start - 1])) start--;
        int end = caretLine;
        while (end < lines.Count - 1 && !IsBlank(text, lines[end + 1])) end++;
        return Make(text, lines, start, end);
    }

    /// <summary>A selection, expanded to whole lines.</summary>
    public static CodeRange? GetSelection(string text, int selectionStart, int selectionEnd)
    {
        if (selectionEnd <= selectionStart) return null;
        var lines = SplitLines(text);
        int first = LineAt(lines, selectionStart);
        int last = LineAt(lines, Math.Max(selectionStart, selectionEnd - 1));
        var range = Make(text, lines, first, last);
        return range.IsEmpty ? null : range;
    }

    public static int LineAt(string text, int offset) => LineAt(SplitLines(text), offset);

    private static int LineAt(List<(int Start, int Length)> lines, int offset)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
            if (offset >= lines[i].Start) return i;
        return 0;
    }

    private static CodeRange Make(string text, List<(int Start, int Length)> lines, int first, int last)
    {
        int start = lines[first].Start;
        int end = lines[last].Start + lines[last].Length;
        string block = text.Substring(start, end - start).Replace("\r\n", "\n").Replace('\r', '\n');
        return new CodeRange(first, last, start, block);
    }

    private static bool IsBlank(string text, (int Start, int Length) line)
    {
        for (int i = line.Start; i < line.Start + line.Length; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    /// <summary>Line start offsets and lengths (without the line terminator).</summary>
    internal static List<(int Start, int Length)> SplitLines(string text)
    {
        var result = new List<(int, int)>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || text[i] == '\r')
            {
                result.Add((start, i - start));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        result.Add((start, text.Length - start));
        return result;
    }
}
