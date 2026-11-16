using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidalStudio.Core.Settings;

public enum HighlightStyle { Background, Outline, Underline }

/// <summary>One visualizer tile in the side panel.</summary>
public sealed class VisualizerConfig
{
    public string Type { get; set; } = "";
    /// <summary>Also draw this visualizer behind the code (like Strudel's non-inline visuals).</summary>
    public bool BehindCode { get; set; }
    public double Height { get; set; } = 180;
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class AppSettings
{
    // Tidal / tools
    public string? GhciPath { get; set; }
    public string? BootFilePath { get; set; }
    public bool UseBuiltInBootFile { get; set; }
    public string? SclangPath { get; set; }
    public string? SuperDirtScript { get; set; }
    public int EditorOscPort { get; set; } = 57130;
    public bool StartTidalOnLaunch { get; set; } = true;
    /// <summary>Start SuperCollider + SuperDirt when they are not running (at launch and when code is run).</summary>
    public bool AutoStartSuperDirt { get; set; } = true;
    /// <summary>Where Tidepool saves sample folders (default: Music\samples).</summary>
    public string? SamplesFolder { get; set; }
    /// <summary>The synth patch that was open last.</summary>
    public string? SynthPatchJson { get; set; }
    /// <summary>After Tidepool saves, make SuperDirt load the sound (restarting a SuperCollider TidalStudio didn't start).</summary>
    public bool SynthRestartOnSave { get; set; } = true;
    public bool SynthShowSignalFlow { get; set; } = true;
    /// <summary>The drum kit that was open last in Tidepool Drums.</summary>
    public string? DrumKitJson { get; set; }
    /// <summary>Also save every drum kit as one folder (s "kit" # n 0..).</summary>
    public bool DrumSaveCombined { get; set; } = true;
    /// <summary>The bassline box that was open last in Tidepool Acid.</summary>
    public string? AcidBoxJson { get; set; }
    /// <summary>The FM patch that was open last in Tidepool FM.</summary>
    public string? FmPatchJson { get; set; }
    /// <summary>The VJ screen's scenes, effects and code display.</summary>
    public TidalStudio.Core.Vj.VjOptions Vj { get; set; } = new();

    // Editor
    public double FontSize { get; set; } = 15;
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, Menlo, monospace";
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool Autocomplete { get; set; } = true;

    // Mini-notation highlighting
    public bool HighlightMiniNotation { get; set; } = true;
    public HighlightStyle HighlightStyle { get; set; } = HighlightStyle.Background;
    /// <summary>Colour per pattern (d1..d16); index 0 is d1.</summary>
    public List<string> PatternColors { get; set; } = new(DefaultPalette);
    public double HighlightOpacity { get; set; } = 0.45;

    // Visualizers
    public List<VisualizerConfig> Visualizers { get; set; } = DefaultVisualizers();
    public bool CaptureAudio { get; set; } = true;
    public double VisualizerPanelWidth { get; set; } = 380;

    // Window / files
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }
    public double ConsoleHeight { get; set; } = 170;
    /// <summary>Root of the file explorer, ghci's current directory, and where file dialogs start.</summary>
    public string? WorkingDirectory { get; set; }
    public bool ShowExplorer { get; set; } = true;
    public double ExplorerWidth { get; set; } = 240;
    /// <summary>How long the last Tidal start took, to estimate progress on the loading screen.</summary>
    public double LastTidalStartSeconds { get; set; } = 8;
    public string? LastFile { get; set; }
    /// <summary>Files open in tabs when TidalStudio closed; reopened on the next start.</summary>
    public List<string> OpenFiles { get; set; } = new();
    public List<string> RecentFiles { get; set; } = new();

    public static readonly string[] DefaultPalette =
    {
        "#FF6B6B", "#4ECDC4", "#FFD166", "#A78BFA", "#60A5FA", "#F472B6", "#34D399", "#FB923C",
        "#C084FC", "#22D3EE", "#FACC15", "#F87171", "#2DD4BF", "#818CF8", "#E879F9", "#A3E635",
    };

    public static List<VisualizerConfig> DefaultVisualizers() => new()
    {
        new() { Type = "cycle", Height = 150 },
        new() { Type = "punchcard", Height = 200 },
        new() { Type = "pianoroll", Height = 200 },
        new() { Type = "spiral", Height = 260 },
        new() { Type = "pitchwheel", Height = 260 },
        new() { Type = "scope", Height = 140 },
        new() { Type = "spectrum", Height = 160 },
    };

    // ---------------- persistence ----------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TidalStudio");

    /// <summary>settings.json in %APPDATA%\TidalStudio, or the file named by TIDALSTUDIO_SETTINGS.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("TIDALSTUDIO_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(DefaultDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }
        while (settings.PatternColors.Count < DefaultPalette.Length) settings.PatternColors.Add(DefaultPalette[settings.PatternColors.Count]);
        return settings;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public void AddRecent(string file)
    {
        RecentFiles.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, file);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    public string PatternColor(string patternId)
    {
        if (int.TryParse(patternId, out int n) && n >= 1)
            return PatternColors[(n - 1) % PatternColors.Count];
        // Named patterns (p "drums"): stable colour from the name.
        int hash = 0;
        foreach (char c in patternId) hash = hash * 31 + c;
        return PatternColors[Math.Abs(hash % PatternColors.Count)];
    }
}
