using System.Text.RegularExpressions;
using TidalStudio.Core.Completion;

namespace TidalStudio.Core.SuperCollider;

/// <summary>Reads and (on request) edits SuperCollider's startup.scd so SuperDirt loads a samples folder.</summary>
public static partial class StartupScript
{
    /// <summary>True when the script loads <paramref name="soundFolder"/> (a folder of sound files) at startup.</summary>
    public static bool LoadsFolder(string script, string soundFolder)
    {
        string target = Normalize(soundFolder);
        return SampleLibrary.SoundFoldersFromScript(script).Any(f => Normalize(f) == target);
    }

    /// <summary>The SuperCollider line that loads every sound folder inside <paramref name="samplesRoot"/>.</summary>
    public static string LoadLine(string samplesRoot) =>
        $"~dirt.loadSoundFiles(\"{samplesRoot.Replace('\\', '/').TrimEnd('/')}/*\");";

    [GeneratedRegex(@"^(\s*)//\s*(~dirt\.loadSoundFiles\(""([^""]+)""\);?.*)$")]
    private static partial Regex CommentedLoad();

    [GeneratedRegex(@"^(\s*)~dirt\.loadSoundFiles\b")]
    private static partial Regex ActiveLoad();

    /// <summary>
    /// Makes the script load every sound in <paramref name="samplesRoot"/>: uncomments an existing commented-out
    /// <c>// ~dirt.loadSoundFiles("&lt;root&gt;/*")</c> line, or adds one after the last active loadSoundFiles line.
    /// Returns null when there is nowhere sensible to put it (the script doesn't load samples itself).
    /// </summary>
    public static string? AddSamplesFolder(string script, string samplesRoot)
    {
        string newline = script.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        string wanted = Normalize(samplesRoot.TrimEnd('/', '\\') + "/*");

        for (int i = 0; i < lines.Count; i++)
        {
            var m = CommentedLoad().Match(lines[i]);
            if (m.Success && Normalize(m.Groups[3].Value) == wanted)
            {
                lines[i] = m.Groups[1].Value + m.Groups[2].Value;
                return string.Join(newline, lines);
            }
        }

        int last = -1;
        string indent = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var m = ActiveLoad().Match(lines[i]);
            if (m.Success) { last = i; indent = m.Groups[1].Value; }
        }
        if (last < 0) return null;
        lines.Insert(last + 1, indent + LoadLine(samplesRoot) + "  // added by TidalStudio (your own samples)");
        return string.Join(newline, lines);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
