using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

/// <summary>
/// Level meter of the captured audio: peak and RMS bars with peak hold and a dB scale, plus a loudness history so
/// you can see a build-up or a drop. Red means close to clipping.
/// </summary>
public sealed class MeterVisualizer : VisualizerBase
{
    private float[] _samples = Array.Empty<float>();
    private double _peakDb = -120, _rmsDb = -120, _holdDb = -120, _holdTime, _lastTime;
    private readonly Queue<(double Time, double Rms, double Peak)> _history = new();

    private static readonly Color Green = Color.Parse("#34D399");
    private static readonly Color Yellow = Color.Parse("#FFD166");
    private static readonly Color Red = Color.Parse("#FF5370");

    public MeterVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "meter";
    public override string Title => "Level meter";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Number("minDb", "Bottom of the scale (dB)", -60, -96, -20, 6),
        VisOption.Number("history", "History (seconds, 0 = off)", 10, 0, 60, 1),
        VisOption.Number("hold", "Peak hold (seconds)", 1.5, 0, 5, 0.1),
        VisOption.Number("fall", "Fall speed (dB/s)", 24, 5, 90, 1),
        VisOption.Bool("numbers", "Numbers", true),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var o = Options;
        if (!Services.AudioCapture.IsRunning)
        {
            DrawIdleHint(dc, bounds, Services.AudioCapture.Error is { } err ? $"Audio capture failed: {err}" : "Audio capture is off (View ▸ Audio capture)");
            return;
        }

        // Measure the last ~50 ms.
        var audio = Services.Audio;
        int n = Math.Clamp(audio.SampleRate / 20, 256, audio.Capacity / 2);
        if (_samples.Length != n) _samples = new float[n];
        audio.ReadLatest(_samples);
        double sum = 0, peak = 0;
        foreach (float v in _samples)
        {
            sum += v * v;
            peak = Math.Max(peak, Math.Abs(v));
        }
        double rmsDb = ToDb(Math.Sqrt(sum / n)), peakDb = ToDb(peak);

        // Ballistics: instant rise, steady fall.
        double dt = Math.Clamp(now - _lastTime, 0, 0.5);
        _lastTime = now;
        double fall = o.Number("fall") * dt;
        _peakDb = Math.Max(peakDb, _peakDb - fall);
        _rmsDb = Math.Max(rmsDb, _rmsDb - fall);
        if (_peakDb >= _holdDb || now - _holdTime > o.Number("hold"))
        {
            _holdDb = _peakDb;
            _holdTime = now;
        }

        double historySeconds = o.Number("history");
        _history.Enqueue((now, rmsDb, peakDb));
        while (_history.Count > 0 && _history.Peek().Time < now - Math.Max(historySeconds, 0.1)) _history.Dequeue();

        double minDb = o.Number("minDb");
        double Frac(double db) => Math.Clamp((db - minDb) / -minDb, 0, 1);

        // Layout: bars on the left, history on the right.
        double meterWidth = historySeconds > 0 ? Math.Min(90, bounds.Width * 0.35) : bounds.Width;
        double top = o.Bool("numbers") ? 18 : 8, bottom = bounds.Height - (o.Bool("numbers") ? 18 : 6);
        double h = Math.Max(10, bottom - top);
        double barW = Math.Max(6, Math.Min(18, (meterWidth - 40) / 2));
        double x0 = 30;

        // dB scale.
        for (double db = 0; db >= minDb; db -= minDb <= -48 ? 12 : 6)
        {
            double y = bottom - Frac(db) * h;
            dc.DrawLine(Pen(Color.FromArgb(30, 255, 255, 255), 1), new Point(x0 - 4, y), new Point(x0 + 2 * barW + 10, y));
            var t = Text(db.ToString("0", CultureInfo.InvariantCulture), 9, DimLabelBrush, MonoFace);
            dc.DrawText(t, new Point(x0 - 6 - t.Width, y - t.Height / 2));
        }

