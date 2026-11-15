namespace TidalStudio.Core.Completion;

public sealed record SampleEntry(string Name, int Count, bool IsSynth);

/// <summary>Sample folder names (from SuperDirt's Dirt-Samples) and SuperDirt's built-in synths.</summary>
public static class SampleLibrary
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

    /// <summary>Folders where SuperDirt's sample quark is usually installed.</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(local, "SuperCollider", "downloaded-quarks", "Dirt-Samples");
        yield return Path.Combine(home, "Library", "Application Support", "SuperCollider", "downloaded-quarks", "Dirt-Samples");
        yield return Path.Combine(home, ".local", "share", "SuperCollider", "downloaded-quarks", "Dirt-Samples");
    }

    /// <summary>Every sample folder (with its number of sound files) plus the synths, sorted by name.</summary>
    public static List<SampleEntry> Scan(IEnumerable<string>? folders = null)
    {
        var result = new Dictionary<string, SampleEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in folders ?? DefaultFolders())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith('.') || name.Contains(' ')) continue;
                    int count = 0;
                    try
                    {
                        count = Directory.EnumerateFiles(dir).Count(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    if (count > 0) result.TryAdd(name, new SampleEntry(name, count, false));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var synth in Synths) result.TryAdd(synth, new SampleEntry(synth, 0, true));
        return result.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
