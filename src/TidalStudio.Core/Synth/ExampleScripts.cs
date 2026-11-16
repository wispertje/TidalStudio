using System.Reflection;

namespace TidalStudio.Core.Synth;

/// <summary>The example scripts in the repository's examples folder, built into the app (Synth ▸ Example scripts).</summary>
public static class ExampleScripts
{
    public static readonly IReadOnlyList<(string Title, string FileName)> All = new[]
    {
        ("Tidepool synth samples", "01-tidepool-synth.tidal"),
        ("Tidepool Drums kits", "02-tidepool-drums.tidal"),
        ("Tidepool Acid loops", "03-tidepool-acid.tidal"),
        ("Synth pop sketch (all three)", "04-synth-pop-song.tidal"),
        ("Tidepool FM samples", "05-tidepool-fm.tidal"),
    };

    /// <summary>The script's text (empty if it is missing from the build).</summary>
    public static string Read(string fileName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("examples." + fileName);
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
