using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Visualizers;

/// <summary>Sounding notes placed on a circle that spans one octave (position = pitch class, microtones included).</summary>
public sealed class PitchwheelVisualizer : VisualizerBase
{
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public PitchwheelVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "pitchwheel";
    public override string Title => "Pitchwheel";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Choice("mode", "Draw as", "flake", "flake", "polygon", "dots"),
        VisOption.Number("edo", "Divisions (EDO grid)", 12, 5, 53, 1),
        VisOption.Bool("labels", "Note names", true),
        VisOption.Bool("circle", "Show circle", true),
        VisOption.Number("decay", "Trail (seconds)", 0.4, 0, 3, 0.05),
        VisOption.Number("thickness", "Thickness", 3, 1, 12, 0.5),
        VisOption.Number("dot", "Dot size", 7, 2, 20, 1),
        VisOption.Bool("nAsPitch", "Treat n as pitch for samples too", false),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var o = Options;
        var center = bounds.Center;
        double labelPad = o.Bool("labels") ? 18 : 6;
        double r = Math.Min(bounds.Width, bounds.Height) / 2 - labelPad - o.Number("dot");
        if (r < 10) return;
        int edo = o.Int("edo");

        if (o.Bool("circle"))
        {
            dc.DrawEllipse(null, Pen(Color.FromArgb(45, 255, 255, 255), 1), center, r, r);
            for (int i = 0; i < edo; i++)
            {
                double a = i / (double)edo * 2 * Math.PI - Math.PI / 2;
                var p1 = new Point(center.X + Math.Cos(a) * (r - 4), center.Y + Math.Sin(a) * (r - 4));
                var p2 = new Point(center.X + Math.Cos(a) * (r + 4), center.Y + Math.Sin(a) * (r + 4));
                dc.DrawLine(Pen(Color.FromArgb(70, 255, 255, 255), 1), p1, p2);
            }
        }
        if (o.Bool("labels"))
        {
            for (int i = 0; i < 12; i++)
            {
                double a = i / 12.0 * 2 * Math.PI - Math.PI / 2;
                var p = new Point(center.X + Math.Cos(a) * (r + labelPad * 0.75), center.Y + Math.Sin(a) * (r + labelPad * 0.75));
                DrawCentered(dc, Names[i], p, 10, Names[i].Length == 1 ? LabelBrush : DimLabelBrush);
            }
        }

        double decay = o.Number("decay");
        bool nAsPitch = o.Bool("nAsPitch");
        var candidates = Filter(Services.Events.Query(now - decay - 4, now + 0.001));
        var points = new List<(Point P, Color C, double Alpha, double Angle)>();
        foreach (var e in candidates)
        {
            if (e.Midi(nAsPitch) is not { } midi) continue;
            if (e.Time > now) continue;
            double end = e.Time + Math.Max(e.Duration, 0.05);
            double alpha;
            if (now < end) alpha = 1;
            else if (decay > 0 && now < end + decay) alpha = 1 - (now - end) / decay;
            else continue;

            double pc = ((midi - 60) / 12.0 % 1 + 1) % 1;
            double angle = pc * 2 * Math.PI - Math.PI / 2;
            var p = new Point(center.X + Math.Cos(angle) * r, center.Y + Math.Sin(angle) * r);
            points.Add((p, EventColor(e), alpha, angle));
        }

        if (points.Count == 0)
        {
            if (!Services.Clock.IsPlaying(now)) DrawIdleHint(dc, new Rect(center.X - 60, center.Y - 10, 120, 20), "no notes");
            return;
        }

        double thickness = o.Number("thickness");
        string mode = o.Choice("mode");
        if (mode == "flake")
        {
            foreach (var pt in points)
                dc.DrawLine(new Pen(Brush(WithAlpha(pt.C, pt.Alpha * 0.9)), thickness, lineCap: PenLineCap.Round), center, pt.P);
        }
        else if (mode == "polygon" && points.Count > 1)
        {
            var ordered = points.Where(p => p.Alpha >= 0.999).OrderBy(p => p.Angle).ToList();
            if (ordered.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(ordered[0].P, true);
                    foreach (var p in ordered.Skip(1)) ctx.LineTo(p.P);
                    ctx.EndFigure(true);
                }
                var c = ordered[0].C;
                dc.DrawGeometry(Brush(WithAlpha(c, 0.18)), new Pen(Brush(WithAlpha(c, 0.9)), thickness, lineJoin: PenLineJoin.Round), geo);
            }
        }

        double dot = o.Number("dot");
        foreach (var pt in points)
            dc.DrawEllipse(Brush(WithAlpha(pt.C, pt.Alpha)), pt.Alpha >= 0.999 ? Pen(Colors.White, 1) : null, pt.P, dot, dot);
    }
}
