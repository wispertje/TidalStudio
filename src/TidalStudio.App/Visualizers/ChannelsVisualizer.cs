using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// A mixer-like strip per pattern: the bar jumps on every hit (height = gain) and falls back, with the pattern's
/// sounds and how many hits it played in the last cycle. Good for seeing at a glance what is playing.
/// </summary>
public sealed class ChannelsVisualizer : VisualizerBase
{
    public ChannelsVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "channels";
    public override string Title => "Channels";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Number("decay", "Fall time (seconds)", 0.3, 0.05, 2, 0.05),
        VisOption.Number("keep", "Keep idle channels (cycles)", 4, 1, 32, 1),
        VisOption.Bool("sounds", "Sound names", true),
        VisOption.Bool("rate", "Hits per cycle", true),
        VisOption.Bool("peak", "Peak marker", true),
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

        double decay = o.Number("decay");
        double nowCycle = clock.CycleAt(now);
        double keepFrom = clock.TimeAt(nowCycle - o.Number("keep"));
        var recent = Filter(Services.Events.Query(keepFrom, now + 0.01)).Where(e => e.Time <= now).ToList();
        var channels = recent.GroupBy(e => e.PatternId).OrderBy(g => PatternOrder(g.Key)).ToList();
        if (channels.Count == 0)
        {
            DrawWaiting(dc, bounds, now);
            return;
        }

        bool showSounds = o.Bool("sounds"), showRate = o.Bool("rate");
        double footer = 16 + (showSounds ? 13 : 0) + (showRate ? 13 : 0);
        double colWidth = Math.Min(70, bounds.Width / channels.Count);
        double barWidth = Math.Max(4, colWidth * 0.5);
        double top = 8, barHeight = Math.Max(10, bounds.Height - footer - top - 4);
        double lastCycleStart = clock.TimeAt(nowCycle - 1);

        for (int i = 0; i < channels.Count; i++)
        {
            string g = channels[i].Key;
            var events = channels[i].OrderBy(e => e.Time).ToList();
            var color = EventColor(events[^1]);
            double cx = (i + 0.5) * colWidth;

            // Envelope: each hit jumps to its gain and decays exponentially; the loudest wins.
            double level = 0, peak = 0;
            foreach (var e in events)
            {
                double age = now - e.Time;
                double hit = Math.Clamp(e.Gain / 1.3, 0.05, 1);
                if (age < decay * 6) level = Math.Max(level, hit * Math.Exp(-age / decay));
                if (age < 1.5) peak = Math.Max(peak, hit);
            }

            var track = new Rect(cx - barWidth / 2, top, barWidth, barHeight);
            dc.FillRectangle(Brush(Color.FromArgb(18, 255, 255, 255)), track, 3);
            double h = level * barHeight;
            if (h > 0.5)
                dc.FillRectangle(Brush(WithAlpha(Mix(color, Colors.White, level * 0.35), 0.4 + 0.6 * level)),
                    new Rect(track.X, track.Bottom - h, barWidth, h), 3);
            if (o.Bool("peak") && peak > 0)
            {
                double py = track.Bottom - peak * barHeight;
                dc.DrawLine(Pen(WithAlpha(color, 0.9), 2), new Point(track.X - 2, py), new Point(track.Right + 2, py));
            }

            double y = track.Bottom + 3;
            DrawCentered(dc, PatternName(g), new Point(cx, y + 7), 11, Brush(color));
            y += 15;
            if (showSounds)
            {
                string sounds = string.Join(" ", events.Where(e => e.Time >= lastCycleStart).Select(e => e.Sound ?? "").Where(s => s.Length > 0).Distinct().Take(3));
                DrawCentered(dc, Trim(sounds, colWidth), new Point(cx, y + 6), 9.5, LabelBrush);
                y += 13;
            }
            if (showRate)
            {
                int hits = events.Count(e => e.Time >= lastCycleStart);
                DrawCentered(dc, hits == 0 ? "idle" : $"{hits}/cyc", new Point(cx, y + 6), 9.5, DimLabelBrush);
            }
        }
    }

    private static string Trim(string text, double width)
    {
        int max = Math.Max(3, (int)(width / 5.5));
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
