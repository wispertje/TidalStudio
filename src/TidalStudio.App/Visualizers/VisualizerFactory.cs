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
        ("rings", "Rings (polyrhythm)"),
        ("params", "Parameter plot (lpf, pan, …)"),
        ("channels", "Channels (activity per pattern)"),
        ("stereo", "Stereo field"),
        ("spectrogram", "Spectrogram"),
        ("meter", "Level meter"),
    };

    /// <summary>Visualizers that look best roughly square.</summary>
    public static bool IsRound(string type) => type is "spiral" or "pitchwheel" or "rings";

    public static VisualizerBase? Create(string type, AppServices services) => type switch
    {
        "cycle" => new CycleVisualizer(services),
        "punchcard" => new PianorollVisualizer(services, punchcard: true),
        "pianoroll" => new PianorollVisualizer(services, punchcard: false),
        "spiral" => new SpiralVisualizer(services),
        "pitchwheel" => new PitchwheelVisualizer(services),
        "scope" or "tscope" => new ScopeVisualizer(services),
        "spectrum" => new SpectrumVisualizer(services),
        "rings" => new RingsVisualizer(services),
        "params" => new ParamsVisualizer(services),
        "channels" => new ChannelsVisualizer(services),
        "stereo" => new StereoVisualizer(services),
        "spectrogram" => new SpectrogramVisualizer(services),
        "meter" => new MeterVisualizer(services),
        _ => null,
    };
}
