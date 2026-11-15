using System.Globalization;

namespace TidalStudio.App.Visualizers;

public enum OptionKind { Bool, Number, Choice, Color }

/// <summary>Describes one user-tweakable option of a visualizer (rendered in its options flyout).</summary>
public sealed record VisOption(
    string Key,
    string Label,
    OptionKind Kind,
    string Default,
    double Min = 0,
    double Max = 1,
    double Step = 0.1,
    IReadOnlyList<string>? Choices = null)
{
    public static VisOption Bool(string key, string label, bool def) => new(key, label, OptionKind.Bool, def ? "true" : "false");

    public static VisOption Number(string key, string label, double def, double min, double max, double step) =>
        new(key, label, OptionKind.Number, def.ToString(CultureInfo.InvariantCulture), min, max, step);

    public static VisOption Choice(string key, string label, string def, params string[] choices) =>
        new(key, label, OptionKind.Choice, def, Choices: choices);

    public static VisOption Color(string key, string label, string def) => new(key, label, OptionKind.Color, def);
}

/// <summary>Option values stored as invariant strings (persisted in settings.json).</summary>
public sealed class OptionValues
{
    private readonly Dictionary<string, string> _values;
    private readonly IReadOnlyList<VisOption> _definitions;

    public OptionValues(Dictionary<string, string> values, IReadOnlyList<VisOption> definitions)
    {
        _values = values;
        _definitions = definitions;
    }

    public event Action? Changed;

    public IReadOnlyList<VisOption> Definitions => _definitions;

    public string Get(string key)
    {
        if (_values.TryGetValue(key, out var v)) return v;
        return _definitions.FirstOrDefault(d => d.Key == key)?.Default ?? "";
    }

    public void Set(string key, string value)
    {
        _values[key] = value;
        Changed?.Invoke();
    }

    public bool Bool(string key) => Get(key) == "true";

    public double Number(string key)
    {
        var def = _definitions.FirstOrDefault(d => d.Key == key);
        double v = double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
            : double.TryParse(def?.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) ? dd : 0;
        if (def is { Kind: OptionKind.Number }) v = Math.Clamp(v, def.Min, def.Max);
        return v;
    }

    public int Int(string key) => (int)Math.Round(Number(key));

    public string Choice(string key) => Get(key);
}
