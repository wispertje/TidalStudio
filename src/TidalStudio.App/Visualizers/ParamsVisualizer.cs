using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Plots one control (lpf, pan, speed, …) over the last few cycles, a line per pattern, so modulation such as
/// <c># lpf (range 200 2000 sine)</c> becomes visible. Now is at the right edge.
/// </summary>
public sealed class ParamsVisualizer : VisualizerBase
{
    // Tidal names → OSC names where they differ (lpf is sent as "cutoff").
    private static readonly (string Choice, string Osc)[] ParamChoices =
    {
        ("lpf", "cutoff"), ("hpf", "hcutoff"), ("resonance", "resonance"), ("gain", "gain"), ("pan", "pan"),
        ("speed", "speed"), ("note", "note"), ("n", "n"), ("room", "room"), ("size", "size"), ("delay", "delay"),
        ("shape", "shape"), ("crush", "crush"), ("coarse", "coarse"), ("legato", "legato"), ("accelerate", "accelerate"),
        ("squiz", "squiz"), ("orbit", "orbit"), ("cps", "cps"),
    };

    public ParamsVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "params";
    public override string Title => "Parameter plot";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Choice("param", "Parameter", "lpf", ParamChoices.Select(p => p.Choice).ToArray()),
        VisOption.Number("cycles", "Cycles shown", 4, 1, 32, 1),
        VisOption.Choice("style", "Style", "steps", "steps", "lines", "dots"),
        VisOption.Bool("zero", "Include zero in the range", false),
        VisOption.Bool("log", "Log scale (for frequencies)", false),
        VisOption.Bool("labels", "Legend & values", true),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var clock = Services.Clock;
        var o = Options;
        string choice = o.Choice("param");
        string osc = ParamChoices.FirstOrDefault(p => p.Choice == choice).Osc ?? choice;
        if (!clock.HasReference)
        {
            DrawWaiting(dc, bounds, now);
            return;
        }

        double cycles = o.Number("cycles");
        double nowCycle = clock.CycleAt(now);
        double from = nowCycle - cycles;
        double XOf(double cycle) => (cycle - from) / cycles * bounds.Width;

        // Cycle lines.
        var gridPen = Pen(Color.FromArgb(22, 255, 255, 255), 1);
        for (double c = Math.Ceiling(from); c <= nowCycle; c++)
            dc.DrawLine(gridPen, new Point(XOf(c), 0), new Point(XOf(c), bounds.Height));

        var series = Filter(Services.Events.Query(clock.TimeAt(from) - 1, now + 0.01))
            .Where(e => e.Time <= now && e.Cycle >= from - 1)
            .Select(e => (Event: e, Value: Param(e, osc)))
            .Where(x => x.Value is { } v && double.IsFinite(v))
            .GroupBy(x => x.Event.PatternId)
            .OrderBy(g => PatternOrder(g.Key))
            .Select(g => (Id: g.Key, Color: EventColor(g.First().Event),
                          Points: g.Select(x => (Cycle: x.Event.Cycle, End: x.Event.Cycle + x.Event.CycleDuration, Value: x.Value!.Value,
                                                 Active: x.Event.IsActive(now))).OrderBy(p => p.Cycle).ToList()))
            .ToList();

        if (series.Count == 0)
        {
            if (clock.IsPlaying(now)) DrawIdleHint(dc, bounds, $"no pattern sets {choice}");
            else DrawWaiting(dc, bounds, now);
            return;
        }

        bool log = o.Bool("log");
        var values = series.SelectMany(s => s.Points.Select(p => p.Value)).Where(v => !log || v > 0).ToList();
        if (values.Count == 0) return;
        double min = values.Min(), max = values.Max();
        if (o.Bool("zero") && !log) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
        if (max - min < 1e-9) { double pad = Math.Max(Math.Abs(max) * 0.1, 0.5); min -= pad; max += pad; }
        double T(double v) => log ? Math.Log(Math.Max(v, 1e-9)) : v;
        double lo = T(min), hi = T(max), margin = (hi - lo) * 0.08;
        lo -= margin; hi += margin;
        double YOf(double v) => bounds.Height - 6 - (T(v) - lo) / (hi - lo) * (bounds.Height - 12);

        string style = o.Choice("style");
        foreach (var (id, color, points) in series)
        {
            var linePen = new Pen(Brush(WithAlpha(color, 0.85)), 1.6, lineJoin: PenLineJoin.Round);
            if (style != "dots" && points.Count > 0)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    for (int i = 0; i < points.Count; i++)
                    {
                        var p = points[i];
                        var at = new Point(XOf(p.Cycle), YOf(p.Value));
                        if (i == 0) ctx.BeginFigure(at, false);
                        else if (style == "steps") { ctx.LineTo(new Point(at.X, YOf(points[i - 1].Value))); ctx.LineTo(at); }
                        else ctx.LineTo(at);
                    }
                    // Hold the last value until that event ends (not forever, in case the pattern stopped).
                    if (style == "steps") ctx.LineTo(new Point(XOf(Math.Min(nowCycle, points[^1].End)), YOf(points[^1].Value)));
                    ctx.EndFigure(false);
                }
                dc.DrawGeometry(null, linePen, geo);
            }
            foreach (var p in points)
            {
                var at = new Point(XOf(p.Cycle), YOf(p.Value));
                double size = p.Active ? 4 : style == "dots" ? 3 : 2;
                dc.DrawEllipse(Brush(p.Active ? color : WithAlpha(color, 0.8)), p.Active ? Pen(Colors.White, 1) : null, at, size, size);
            }
        }

        if (o.Bool("labels"))
        {
            DrawText(dc, Format(max), new Point(4, 2), 9.5, DimLabelBrush, MonoFace);
            DrawText(dc, Format(min), new Point(4, bounds.Height - 15), 9.5, DimLabelBrush, MonoFace);
            double y = 2;
            var title = Text(choice, 10.5, LabelBrush);
            dc.DrawText(title, new Point(bounds.Width - title.Width - 6, y));
            y += 15;
            foreach (var (id, color, points) in series)
            {
                string label = $"{PatternName(id)} {Format(points[^1].Value)}";
                var ft = Text(label, 10, Brush(color), MonoFace);
                dc.DrawText(ft, new Point(bounds.Width - ft.Width - 6, y));
                y += 14;
                if (y > bounds.Height - 14) break;
            }
        }
    }

    private static string Format(double v) =>
        Math.Abs(v) >= 1000 ? v.ToString("0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);
}