        DrawBar(dc, new Rect(x0, top, barW, h), Frac(_peakDb), minDb);
        DrawBar(dc, new Rect(x0 + barW + 6, top, barW, h), Frac(_rmsDb), minDb);
        double holdY = bottom - Frac(_holdDb) * h;
        if (_holdDb > minDb)
            dc.DrawLine(Pen(LevelColor(_holdDb), 2), new Point(x0, holdY), new Point(x0 + barW, holdY));

        if (o.Bool("numbers"))
        {
            DrawCentered(dc, "pk", new Point(x0 + barW / 2, bottom + 8), 9, DimLabelBrush);
            DrawCentered(dc, "rms", new Point(x0 + barW * 1.5 + 6, bottom + 8), 9, DimLabelBrush);
            string hold = _holdDb <= minDb ? "-∞" : _holdDb.ToString("0.0", CultureInfo.InvariantCulture);
            var readout = Text($"peak {hold} dB", 10, Brush(LevelColor(_holdDb)), MonoFace);
            dc.DrawText(readout, new Point(bounds.Width - readout.Width - 6, 2));
        }

        // History: RMS as a filled area, peaks as a thin line.
        if (historySeconds > 0 && _history.Count > 1)
        {
            var area = new Rect(meterWidth + 8, top, Math.Max(10, bounds.Width - meterWidth - 14), h);
            dc.DrawLine(Pen(Color.FromArgb(30, 255, 255, 255), 1), new Point(area.X, area.Bottom), new Point(area.Right, area.Bottom));
            double XOf(double t) => area.Right - (now - t) / historySeconds * area.Width;
            var fill = new StreamGeometry();
            var peaks = new StreamGeometry();
            var items = _history.ToArray();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(XOf(items[0].Time), area.Bottom), true);
                foreach (var it in items) ctx.LineTo(new Point(XOf(it.Time), area.Bottom - Frac(it.Rms) * area.Height));
                ctx.LineTo(new Point(XOf(items[^1].Time), area.Bottom));
                ctx.EndFigure(true);
            }
            using (var ctx = peaks.Open())
            {
                ctx.BeginFigure(new Point(XOf(items[0].Time), area.Bottom - Frac(items[0].Peak) * area.Height), false);
                foreach (var it in items.Skip(1)) ctx.LineTo(new Point(XOf(it.Time), area.Bottom - Frac(it.Peak) * area.Height));
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(Brush(WithAlpha(Green, 0.35)), Pen(WithAlpha(Green, 0.8), 1), fill);
            dc.DrawGeometry(null, Pen(Color.FromArgb(90, 255, 255, 255), 1), peaks);
            double clipY = area.Bottom - Frac(-1) * area.Height;
            dc.DrawLine(Pen(WithAlpha(Red, 0.35), 1), new Point(area.X, clipY), new Point(area.Right, clipY));
        }
    }

    private void DrawBar(DrawingContext dc, Rect track, double frac, double minDb)
    {
        dc.FillRectangle(Brush(Color.FromArgb(18, 255, 255, 255)), track, 2);
        if (frac <= 0) return;
        // Green up to -12 dB, yellow up to -3 dB, red above.
        double FracOf(double db) => Math.Clamp((db - minDb) / -minDb, 0, 1);
        (double From, double To, Color C)[] zones = { (0, FracOf(-12), Green), (FracOf(-12), FracOf(-3), Yellow), (FracOf(-3), 1, Red) };
        foreach (var (from, to, c) in zones)
        {
            double a = from, b = Math.Min(to, frac);
            if (b <= a) continue;
            dc.FillRectangle(Brush(WithAlpha(c, 0.9)), new Rect(track.X, track.Bottom - b * track.Height, track.Width, (b - a) * track.Height));
        }
    }

    private static Color LevelColor(double db) => db > -3 ? Red : db > -12 ? Yellow : Green;

    private static double ToDb(double amplitude) => amplitude > 1e-6 ? 20 * Math.Log10(amplitude) : -120;
}
