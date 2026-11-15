using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;
using TidalStudio.Core.Audio;

namespace TidalStudio.App.Visualizers;

/// <summary>Spectrum analyser of the captured audio: FFT magnitude in dB over a (log) frequency axis.</summary>
public sealed class SpectrumVisualizer : VisualizerBase
{
    private float[] _samples = Array.Empty<float>();
    private float[] _db = Array.Empty<float>();
    private float[] _smoothed = Array.Empty<float>();
    private float[] _peaks = Array.Empty<float>();
    private double _lastPeakDecay;

    public SpectrumVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "spectrum";
    public override string Title => "Spectrum";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Choice("fft", "FFT size", "4096", "1024", "2048", "4096", "8192"),
        VisOption.Number("minDb", "Min dB", -90, -140, -20, 5),
        VisOption.Number("maxDb", "Max dB", 0, -40, 20, 5),
        VisOption.Number("smoothing", "Smoothing", 0.7, 0, 0.98, 0.02),
        VisOption.Bool("log", "Log frequency axis", true),
        VisOption.Number("minFreq", "Min frequency (Hz)", 20, 1, 1000, 1),
        VisOption.Number("maxFreq", "Max frequency (Hz)", 20000, 1000, 24000, 100),
        VisOption.Choice("style", "Style", "fill", "fill", "line", "bars"),
        VisOption.Bool("peaks", "Peak hold", true),
        VisOption.Bool("grid", "Grid & labels", true),
        VisOption.Color("color", "Colour", "#A78BFA"),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var o = Options;
        double minDb = o.Number("minDb"), maxDb = Math.Max(o.Number("maxDb"), minDb + 10);
        double minF = o.Number("minFreq"), maxF = Math.Max(o.Number("maxFreq"), minF * 2);
        bool log = o.Bool("log");
        var audio = Services.Audio;
        maxF = Math.Min(maxF, audio.SampleRate / 2.0);

        double XOf(double f) => log
            ? Math.Log(f / minF) / Math.Log(maxF / minF) * bounds.Width
            : (f - minF) / (maxF - minF) * bounds.Width;
        double YOf(double db) => bounds.Height - Math.Clamp((db - minDb) / (maxDb - minDb), 0, 1) * bounds.Height;

        if (o.Bool("grid"))
        {
            var gridPen = Pen(Color.FromArgb(25, 255, 255, 255), 1);
            foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
            {
                if (f < minF || f > maxF) continue;
                double x = XOf(f);
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
                DrawText(dc, f >= 1000 ? $"{f / 1000:0}k" : f.ToString(CultureInfo.InvariantCulture), new Point(x + 2, bounds.Height - 14), 9, DimLabelBrush);
            }
            for (double db = Math.Ceiling(maxDb / 20) * 20; db >= minDb; db -= 20)
            {
                double y = YOf(db);
                dc.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
                DrawText(dc, $"{db:0} dB", new Point(2, y + 1), 9, DimLabelBrush);
            }
        }

        if (!Services.AudioCapture.IsRunning)
        {
            DrawIdleHint(dc, bounds, Services.AudioCapture.Error is { } err ? $"Audio capture failed: {err}" : "Audio capture is off (View ▸ Audio capture)");
            return;
        }

        int size = int.TryParse(o.Choice("fft"), out int s) ? s : 4096;
        if (_samples.Length != size)
        {
            _samples = new float[size];
            _db = new float[size / 2];
            _smoothed = Enumerable.Repeat(-140f, size / 2).ToArray();
            _peaks = Enumerable.Repeat(-140f, size / 2).ToArray();
        }
        audio.ReadLatest(_samples);
        Fft.MagnitudeDb(_samples, _db, -140f);

        float smooth = (float)o.Number("smoothing");
        double decayDt = Math.Clamp(now - _lastPeakDecay, 0, 0.5);
        _lastPeakDecay = now;
        for (int k = 0; k < _db.Length; k++)
        {
            // Fast attack, smoothed release.
            _smoothed[k] = _db[k] > _smoothed[k] ? _db[k] : _smoothed[k] * smooth + _db[k] * (1 - smooth);
            _peaks[k] = Math.Max(_smoothed[k], _peaks[k] - (float)(decayDt * 20)); // 20 dB/s fall
        }

        int width = Math.Max(2, (int)bounds.Width);
        var heights = new double[width];
        var peakHeights = new double[width];
        for (int x = 0; x < width; x++)
        {
            double f0 = FreqAt(x, width, minF, maxF, log), f1 = FreqAt(x + 1, width, minF, maxF, log);
            int b0 = Math.Clamp((int)Math.Floor(f0 * size / audio.SampleRate), 1, _db.Length - 1);
            int b1 = Math.Clamp((int)Math.Ceiling(f1 * size / audio.SampleRate), b0 + 1, _db.Length);
            float best = -140, bestPeak = -140;
            for (int b = b0; b < b1; b++) { best = Math.Max(best, _smoothed[b]); bestPeak = Math.Max(bestPeak, _peaks[b]); }
            heights[x] = YOf(best);
            peakHeights[x] = YOf(bestPeak);
        }

        TryParseColor(o.Get("color"), out var color);
        string style = o.Choice("style");
        if (style == "bars")
        {
            int barW = Math.Max(3, width / 64);
            for (int x = 0; x < width; x += barW)
            {
                double y = heights.Skip(x).Take(barW).Min();
                double frac = 1 - y / bounds.Height;
                dc.FillRectangle(Brush(WithAlpha(Mix(color, Colors.White, frac * 0.3), 0.85)), new Rect(x, y, barW - 1, bounds.Height - y));
            }
        }
        else
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, style == "fill" ? bounds.Height : heights[0]), style == "fill");
                for (int x = 0; x < width; x++) ctx.LineTo(new Point(x, heights[x]));
                if (style == "fill") ctx.LineTo(new Point(width - 1, bounds.Height));
                ctx.EndFigure(style == "fill");
            }
            dc.DrawGeometry(style == "fill" ? Brush(WithAlpha(color, 0.35)) : null, Pen(color, 1.5), geo);
        }

        if (o.Bool("peaks"))
        {
            var peakPen = Pen(Color.FromArgb(150, 255, 255, 255), 1);
            for (int x = 0; x < width - 1; x += 2)
                dc.DrawLine(peakPen, new Point(x, peakHeights[x]), new Point(x + 1, peakHeights[x]));
        }
    }

    private static double FreqAt(int x, int width, double minF, double maxF, bool log) =>
        log ? minF * Math.Pow(maxF / minF, x / (double)width) : minF + (maxF - minF) * x / width;
}
