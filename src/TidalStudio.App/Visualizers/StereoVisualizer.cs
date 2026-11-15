using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Where sounds sit in the stereo field: left–right is pan, up–down is pitch, gain, orbit or pattern. Each hit is a
/// dot (bigger = louder) that fades out, so <c># pan sine</c> or <c>jux rev</c> become visible.
/// </summary>
public sealed class StereoVisualizer : VisualizerBase
{
    public StereoVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "stereo";
    public override string Title => "Stereo field";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Choice("y", "Vertical axis", "pitch", "pitch", "gain", "orbit", "pattern"),
        VisOption.Number("decay", "Fade (seconds)", 0.8, 0.1, 4, 0.05),
        VisOption.Number("size", "Dot size", 6, 2, 20, 0.5),
        VisOption.Bool("nAsPitch", "Treat n as pitch for samples too", true),
        VisOption.Bool("labels", "Labels", true),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var o = Options;
        double pad = 10;
        var area = new Rect(pad, pad, Math.Max(1, bounds.Width - 2 * pad), Math.Max(1, bounds.Height - 2 * pad - 10));

        // Guides: centre line and quarter lines.
        var guide = Pen(Color.FromArgb(22, 255, 255, 255), 1);
        foreach (double f in new[] { 0.25, 0.75 }) dc.DrawLine(guide, new Point(area.X + area.Width * f, area.Y), new Point(area.X + area.Width * f, area.Bottom));
        dc.DrawLine(Pen(Color.FromArgb(55, 255, 255, 255), 1), new Point(area.Center.X, area.Y), new Point(area.Center.X, area.Bottom));
        if (o.Bool("labels"))
        {
            DrawText(dc, "L", new Point(area.X, area.Bottom + 1), 10, DimLabelBrush);
            DrawCentered(dc, "C", new Point(area.Center.X, area.Bottom + 7), 10, DimLabelBrush);
            var r = Text("R", 10, DimLabelBrush);
            dc.DrawText(r, new Point(area.Right - r.Width, area.Bottom + 1));
        }

        double decay = o.Number("decay");
        bool nAsPitch = o.Bool("nAsPitch");
        string axis = o.Choice("y");
        var events = Filter(Services.Events.Query(now - decay - 2, now + 0.001))
            .Where(e => e.Time <= now && now < e.End + decay)
            .ToList();
        if (events.Count == 0)
        {
            DrawWaiting(dc, bounds, now);
            return;
        }

        // Vertical position in 0..1 (1 = top).
        Func<TidalEvent, double> yOf;
        switch (axis)
        {
            case "gain":
                yOf = e => Math.Clamp(e.Gain / 1.5, 0, 1);
                if (o.Bool("labels")) DrawText(dc, "gain", new Point(area.X + 2, area.Y), 9.5, DimLabelBrush);
                break;
            case "orbit":
            {
                int maxOrbit = Math.Max(1, events.Max(e => e.Orbit));
                yOf = e => 1 - (e.Orbit + 0.5) / (maxOrbit + 1);
                if (o.Bool("labels"))
                    for (int orbit = 0; orbit <= maxOrbit; orbit++)
                        DrawText(dc, $"orbit {orbit}", new Point(area.X + 2, area.Y + (orbit + 0.5) / (maxOrbit + 1) * area.Height - 6), 9.5, DimLabelBrush);
                break;
            }
            case "pattern":
            {
                var ids = events.Select(e => e.PatternId).Distinct().OrderBy(PatternOrder).ToList();
                yOf = e => 1 - (ids.IndexOf(e.PatternId) + 0.5) / ids.Count;
                if (o.Bool("labels"))
                    for (int i = 0; i < ids.Count; i++)
                        DrawText(dc, PatternName(ids[i]), new Point(area.X + 2, area.Y + (i + 0.5) / ids.Count * area.Height - 6), 9.5, DimLabelBrush);
                break;
            }
            default:
            {
                var pitches = events.Select(e => e.Midi(nAsPitch)).OfType<double>().ToList();
                double lo = pitches.Count > 0 ? Math.Min(pitches.Min(), 48) : 48, hi = pitches.Count > 0 ? Math.Max(pitches.Max(), 72) : 72;
                yOf = e => e.Midi(nAsPitch) is { } m ? (m - lo) / Math.Max(1, hi - lo) * 0.9 + 0.05 : 0.5;
                if (o.Bool("labels"))
                {
                    DrawText(dc, NoteName(hi), new Point(area.X + 2, area.Y), 9.5, DimLabelBrush);
                    DrawText(dc, NoteName(lo), new Point(area.X + 2, area.Bottom - 12), 9.5, DimLabelBrush);
                }
                break;
            }
        }

        double size = o.Number("size");
        foreach (var e in events.OrderBy(e => e.Time))
        {
            bool sounding = now < e.End;
            double alpha = sounding ? 1 : 1 - (now - e.End) / decay;
            var at = new Point(area.X + Math.Clamp(e.Pan, 0, 1) * area.Width, area.Bottom - yOf(e) * area.Height);
            double r = size * (0.6 + 0.6 * Math.Clamp(e.Gain, 0, 1.5));
            var color = EventColor(e);
            if (sounding && now - e.Time < 0.12)
                dc.DrawEllipse(Brush(WithAlpha(color, 0.25)), null, at, r * 2, r * 2);
            dc.DrawEllipse(Brush(WithAlpha(color, alpha * 0.85)), sounding ? Pen(WithAlpha(Colors.White, 0.8), 1) : null, at, r, r);
        }
    }

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static string NoteName(double midi)
    {
        int m = (int)Math.Round(midi);
        return Names[((m % 12) + 12) % 12] + ((m / 12) - 1).ToString(CultureInfo.InvariantCulture);
    }
}
