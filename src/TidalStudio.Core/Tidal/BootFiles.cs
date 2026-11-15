using System.Reflection;

namespace TidalStudio.Core.Tidal;

public sealed record BootFileChoice(string Text, string Description, bool SupportsVisuals, string? Path);

/// <summary>Chooses the BootTidal.hs to use and tells whether it has TidalStudio's additions.</summary>
public static class BootFiles
{
    /// <summary>Marker that identifies a boot file with TidalStudio's highlighting support.</summary>
    public const string VisualsMarker = "tsctx";

    public const string BootFileName = "BootTidal.hs";

    public static string BuiltIn
    {
        get
        {
            var asm = typeof(BootFiles).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith(BootFileName, StringComparison.Ordinal));
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Order: explicit path from settings; BootTidal.hs next to the open file (or a parent folder, like the
    /// VS Code extension); BootTidal.hs in the working directory; otherwise the built-in boot file.
    /// </summary>
    public static BootFileChoice Choose(string? configuredPath, string? documentPath, bool preferBuiltIn, string? workingDirectory = null)
    {
        if (preferBuiltIn) return FromBuiltIn();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (File.Exists(path)) return FromFile(path, "configured");
        }

        if (!string.IsNullOrEmpty(documentPath))
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(documentPath));
            for (int depth = 0; dir is not null && depth < 4; depth++)
            {
                var candidate = System.IO.Path.Combine(dir, BootFileName);
                if (File.Exists(candidate)) return FromFile(candidate, "next to the file");
                dir = System.IO.Path.GetDirectoryName(dir);
            }
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            var candidate = System.IO.Path.Combine(workingDirectory, BootFileName);
            if (File.Exists(candidate)) return FromFile(candidate, "in the working directory");
        }

        return FromBuiltIn();
    }

    public static BootFileChoice FromBuiltIn() => new(BuiltIn, "built-in BootTidal.hs", true, null);

    private static BootFileChoice FromFile(string path, string how)
    {
        string text = File.ReadAllText(path);
        return new BootFileChoice(text, $"{path} ({how})", text.Contains(VisualsMarker, StringComparison.Ordinal), path);
    }
}
