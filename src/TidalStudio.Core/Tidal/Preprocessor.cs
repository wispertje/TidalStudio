using System.Text;

namespace TidalStudio.Core.Tidal;

/// <summary>A string literal found in evaluated code.</summary>
/// <param name="Id">Id passed to tsctx; Tidal reports it back as line (Id + 1).</param>
/// <param name="Offset">Offset of the opening quote within the evaluated block.</param>
/// <param name="ContentLength">Number of characters between the quotes.</param>
public readonly record struct LiteralRef(int Id, int Offset, int ContentLength);

public sealed record PreprocessResult(string Code, IReadOnlyList<LiteralRef> Literals);

/// <summary>
/// Wraps Haskell string literals as <c>(tsctx ID "...")</c> so that Tidal tags each event with the
/// position of the mini-notation that produced it. Comments, character literals, ghci commands and imports
/// are left alone. Strings with escapes are not wrapped (their positions would not line up).
/// </summary>
public static class Preprocessor
{
    private const string OperatorChars = "!#$%&*+./<=>?@\\^|-~:";

    public static PreprocessResult Wrap(string code, Func<int> nextId)
    {
        var sb = new StringBuilder(code.Length + 64);
        var literals = new List<LiteralRef>();
        int i = 0;
        int n = code.Length;
        bool lineIsCommand = IsCommandLine(code, 0);

        while (i < n)
        {
            char c = code[i];

            if (c == '\n')
            {
                sb.Append(c);
                i++;
                lineIsCommand = IsCommandLine(code, i);
                continue;
            }

            // Line comment: "--" (or more dashes) not followed by another operator character.
            if (c == '-' && i + 1 < n && code[i + 1] == '-')
            {
                int j = i;
                while (j < n && code[j] == '-') j++;
                if (j >= n || OperatorChars.IndexOf(code[j]) < 0)
                {
                    int eol = code.IndexOf('\n', i);
                    if (eol < 0) eol = n;
                    sb.Append(code, i, eol - i);
                    i = eol;
                    continue;
                }
                sb.Append(code, i, j - i);
                i = j;
                continue;
            }

            // Block comment {- ... -}, nested.
            if (c == '{' && i + 1 < n && code[i + 1] == '-')
            {
                int depth = 0;
                int j = i;
                while (j < n)
                {
                    if (code[j] == '{' && j + 1 < n && code[j + 1] == '-') { depth++; j += 2; continue; }
                    if (code[j] == '-' && j + 1 < n && code[j + 1] == '}') { depth--; j += 2; if (depth == 0) break; continue; }
                    j++;
                }
                sb.Append(code, i, j - i);
                i = j;
                continue;
            }

            // Character literal (but not a prime in an identifier like d1' or note').
            if (c == '\'' && (i == 0 || !IsIdentChar(code[i - 1])))
            {
                int end = CharLiteralEnd(code, i);
                if (end > i)
                {
                    sb.Append(code, i, end - i);
                    i = end;
                    continue;
                }
            }

            if (c == '"')
            {
                int close = StringEnd(code, i, out bool hasEscape);
                if (close < 0)
                {
                    // Unterminated on this line: copy the rest verbatim.
                    sb.Append(code, i, n - i);
                    break;
                }
                int contentLength = close - i - 1;
                if (!lineIsCommand && !hasEscape && contentLength > 0)
                {
                    int id = nextId();
                    literals.Add(new LiteralRef(id, i, contentLength));
                    sb.Append("(tsctx ").Append(id).Append(' ');
                    sb.Append(code, i, close - i + 1);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(code, i, close - i + 1);
                }
                i = close + 1;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return new PreprocessResult(sb.ToString(), literals);
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'';

    private static bool IsCommandLine(string code, int lineStart)
    {
        int i = lineStart;
        while (i < code.Length && (code[i] == ' ' || code[i] == '\t')) i++;
        if (i >= code.Length) return false;
        if (code[i] == ':') return true;
        return string.CompareOrdinal(code, i, "import ", 0, 7) == 0;
    }

    /// <summary>Returns the offset just past a character literal starting at <paramref name="start"/>, or -1.</summary>
    private static int CharLiteralEnd(string code, int start)
    {
        int n = code.Length;
        if (start + 2 < n && code[start + 1] != '\\' && code[start + 2] == '\'') return start + 3;
        if (start + 1 < n && code[start + 1] == '\\')
        {
            int j = start + 2;
            while (j < n && j < start + 12 && code[j] != '\'' && code[j] != '\n') j++;
            if (j < n && code[j] == '\'') return j + 1;
        }
        return -1;
    }

    /// <summary>Returns the index of the closing quote of the string starting at <paramref name="start"/>, or -1.</summary>
    private static int StringEnd(string code, int start, out bool hasEscape)
    {
        hasEscape = false;
        for (int j = start + 1; j < code.Length; j++)
        {
            char c = code[j];
            if (c == '\\') { hasEscape = true; j++; continue; }
            if (c == '"') return j;
            if (c == '\n') return -1;
        }
        return -1;
    }
}
