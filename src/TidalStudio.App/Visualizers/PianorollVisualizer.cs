using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Scrolling pianoroll (rows = pitch) or punchcard (rows = sound). Tidal only sends events a little ahead of
/// time, so the part after the playhead shows a faint "ghost" repeat of the last cycle (optional).
/// </summary>
public class PianorollVisualizer : VisualizerBase
{
    private readonly bool _punchcardDefault;

    public PianorollVisualizer(AppServices services, bool punchcard) : base(services)
    {
        _punchcardDefault = punchcard;
    }

    public override string TypeName => _punchcardDefault ? "punchcard" : "pianoroll";
    public override string Title => _punchcardDefault ? "Punchcard" : "Pianoroll";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Choice("rows", "Rows", _punchcardDefault ? "sound" : "pitch", "sound", "pitch"),
        VisOption.Number("cycles", "Cycles shown", 4, 0.5, 16, 0.5),
        VisOption.Number("playhead", "Playhead position", _punchcardDefault ? 0.75 : 0.5, 0, 1, 0.05),
        VisOption.Bool("vertical", "Vertical", false),
        VisOption.Bool("labels", "Labels", true),
        VisOption.Bool("fold", "Fold empty rows (pitch)", true),
        VisOption.Bool("ghost", "Preview future (repeat last cycle)", true),
        VisOption.Bool("playheadLine", "Show playhead", true),
        VisOption.Choice("color", "Colour by", "pattern", "pattern", "sound", "fixed"),
        VisOption.Color("fixedColor", "Fixed colour", "#4ECDC4"),
        VisOption.Number("gap", "Gap between notes", 1, 0, 4, 0.5),
        VisOption.Bool("nAsPitch", "Treat n as pitch for samples too", false),
    };

    private readonly record struct Note(TidalEvent Event, double Start, double End, bool Ghost);

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var clock = Services.Clock;
        if (!clock.HasReference)
        {
            DrawIdleHint(dc, bounds, "Waiting for Tidal events…");
            return;
        }

        var o = Options;
        bool pitchRows = o.Choice("rows") == "pitch";
        bool vertical = o.Bool("vertical");
        double span = o.Number("cycles");
        double playhead = o.Number("playhead");
        double nowCycle = clock.CycleAt(now);
        double startCycle = nowCycle - span * playhead;
        double endCycle = startCycle + span;

        // Gather real events, plus ghosts for the not-yet-known future.
        var latest = Services.Events.Latest;
        double latestCycle = latest?.Cycle ?? nowCycle;
        double queryFrom = clock.TimeAt(Math.Min(startCycle, latestCycle - 1)) - 0.5;
        double queryTo = clock.TimeAt(endCycle) + 0.5;
        var events = Filter(Services.Events.Query(queryFrom, queryTo)).ToList();

        var notes = new List<Note>(events.Count * 2);
        foreach (var e in events)
        {
            double len = Math.Max(e.CycleDuration, 1 / 64.0);
            if (e.Cycle + len >= startCycle && e.Cycle <= endCycle) notes.Add(new Note(e, e.Cycle, e.Cycle + len, false));
        }
        if (o.Bool("ghost") && latest is not null && clock.IsPlaying(now))
        {
            foreach (var e in events)
            {
                if (e.Cycle <= latestCycle - 1 || e.Cycle > latestCycle) continue;
                double len = Math.Max(e.CycleDuration, 1 / 64.0);
                for (int k = 1; e.Cycle + k <= endCycle; k++)
                {
                    double s = e.Cycle + k;
                    if (s > latestCycle + 1e-6) notes.Add(new Note(e, s, s + len, true));
                }
            }
        }

        // Rows.
        List<string> rowKeys;
        Func<TidalEvent, string?> keyOf;
        if (pitchRows)
        {
            bool nAsPitch = o.Bool("nAsPitch");
            keyOf = e => e.Midi(nAsPitch) is { } m ? Math.Round(m, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null;
            var pitches = notes.Select(n => n.Event.Midi(nAsPitch)).Where(m => m.HasValue).Select(m => Math.Round(m!.Value, 2)).Distinct().OrderBy(m => m).ToList();
            if (!o.Bool("fold") && pitches.Count > 0)
            {
                int lo = (int)Math.Floor(pitches[0]), hi = (int)Math.Ceiling(pitches[^1]);
                var all = Enumerable.Range(lo, hi - lo + 1).Select(i => (double)i).Union(pitches).OrderBy(x => x).ToList();
                pitches = all;
            }
            pitches.Reverse(); // high notes on top
            rowKeys = pitches.Select(m => m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        }
        else
        {
            keyOf = e => e.Label;
            rowKeys = notes.Select(n => n.Event.Label).Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
        }

        DrawGrid(dc, bounds, startCycle, endCycle, vertical);

        if (rowKeys.Count == 0)
        {
            DrawIdleHint(dc, bounds, pitchRows ? "No pitched events (use note or n)" : "No events in view");
            DrawPlayhead(dc, bounds, playhead, vertical);
            return;
        }

        var rowIndex = new Dictionary<string, int>();
        for (int i = 0; i < rowKeys.Count; i++) rowIndex[rowKeys[i]] = i;

        bool labels = o.Bool("labels");
        double labelSpace = labels && !vertical ? Math.Min(70, bounds.Width * 0.18) : 0;
        double rowsExtent = vertical ? bounds.Width : bounds.Height;
        double timeExtent = (vertical ? bounds.Height : bounds.Width) - labelSpace;
        double rowSize = rowsExtent / rowKeys.Count;
        double gap = Math.Min(o.Number("gap"), rowSize * 0.3);
        string colorMode = o.Choice("color");
        TryParseColor(o.Get("fixedColor"), out var fixedColor);

        foreach (var n in notes)
        {
            var key = keyOf(n.Event);
            if (key is null || !rowIndex.TryGetValue(key, out int row)) continue;

            double t0 = (Math.Max(n.Start, startCycle) - startCycle) / span * timeExtent;
            double t1 = (Math.Min(n.End, endCycle) - startCycle) / span * timeExtent;
            if (t1 - t0 < 1.5) t1 = t0 + 1.5;
            t1 = Math.Max(t0 + 1, t1 - gap);

            Rect r = vertical
                ? new Rect(row * rowSize + gap / 2, t0, Math.Max(1, rowSize - gap), t1 - t0)
                : new Rect(labelSpace + t0, row * rowSize + gap / 2, t1 - t0, Math.Max(1, rowSize - gap));

            Color c = colorMode switch
            {
                "sound" => HashColor(n.Event.Label),
                "fixed" => fixedColor,
                _ => EventColor(n.Event),
            };
            bool active = !n.Ghost && nowCycle >= n.Start && nowCycle < n.End;
            bool past = !n.Ghost && n.End <= nowCycle;
            double alpha = n.Ghost ? 0.18 : active ? 1.0 : past ? 0.55 : 0.8;
            double gainScale = Math.Clamp(0.55 + 0.45 * n.Event.Gain, 0.3, 1);
            var fill = Brush(WithAlpha(active ? Mix(c, Colors.White, 0.25) : c, alpha * gainScale));
            double radius = Math.Min(3, Math.Min(r.Width, r.Height) / 2);
            dc.DrawRectangle(fill, active ? Pen(Colors.White, 1) : null, r, radius, radius);
        }

        if (labels && rowSize >= 8)
        {
            double fontSize = Math.Clamp(rowSize * 0.7, 8, 12);
            for (int i = 0; i < rowKeys.Count; i++)
            {
                string label = pitchRows ? MidiName(rowKeys[i]) : rowKeys[i];
                if (vertical)
                {
                    if (rowSize >= 26) DrawText(dc, label, new Point(i * rowSize + 2, 2), fontSize, LabelBrush);
                }
                else
                {
                    var ft = Text(label, fontSize, LabelBrush);
                    ft.MaxLineCount = 1;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                    ft.MaxTextWidth = Math.Max(10, labelSpace - 4);
                    dc.DrawText(ft, new Point(4, i * rowSize + (rowSize - ft.Height) / 2));
                }
            }
        }

        DrawPlayhead(dc, bounds, playhead, vertical, labelSpace);
    }

    private void DrawGrid(DrawingContext dc, Rect bounds, double startCycle, double endCycle, bool vertical)
    {
        double span = endCycle - startCycle;
        double labelSpace = Options.Bool("labels") && !vertical ? Math.Min(70, bounds.Width * 0.18) : 0;
        double extent = (vertical ? bounds.Height : bounds.Width) - labelSpace;
        var minor = Pen(Color.FromArgb(18, 255, 255, 255), 1);
        var major = Pen(Color.FromArgb(55, 255, 255, 255), 1);
        double step = span > 8 ? 1 : 0.25;
        for (double c = Math.Ceiling(startCycle / step) * step; c <= endCycle; c += step)
        {
            double p = (c - startCycle) / span * extent;
            bool isCycle = Math.Abs(c - Math.Round(c)) < 1e-6;
            var pen = isCycle ? major : minor;
            if (vertical) dc.DrawLine(pen, new Point(0, p), new Point(bounds.Width, p));
            else dc.DrawLine(pen, new Point(labelSpace + p, 0), new Point(labelSpace + p, bounds.Height));
        }
    }

    private void DrawPlayhead(DrawingContext dc, Rect bounds, double playhead, bool vertical, double labelSpace = 0)
    {
        if (!Options.Bool("playheadLine")) return;
        var pen = Pen(Color.FromArgb(200, 255, 255, 255), 1.5);
        if (vertical)
        {
            double y = playhead * bounds.Height;
            dc.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
        }
        else
        {
            double x = labelSpace + playhead * (bounds.Width - labelSpace);
            dc.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        }
    }

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static string MidiName(string midiText)
    {
        if (!double.TryParse(midiText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double midi))
            return midiText;
        int m = (int)Math.Round(midi);
        // Tidal's convention: note 0 (MIDI 60) is c5.
        string name = NoteNames[((m % 12) + 12) % 12] + (int)Math.Floor(m / 12.0);
        return Math.Abs(midi - m) > 0.01 ? $"{name}{(midi - m >= 0 ? "+" : "")}{(midi - m) * 100:0}c" : name;
    }
}
