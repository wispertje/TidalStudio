using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Shows where Tidal's clock is: progress through the current cycle (ring with beat ticks), the cycle number,
/// tempo, one lane per pattern with its onsets in this cycle, and progress through a phrase of N cycles.
/// </summary>
public sealed class CycleVisualizer : VisualizerBase
{
    public CycleVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "cycle";
    public override string Title => "Cycle";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Number("beats", "Beats per cycle", 4, 1, 16, 1),
        VisOption.Number("phrase", "Phrase length (cycles)", 8, 1, 64, 1),
        VisOption.Bool("lanes", "Pattern lanes", true),
        VisOption.Bool("bpm", "Show tempo", true),
        VisOption.Color("color", "Colour", "#4ECDC4"),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var clock = Services.Clock;
        var o = Options;
        TryParseColor(o.Get("color"), out var accent);
        bool playing = clock.IsPlaying(now);
        if (!playing) accent = Color.FromRgb(110, 118, 135);

        double nowCycle = clock.HasReference ? clock.CycleAt(now) : 0;
        int beats = o.Int("beats");
        int phrase = o.Int("phrase");
        double frac = nowCycle - Math.Floor(nowCycle);
        long cycleNumber = (long)Math.Floor(nowCycle);

        // Layout: ring on the left, text + phrase bar on the right.
        double ringSize = Math.Min(bounds.Height - 16, bounds.Width * 0.5);
        var center = new Point(bounds.X + 8 + ringSize / 2, bounds.Center.Y - 4);
        double r = ringSize / 2 - 6;
        if (r < 12) return;

        // Pattern lanes (inner rings) with onsets of the current cycle.
        var lanes = new List<string>();
        if (o.Bool("lanes") && clock.HasReference)
        {
            double cycleStart = Math.Floor(nowCycle);
            var evs = Filter(Services.Events.Query(clock.TimeAt(cycleStart) - 0.05, clock.TimeAt(cycleStart + 1)))
                .Where(e => e.Cycle >= cycleStart - 1e-6 && e.Cycle < cycleStart + 1).ToList();
            lanes = evs.Select(e => e.PatternId).Distinct().OrderBy(id => int.TryParse(id, out int n) ? n : 1000).ThenBy(id => id).Take(8).ToList();
            double laneSpacing = Math.Min(9, (r * 0.55) / Math.Max(1, lanes.Count));
            for (int li = 0; li < lanes.Count; li++)
            {
                double lr = r - 12 - li * laneSpacing;
                if (lr < 6) break;
                dc.DrawEllipse(null, Pen(Color.FromArgb(18, 255, 255, 255), 1), center, lr, lr);
                foreach (var e in evs.Where(e => e.PatternId == lanes[li]))
                {
                    double a = (e.Cycle - cycleStart) * 2 * Math.PI - Math.PI / 2;
                    var p = new Point(center.X + Math.Cos(a) * lr, center.Y + Math.Sin(a) * lr);
                    bool passed = e.Cycle <= nowCycle;
                    var c = EventColor(e);
                    dc.DrawEllipse(Brush(WithAlpha(c, passed ? 1 : 0.35)), null, p, passed ? 3 : 2, passed ? 3 : 2);
                }
            }
        }

