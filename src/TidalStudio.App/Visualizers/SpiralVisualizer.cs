using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Events laid out on an Archimedean spiral: one turn per cycle, the present at the outside,
/// the past spiralling inward. The spiral rotates so "now" stays at the playhead angle.
/// </summary>
public sealed class SpiralVisualizer : VisualizerBase
{
    public SpiralVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "spiral";
    public override string Title => "Spiral";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Number("cycles", "Cycles of history", 4, 1, 16, 1),
        VisOption.Number("size", "Size (turn spacing)", 1, 0.2, 3, 0.05),
        VisOption.Number("thickness", "Thickness", 9, 1, 40, 1),
        VisOption.Number("stretch", "Stretch (turns per cycle)", 1, 0.25, 4, 0.25),
        VisOption.Bool("rotate", "Rotate (now stays at playhead)", true),
        VisOption.Bool("playhead", "Show playhead", true),
        VisOption.Bool("track", "Show spiral track", true),
        VisOption.Bool("fade", "Fade the past", true),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var clock = Services.Clock;
        if (!clock.HasReference)
        {
            DrawIdleHint(dc, bounds, "Waiting for Tidal events…");
            return;
        }

        var o = Options;
        double back = o.Number("cycles");
        double stretch = o.Number("stretch");
        double thickness = o.Number("thickness");
        bool rotate = o.Bool("rotate");
        bool fade = o.Bool("fade");

        var center = bounds.Center;
        double maxR = Math.Min(bounds.Width, bounds.Height) / 2 - thickness / 2 - 4;
        if (maxR < 10) return;
        double nowCycle = clock.CycleAt(now);
        double ahead = 0.25;
        double turnSpacing = maxR / (back * stretch + ahead * stretch + 0.5) * o.Number("size");
        double rNow = maxR - ahead * stretch * turnSpacing;

        Point PointAt(double u)
        {
            // u = cycles relative to now
            double baseAngle = rotate ? 0 : nowCycle * stretch * 2 * Math.PI;
            double angle = baseAngle + u * stretch * 2 * Math.PI - Math.PI / 2;
            double r = rNow + u * stretch * turnSpacing;
            return new Point(center.X + Math.Cos(angle) * r, center.Y + Math.Sin(angle) * r);
        }

        double RadiusAt(double u) => rNow + u * stretch * turnSpacing;

        // The track.
        if (o.Bool("track"))
        {
            double uMin = -back;
            while (RadiusAt(uMin) < thickness && uMin < 0) uMin += 0.01;
            DrawSpiral(dc, PointAt, uMin, ahead, Pen(Color.FromArgb(28, 255, 255, 255), thickness), stretch);
        }

        double fromTime = clock.TimeAt(nowCycle - back) - 0.5;
        double toTime = clock.TimeAt(nowCycle + ahead) + 0.5;
        foreach (var e in Filter(Services.Events.Query(fromTime, toTime)))
        {
            double u0 = e.Cycle - nowCycle;
            double u1 = u0 + Math.Max(e.CycleDuration, 1 / 64.0);
            if (u1 < -back || u0 > ahead) continue;
            u0 = Math.Max(u0, -back);
            u1 = Math.Min(u1, ahead);
            if (RadiusAt(u0) < thickness / 2) continue;

            bool active = u0 <= 0 && u1 > 0;
            double age = Math.Clamp(-u1 / back, 0, 1);
            double alpha = active ? 1 : u0 > 0 ? 0.6 : fade ? 0.85 - 0.7 * age : 0.8;
            var color = EventColor(e);
            if (active) color = Mix(color, Colors.White, 0.3);
            // Leave a small gap between consecutive events.
            double gapU = Math.Min((u1 - u0) * 0.15, 0.01);
            var pen = new Pen(Brush(WithAlpha(color, alpha * Math.Clamp(0.5 + 0.5 * e.Gain, 0.3, 1))), thickness * (active ? 1.15 : 1), lineCap: PenLineCap.Round);
            DrawSpiral(dc, PointAt, u0 + gapU, u1 - gapU, pen, stretch);
        }

        if (o.Bool("playhead"))
        {
            var p = PointAt(0);
            var inner = new Point(center.X + (p.X - center.X) * 0.15, center.Y + (p.Y - center.Y) * 0.15);
            dc.DrawLine(Pen(Color.FromArgb(70, 255, 255, 255), 1), inner, p);
            dc.DrawEllipse(Brush(Colors.White), null, p, 3.5, 3.5);
        }
    }

    private static void DrawSpiral(DrawingContext dc, Func<double, Point> pointAt, double u0, double u1, IPen pen, double stretch)
    {
        if (u1 <= u0) return;
        double step = 1 / 96.0 / stretch;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pointAt(u0), false);
            for (double u = u0 + step; u < u1; u += step) ctx.LineTo(pointAt(u));
            ctx.LineTo(pointAt(u1));
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geo);
    }
}
