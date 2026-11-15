using System.Text.RegularExpressions;

namespace TidalStudio.Core.Completion;

public sealed record SampleEntry(string Name, int Count, bool IsSynth);

/// <summary>
/// Sample names SuperDirt knows: the Dirt-Samples quark, every folder loaded by
/// <c>~dirt.loadSoundFiles("…")</c> in the SuperCollider startup file, and SuperDirt's built-in synths.
/// </summary>
public static partial class SampleLibrary
{
    private static readonly string[] AudioExtensions = { ".wav", ".aif", ".aiff", ".flac", ".ogg", ".mp3" };

    /// <summary>Synths that come with SuperDirt.</summary>
    public static readonly IReadOnlyList<string> Synths = new[]
    {
        "superpiano", "supersaw", "supersquare", "superpwm", "supermandolin", "superhammond", "supergong",
        "superhoover", "superzow", "superchip", "superfm", "supervibe", "superkick", "superhat", "supersnare",
        "superclap", "super808", "supernoise", "superstatic", "supercomparator", "superfork", "superprimes",
        "superwavemechanic",
    };

    /// <summary>SuperCollider's user folder (where startup.scd and downloaded-quarks live) on each platform.</summary>
    public static IEnumerable<string> SuperColliderUserFolders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(local, "SuperCollider");
        yield return Path.Combine(home, "Library", "Application Support", "SuperCollider");
        yield return Path.Combine(home, ".local", "share", "SuperCollider");
    }

    /// <summary>Folders where SuperDirt's sample quark is usually installed (each subfolder is one sound).</summary>
    public static IEnumerable<string> DefaultFolders() =>
        SuperColliderUserFolders().Select(f => Path.Combine(f, "downloaded-quarks", "Dirt-Samples"));

    /// <summary>The SuperCollider startup file, if there is one.</summary>
    public static string? StartupScriptPath() =>
        SuperColliderUserFolders().Select(f => Path.Combine(f, "startup.scd")).FirstOrDefault(File.Exists);

    [GeneratedRegex(@"loadSoundFiles\s*\(\s*""([^""]+)""")]
    private static partial Regex LoadSoundFiles();

    /// <summary>
    /// The sound folders a startup script loads with <c>loadSoundFiles("path/*")</c>. Like SuperDirt, each folder
    /// the path pattern matches is one sound, named after the folder. Commented-out lines are ignored.
    /// </summary>
    public static List<string> SoundFoldersFromScript(string script)
    {
        var folders = new List<string>();
        foreach (var rawLine in script.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            foreach (Match m in LoadSoundFiles().Matches(line))
            {
                if (comment >= 0 && m.Index > comment) continue;
                folders.AddRange(ExpandFolderPattern(m.Groups[1].Value));
            }
        }
        return folders;
    }

    /// <summary>Expands a SuperCollider path pattern with * and ? (in any segment) to the folders it matches.</summary>
    public static IEnumerable<string> ExpandFolderPattern(string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        if (pattern.StartsWith("~/", StringComparison.Ordinal))
            pattern = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/') + pattern[1..];
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return Array.Empty<string>();

        // The first segment is a drive ("C:") or, on Unix, the root.
        var current = new List<string> { pattern.StartsWith('/') ? "/" + segments[0] : segments[0] + (segments[0].EndsWith(':') ? "/" : "") };
        foreach (var segment in segments.Skip(1))
        {
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    if (segment.Contains('*') || segment.Contains('?')) next.AddRange(Directory.EnumerateDirectories(dir, segment));
                    else next.Add(Path.Combine(dir, segment));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            current = next;
        }
        return current.Where(Directory.Exists).Select(Path.GetFullPath);
    }

    /// <summary>
    /// Every sound (with its number of sound files) plus the synths, sorted by name. <paramref name="roots"/> are
    /// folders whose subfolders are sounds; <paramref name="soundFolders"/> are sound folders themselves.
    /// By default: Dirt-Samples plus whatever the SuperCollider startup file loads.
    /// </summary>
    public static List<SampleEntry> Scan(IEnumerable<string>? roots = null, IEnumerable<string>? soundFolders = null)
    {
        var result = new Dictionary<string, SampleEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string dir)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            if (name.StartsWith('.') || name.Contains(' ')) return;
            int count = 0;
            try
            {
                count = Directory.EnumerateFiles(dir).Count(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (count > 0) result.TryAdd(name, new SampleEntry(name, count, false));
        }

        foreach (var root in roots ?? DefaultFolders())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root)) Add(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        if (soundFolders is null && roots is null && StartupScriptPath() is { } startup)
        {
            try { soundFolders = SoundFoldersFromScript(File.ReadAllText(startup)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var dir in soundFolders ?? Array.Empty<string>()) Add(dir);

        foreach (var synth in Synths) result.TryAdd(synth, new SampleEntry(synth, 0, true));
        return result.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
