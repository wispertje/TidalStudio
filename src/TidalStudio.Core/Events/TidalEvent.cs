namespace TidalStudio.Core.Events;

/// <summary>A source position of a mini-notation element: which string literal, and the character range inside it.</summary>
/// <param name="LiteralId">Id assigned by the preprocessor to the string literal.</param>
/// <param name="Start">0-based index of the first character inside the literal (after the opening quote).</param>
/// <param name="End">0-based exclusive end index inside the literal.</param>
public readonly record struct SourceSpan(int LiteralId, int Start, int End);

/// <summary>One event (a "note" or "hit") sent by Tidal.</summary>
public sealed class TidalEvent
{
    /// <summary>When the event sounds, in <see cref="Timing.TimeBase"/> seconds.</summary>
    public double Time { get; init; }

    /// <summary>Duration of the event in seconds (delta).</summary>
    public double Duration { get; init; }

    /// <summary>Cycle position of the event onset.</summary>
    public double Cycle { get; init; }

    /// <summary>Cycles per second at the time of the event.</summary>
    public double Cps { get; init; }

    /// <summary>Pattern id (the "1" in d1), from Tidal's _id_ parameter.</summary>
    public string PatternId { get; init; } = "";

    public string? Sound { get; init; }
    public double? N { get; init; }
    public double? Note { get; init; }
    public int Orbit { get; init; }
    public double Gain { get; init; } = 1;
    public double Pan { get; init; } = 0.5;

    /// <summary>Optional colour requested by the pattern (# color "red").</summary>
    public string? Color { get; init; }

    public IReadOnlyList<SourceSpan> Spans { get; init; } = Array.Empty<SourceSpan>();

    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>();

    public double End => Time + Duration;

    /// <summary>Duration in cycles.</summary>
    public double CycleDuration => Duration * Cps;

    /// <summary>
    /// Pitch as a MIDI note number (SuperDirt convention: note 0 = MIDI 60). For samples, n picks a sample file
    /// rather than a pitch, so n only counts as pitch for synths, or always when <paramref name="treatNAsPitch"/> is set.
    /// </summary>
    public double? Midi(bool treatNAsPitch = false)
    {
        if (Note is { } note) return note + 60; // SuperDirt: note defaults to n; note wins when both are set
        if (N is { } n && (treatNAsPitch || IsSynth)) return n + 60;
        return null;
    }

    /// <summary>The label used for punchcard rows: sound plus sample number.</summary>
    public string Label => Sound is null ? (Note?.ToString("0.##") ?? "?") :
        (N is { } n && n != 0 && Note is null && !IsSynth ? $"{Sound}:{n:0.##}" : Sound);

    /// <summary>SuperDirt synths (superpiano, supersaw, …): for these n is a note, not a sample number.</summary>
    public bool IsSynth => Sound is { } s && (s.StartsWith("super", StringComparison.Ordinal) || s is "sine" or "saw" or "square" or "tri" or "pulse");

    public bool IsActive(double now) => now >= Time && now < End;
}
