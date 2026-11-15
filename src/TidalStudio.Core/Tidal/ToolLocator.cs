namespace TidalStudio.Core.Tidal;

/// <summary>Finds ghci and sclang. Reads PATH from the registry so a PATH changed after login is still seen.</summary>
public static class ToolLocator
{
    public static string? FindGhci(string? configured = null)
    {
        var fromConfig = ResolveConfigured(configured, "ghci");
        if (fromConfig is not null) return fromConfig;

        var onPath = FindOnPath("ghci");
        if (onPath is not null) return onPath;

        var candidates = new List<string>();
        foreach (var root in new[] { @"C:\tools", @"C:\ghcup\ghc", Path.Combine(Home, ".ghcup", "ghc") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("ghc", StringComparison.OrdinalIgnoreCase) && !char.IsDigit(name.FirstOrDefault())) continue;
                candidates.Add(Path.Combine(dir, "bin", Exe("ghci")));
            }
        }
        candidates.Add(Path.Combine(@"C:\ghcup\bin", Exe("ghci")));
        candidates.Add(Path.Combine(Home, ".ghcup", "bin", Exe("ghci")));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ghcup", "bin", Exe("ghci")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindSclang(string? configured = null)
    {
        var fromConfig = ResolveConfigured(configured, "sclang");
        if (fromConfig is not null) return fromConfig;

        var onPath = FindOnPath("sclang");
        if (onPath is not null) return onPath;

        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(pf) || !Directory.Exists(pf)) continue;
                foreach (var dir in Directory.GetDirectories(pf, "SuperCollider*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var exe = Path.Combine(dir, "sclang.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            const string mac = "/Applications/SuperCollider.app/Contents/MacOS/sclang";
            if (File.Exists(mac)) return mac;
        }
        return null;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? ResolveConfigured(string? configured, string tool)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        configured = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
        if (File.Exists(configured)) return configured;
        if (Directory.Exists(configured))
        {
            var inDir = Path.Combine(configured, Exe(tool));
            if (File.Exists(inDir)) return inDir;
        }
        return FindOnPath(configured);
    }

    public static string? FindOnPath(string tool)
    {
        foreach (var dir in PathDirectories())
        {
            try
            {
                var full = Path.Combine(dir, Exe(tool));
                if (File.Exists(full)) return full;
                if (!OperatingSystem.IsWindows()) continue;
                var bare = Path.Combine(dir, tool);
                if (Path.HasExtension(tool) && File.Exists(bare)) return bare;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>The process PATH plus (on Windows) the current machine and user PATH from the registry.</summary>
    public static IEnumerable<string> PathDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        }
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;
            foreach (var raw in source.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (dir.Length > 0 && seen.Add(dir)) yield return dir;
            }
        }
    }

    /// <summary>A PATH value combining process, machine and user PATH, for child processes.</summary>
    public static string FreshPath() => string.Join(Path.PathSeparator, PathDirectories());
}
