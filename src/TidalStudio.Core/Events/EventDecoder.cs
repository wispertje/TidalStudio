using System.Globalization;
using TidalStudio.Core.Osc;
using TidalStudio.Core.Timing;

namespace TidalStudio.Core.Events;

/// <summary>Turns SuperDirt-style OSC traffic (/dirt/play name/value pairs inside timed bundles) into <see cref="TidalEvent"/>s.</summary>
public static class EventDecoder
{
    public const string PlayAddress = "/dirt/play";

    public static IEnumerable<TidalEvent> Decode(OscPacket packet, TimeBase timeBase, double? fallbackTime = null)
    {
        switch (packet)
        {
            case OscBundle bundle:
            {
                double? time = bundle.TimeTag <= OscBundle.Immediately ? fallbackTime : timeBase.FromNtp(bundle.TimeTag);
                foreach (var inner in bundle.Packets)
                foreach (var e in Decode(inner, timeBase, time))
                    yield return e;
                break;
            }
            case OscMessage { Address: PlayAddress } message:
                yield return FromMessage(message, fallbackTime ?? timeBase.Now);
                break;
        }
    }

    public static TidalEvent FromMessage(OscMessage message, double time)
    {
        var p = new Dictionary<string, object?>(StringComparer.Ordinal);
        var args = message.Arguments;
        for (int i = 0; i + 1 < args.Count; i += 2)
        {
            if (args[i] is string name) p[name] = args[i + 1];
        }

        double cps = Num(p, "cps") ?? 0.5625;
        double deltaSeconds = Num(p, "delta") ?? 0;
        // "legato"/"sustain" are not applied: delta is the logical event length, which is what visuals want.

        return new TidalEvent
        {
            Time = time,
            Duration = deltaSeconds,
            Cycle = Num(p, "cycle") ?? 0,
            Cps = cps,
            PatternId = Str(p, "_id_") ?? "",
            Sound = Str(p, "s"),
            N = Num(p, "n"),
            Note = Num(p, "note"),
            Orbit = (int)(Num(p, "orbit") ?? 0),
            Gain = Num(p, "gain") ?? 1,
            Pan = Num(p, "pan") ?? 0.5,
            Color = Str(p, "color"),
            Spans = ParseSpans(Str(p, "_ctx")),
            Params = p,
        };
    }

    /// <summary>
    /// Parses the _ctx parameter written by the TidalStudio boot file: "sc,sl,ec,el;..." using Tidal's
    /// 1-based context positions. The preprocessor shifts every literal by (0, id), so line = id + 1.
    /// </summary>
    public static IReadOnlyList<SourceSpan> ParseSpans(string? ctx)
    {
        if (string.IsNullOrEmpty(ctx)) return Array.Empty<SourceSpan>();
        var list = new List<SourceSpan>();
        foreach (var part in ctx.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var n = part.Split(',');
            if (n.Length != 4) continue;
            if (!int.TryParse(n[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sc) ||
                !int.TryParse(n[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sl) ||
                !int.TryParse(n[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ec) ||
                !int.TryParse(n[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int el))
                continue;
            if (sl != el || sl < 2 || ec <= sc) continue; // line 1 = literal not wrapped by the preprocessor
            var span = new SourceSpan(sl - 1, sc - 1, ec - 1);
            if (!list.Contains(span)) list.Add(span);
        }
        return list;
    }

    private static double? Num(Dictionary<string, object?> p, string key) => p.TryGetValue(key, out var v) ? v switch
    {
        float f => f,
        double d => d,
        int i => i,
        long l => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    } : null;

    private static string? Str(Dictionary<string, object?> p, string key) => p.TryGetValue(key, out var v) ? v switch
    {
        string s => s,
        null => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    } : null;
}
