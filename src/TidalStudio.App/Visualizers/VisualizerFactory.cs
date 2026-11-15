using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

public static class VisualizerFactory
{
    public static readonly (string Type, string Title)[] Available =
    {
        ("cycle", "Cycle progress"),
        ("punchcard", "Punchcard"),
        ("pianoroll", "Pianoroll"),
        ("spiral", "Spiral"),
        ("pitchwheel", "Pitchwheel"),
        ("scope", "Scope (oscilloscope)"),
        ("spectrum", "Spectrum"),
    };

    public static VisualizerBase? Create(string type, AppServices services) => type switch
    {
        "cycle" => new CycleVisualizer(services),
        "punchcard" => new PianorollVisualizer(services, punchcard: true),
        "pianoroll" => new PianorollVisualizer(services, punchcard: false),
        "spiral" => new SpiralVisualizer(services),
        "pitchwheel" => new PitchwheelVisualizer(services),
        "scope" or "tscope" => new ScopeVisualizer(services),
        "spectrum" => new SpectrumVisualizer(services),
        _ => null,
    };
}
