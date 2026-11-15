using Avalonia;
using Avalonia.Media;
using TidalStudio.App.Services;

namespace TidalStudio.App.Visualizers;

/// <summary>Time-domain oscilloscope of the captured audio, with optional rising-edge trigger for a stable picture.</summary>
public sealed class ScopeVisualizer : VisualizerBase
{
    private float[] _samples = Array.Empty<float>();

    public ScopeVisualizer(AppServices services) : base(services) { }

    public override string TypeName => "scope";
    public override string Title => "Scope";

    public override IReadOnlyList<VisOption> DefineOptions() => new[]
    {
        VisOption.Number("window", "Window (ms)", 30, 2, 250, 1),
        VisOption.Number("scale", "Scale", 1, 0.25, 12, 0.25),
        VisOption.Number("thickness", "Thickness", 1.5, 0.5, 6, 0.5),
        VisOption.Bool("trigger", "Trigger on rising edge", true),
        VisOption.Number("triggerLevel", "Trigger level", 0, -0.5, 0.5, 0.01),
        VisOption.Bool("centerLine", "Centre line", true),
        VisOption.Color("color", "Colour", "#4ECDC4"),
    };

    protected override void RenderVisual(DrawingContext dc, Rect bounds, double now)
    {
        var o = Options;
        double midY = bounds.Center.Y;
        if (o.Bool("centerLine"))
            dc.DrawLine(Pen(Color.FromArgb(35, 255, 255, 255), 1), new Point(0, midY), new Point(bounds.Width, midY));

        if (!Services.AudioCapture.IsRunning)
        {
            DrawIdleHint(dc, bounds, Services.AudioCapture.Error is { } err ? $"Audio capture failed: {err}" : "Audio capture is off (View ▸ Audio capture)");
            return;
        }

        var audio = Services.Audio;
        int n = Math.Clamp((int)(o.Number("window") / 1000 * audio.SampleRate), 32, audio.Capacity / 4);
        int total = n * 2;
        if (_samples.Length != total) _samples = new float[total];
        audio.ReadLatest(_samples);

        int start = n; // default: the most recent n samples
        if (o.Bool("trigger"))
        {
            float level = (float)o.Number("triggerLevel");
            for (int i = n - 1; i > 0; i--)
            {
                if (_samples[i - 1] < level && _samples[i] >= level) { start = i; break; }
            }
            if (start + n > total) start = total - n;
        }

        TryParseColor(o.Get("color"), out var color);
        double scale = o.Number("scale") * bounds.Height / 2 * 0.9;
        int width = Math.Max(1, (int)bounds.Width);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (n <= width * 2)
            {
                for (int i = 0; i < n; i++)
                {
                    var p = new Point(i * bounds.Width / (n - 1), midY - Math.Clamp(_samples[start + i] * scale, -midY, midY));
                    if (i == 0) ctx.BeginFigure(p, false); else ctx.LineTo(p);
                }
            }
            else
            {
                // Dense: min/max per pixel column so peaks are not lost.
                bool begun = false;
                for (int x = 0; x < width; x++)
                {
                    int a = start + (int)((long)x * n / width);
                    int b = start + (int)((long)(x + 1) * n / width);
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = a; i < Math.Max(a + 1, b); i++) { lo = Math.Min(lo, _samples[i]); hi = Math.Max(hi, _samples[i]); }
                    var p1 = new Point(x, midY - Math.Clamp(hi * scale, -midY, midY));
                    var p2 = new Point(x, midY - Math.Clamp(lo * scale, -midY, midY));
                    if (!begun) { ctx.BeginFigure(p1, false); begun = true; } else ctx.LineTo(p1);
                    ctx.LineTo(p2);
                }
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(Brush(color), o.Number("thickness"), lineJoin: PenLineJoin.Round), geo);
    }
}