        // Track, ticks and progress arc.
        dc.DrawEllipse(null, Pen(Color.FromArgb(35, 255, 255, 255), 6), center, r, r);
        for (int b = 0; b < beats; b++)
        {
            double a = b / (double)beats * 2 * Math.PI - Math.PI / 2;
            var p1 = new Point(center.X + Math.Cos(a) * (r - 6), center.Y + Math.Sin(a) * (r - 6));
            var p2 = new Point(center.X + Math.Cos(a) * (r + 6), center.Y + Math.Sin(a) * (r + 6));
            dc.DrawLine(Pen(Color.FromArgb(b == 0 ? (byte)200 : (byte)90, 255, 255, 255), b == 0 ? 2 : 1), p1, p2);
        }
        if (clock.HasReference)
        {
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                const int steps = 96;
                ctx.BeginFigure(new Point(center.X, center.Y - r), false);
                int n = Math.Max(1, (int)(steps * frac));
                for (int i = 1; i <= n; i++)
                {
                    double a = frac * i / n * 2 * Math.PI - Math.PI / 2;
                    ctx.LineTo(new Point(center.X + Math.Cos(a) * r, center.Y + Math.Sin(a) * r));
                }
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(Brush(accent), 6, lineCap: PenLineCap.Round), arc);
            double ha = frac * 2 * Math.PI - Math.PI / 2;
            dc.DrawEllipse(Brush(Colors.White), null, new Point(center.X + Math.Cos(ha) * r, center.Y + Math.Sin(ha) * r), 4.5, 4.5);
        }

        // Beat flash in the middle.
        double beatFrac = frac * beats - Math.Floor(frac * beats);
        if (playing)
        {
            double flash = Math.Max(0, 1 - beatFrac * 3);
            dc.DrawEllipse(Brush(WithAlpha(accent, 0.15 + 0.5 * flash)), null, center, 6 + 4 * flash, 6 + 4 * flash);
        }

        // Text.
        double tx = center.X + r + 18;
        double ty = bounds.Y + 10;
        var big = Brush(Colors.White);
        DrawText(dc, clock.HasReference ? cycleNumber.ToString(CultureInfo.InvariantCulture) : "–", new Point(tx, ty), 28, big, MonoFace);
        DrawText(dc, "cycle", new Point(tx, ty + 36), 10, DimLabelBrush);
        int beatNumber = (int)Math.Floor(frac * beats) + 1;
        DrawText(dc, clock.HasReference ? $"beat {beatNumber}/{beats}" : (playing ? "" : "stopped"), new Point(tx, ty + 52), 12, LabelBrush);
        if (o.Bool("bpm"))
        {
            double cps = clock.Cps;
            DrawText(dc, $"cps {cps:0.###}  ·  {cps * 60 * beats:0.#} bpm", new Point(tx, ty + 70), 11, LabelBrush, MonoFace);
        }
        if (!playing) DrawText(dc, clock.HasReference ? "idle" : "waiting for Tidal", new Point(tx, ty + 88), 11, DimLabelBrush);

        // Phrase bar: which cycle of an N-cycle phrase we're in.
        double barX = tx, barW = Math.Max(20, bounds.Right - tx - 10), barY = bounds.Bottom - 22, barH = 8;
        if (barY > ty + 100 || bounds.Height > 120)
        {
            double phrasePos = clock.HasReference ? ((nowCycle % phrase) + phrase) % phrase : 0;
            for (int i = 0; i < phrase; i++)
            {
                double x0 = barX + i * barW / phrase;
                double w = barW / phrase - 2;
                var cell = new Rect(x0, barY, Math.Max(1, w), barH);
                double fill = Math.Clamp(phrasePos - i, 0, 1);
                dc.DrawRectangle(Brush(Color.FromArgb(30, 255, 255, 255)), null, cell, 2, 2);
                if (fill > 0) dc.DrawRectangle(Brush(WithAlpha(accent, fill >= 1 ? 0.55 : 1)), null, new Rect(x0, barY, Math.Max(1, w * fill), barH), 2, 2);
            }
            DrawText(dc, $"phrase {(int)Math.Floor(((nowCycle % phrase) + phrase) % phrase) + 1}/{phrase}", new Point(barX, barY - 16), 10, DimLabelBrush);
        }

        // Lane legend.
        if (lanes.Count > 0 && bounds.Width > 260)
        {
            double lx = bounds.Right - 10;
            double ly = ty + 2;
            foreach (var id in lanes)
            {
                var label = int.TryParse(id, out _) ? $"d{id}" : id;
                var ft = Text(label, 10, LabelBrush, MonoFace);
                dc.DrawText(ft, new Point(lx - ft.Width, ly));
                dc.DrawEllipse(Brush(Color.Parse(Services.Settings.PatternColor(id))), null, new Point(lx - ft.Width - 7, ly + ft.Height / 2), 3, 3);
                ly += 13;
                if (ly > bounds.Bottom - 40) break;
            }
        }
    }
}
