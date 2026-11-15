using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// One ring per pattern (or sound) showing the current cycle: each onset is a dot at its position in the cycle and
/// the dots are joined into a shape, so polyrhythms and euclidean rhythms read at a glance. A hand sweeps round.
/// </summary>
/// <remarks>
/// Tidal only sends events ~50 ms ahead, so the part of the ring that is still to come shows the previous cycle,
/// faintly — for a looping pattern that is exactly what is about to play.
/// </remarks>
public sealed class RingsVisualizer : VisualizerBase
{
    public RingsVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "rings";
    public override string Title => "Rings";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Choice("group", "One ring per", "pattern", "pattern", "sound"),
        VisOption.Bool("shape", "Join onsets into a shape", true),
        VisOption.Bool("hand", "Playhead hand", true),
        VisOption.Bool("ghost", "Show last cycle ahead of the hand", true),
        VisOption.Number("rings", "Max rings", 6, 1, 12, 1),
        VisOption.Number("dot", "Dot size", 4, 2, 12, 0.5),
        VisOption.Number("steps", "Step ticks (0 = none)", 0, 0, 32, 1),
        VisOption.Bool("labels", "Legend", true),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var clock = Services.Clock;
        var o = Options;
        if (!clock.HasReference)
        {
            DrawWaiting(dc, bounds, now);
            return;
        }

        double nowCycle = clock.CycleAt(now);
        double cycleStart = Math.Floor(nowCycle);
        double phaseNow = nowCycle - cycleStart;
        bool bySound = o.Choice("group") == "sound";
        bool ghost = o.Bool("ghost");

        // This cycle up to now, plus (ghosted) the previous cycle from now on.
        var events = Filter(Services.Events.Query(clock.TimeAt(cycleStart - 1) - 0.05, now + 0.05))
            .Select(e => (Event: e, Phase: e.Cycle - Math.Floor(e.Cycle), Current: e.Cycle >= cycleStart - 1e-6))
            .Where(x => x.Event.Cycle >= cycleStart - 1 - 1e-6 && x.Event.Cycle < cycleStart + 1)
            .Where(x => x.Current ? x.Phase <= phaseNow + 1e-6 : ghost && x.Phase > phaseNow)
            .ToList();

        string Key(TidalEvent e) => bySound ? e.Label : e.PatternId;
        var groups = events.GroupBy(x => Key(x.Event))
            .OrderBy(g => bySound ? (0, g.Key) : PatternOrder(g.Key))
            .Take(o.Int("rings"))
            .ToList();

        var center = bounds.Center;
        double outer = Math.Min(bounds.Width, bounds.Height) / 2 - 8;
        if (outer < 16) return;
        double inner = outer * 0.28;
        int count = Math.Max(1, groups.Count);
        double spacing = count == 1 ? 0 : (outer - inner) / (count - 1);

        // Step ticks on the outer ring.
        int steps = o.Int("steps");
        for (int s = 0; s < steps; s++)
        {
            double a = s / (double)steps * 2 * Math.PI - Math.PI / 2;
            dc.DrawLine(Pen(Color.FromArgb(s == 0 ? (byte)120 : (byte)50, 255, 255, 255), 1),
                new Point(center.X + Math.Cos(a) * (outer + 2), center.Y + Math.Sin(a) * (outer + 2)),
                new Point(center.X + Math.Cos(a) * (outer + 7), center.Y + Math.Sin(a) * (outer + 7)));
        }

        double dot = o.Number("dot");
        var legend = new List<(string Name, Color Color)>();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            double r = count == 1 ? outer * 0.8 : outer - gi * spacing;
            var first = g.First().Event;
            Color color = bySound ? HashColor(g.Key) : EventColor(first);
            legend.Add((bySound ? g.Key : PatternName(g.Key), color));

            dc.DrawEllipse(null, Pen(Color.FromArgb(28, 255, 255, 255), 1), center, r, r);

            // One point per distinct position (several sounds on the same step make one dot).
            var points = g.GroupBy(x => Math.Round(x.Phase, 4)).OrderBy(p => p.Key)
                .Select(p => (Phase: p.Key, Current: p.Any(x => x.Current), Active: p.Any(x => x.Current && x.Event.Time <= now && now < x.Event.Time + Math.Max(x.Event.Duration, 0.12))))
                .ToList();
            Point At(double phase, double radius)
            {
                double a = phase * 2 * Math.PI - Math.PI / 2;
                return new Point(center.X + Math.Cos(a) * radius, center.Y + Math.Sin(a) * radius);
            }

            if (o.Bool("shape") && points.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(At(points[0].Phase, r), true);
                    foreach (var p in points.Skip(1)) ctx.LineTo(At(p.Phase, r));
                    ctx.EndFigure(true);
                }
                dc.DrawGeometry(Brush(WithAlpha(color, 0.07)), new Pen(Brush(WithAlpha(color, 0.45)), 1.2, lineJoin: PenLineJoin.Round), geo);
            }
            foreach (var p in points)
            {
                var at = At(p.Phase, r);
                if (p.Active)
                {
                    dc.DrawEllipse(Brush(WithAlpha(color, 0.25)), null, at, dot * 2.6, dot * 2.6);
                    dc.DrawEllipse(Brush(color), Pen(Colors.White, 1.2), at, dot * 1.4, dot * 1.4);
                }
                else dc.DrawEllipse(Brush(WithAlpha(color, p.Current ? 0.95 : 0.3)), null, at, dot, dot);
            }
        }

        if (o.Bool("hand"))
        {
            double a = phaseNow * 2 * Math.PI - Math.PI / 2;
            var tip = new Point(center.X + Math.Cos(a) * (outer + 4), center.Y + Math.Sin(a) * (outer + 4));
            dc.DrawLine(new Pen(Brush(Color.FromArgb(150, 255, 255, 255)), 1.5, lineCap: PenLineCap.Round), center, tip);
            dc.DrawEllipse(Brush(Color.FromArgb(200, 255, 255, 255)), null, center, 2.5, 2.5);
        }

        if (o.Bool("labels"))
        {
            double y = 4;
            foreach (var (name, color) in legend)
            {
                dc.DrawEllipse(Brush(color), null, new Point(9, y + 7), 3.5, 3.5);
                DrawText(dc, name, new Point(17, y), 10.5, LabelBrush);
                y += 15;
                if (y > bounds.Height - 12) break;
            }
        }

        if (groups.Count == 0) DrawWaiting(dc, bounds, now);
    }
}
