using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TidalStudio.Core.Synth;

namespace TidalStudio.App.Synth;

/// <summary>Draws an oscillator's wavetable: the current frame bright, neighbouring frames stacked behind it.</summary>
public sealed class WavetableView : Control
{
    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#12141A"));
    private readonly Func<OscSettings> _osc;
    private readonly Color _accent;

    public WavetableView(Func<OscSettings> osc, Color accent)
    {
        _osc = osc;
        _accent = accent;
        Height = 74;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(Back, b, 4);
        var osc = _osc();
        var table = Wavetables.Get(osc.Table);
        double alpha = osc.Enabled ? 1 : 0.35;
        int frames = table.FrameCount;
        int current = (int)Math.Round(Math.Clamp(osc.Position, 0, 1) * (frames - 1));

        // A few frames on either side, offset diagonally, for a sense of the whole table.
        const int depth = 5;
        for (int d = depth; d >= -depth; d--)
        {
            int f = current + d * Math.Max(1, frames / 12);
            if (f < 0 || f >= frames) continue;
            double ox = d * 6, oy = -d * 3;
            double a = d == 0 ? 1 : 0.18 * (1 - Math.Abs(d) / (double)(depth + 1));
            DrawFrame(dc, table.Frame(f), new Rect(b.X + 14 + ox, b.Y + 12 + oy, b.Width - 28, b.Height - 24),
                new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * a * alpha), _accent.R, _accent.G, _accent.B)), d == 0 ? 1.8 : 1));
        }
        var name = new FormattedText($"{table.Name}  {current + 1}/{frames}", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 9.5, new SolidColorBrush(Color.FromArgb(150, 200, 208, 224)));
        dc.DrawText(name, new Point(b.Right - name.Width - 5, b.Bottom - name.Height - 2));
    }

    private static void DrawFrame(DrawingContext dc, float[] frame, Rect area, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int points = Math.Max(2, (int)area.Width);
            for (int x = 0; x < points; x++)
            {
                float v = frame[(int)((long)x * frame.Length / points)];
                var p = new Point(area.X + x, area.Center.Y - v * area.Height / 2);
                if (x == 0) ctx.BeginFigure(p, false); else ctx.LineTo(p);
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geo);
    }
}

/// <summary>The shape of an ADSR envelope for a given hold time.</summary>
public sealed class EnvelopeView : Control
{
    private readonly Func<EnvelopeSettings> _env;
    private readonly Func<double> _hold;
    private readonly Color _accent;

    public EnvelopeView(Func<EnvelopeSettings> env, Func<double> hold, Color accent)
    {
        _env = env;
        _hold = hold;
        _accent = accent;
        Height = 46;
    }

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(new SolidColorBrush(Color.Parse("#12141A")), b, 4);
        var env = _env();
        double hold = _hold();
        double total = hold + Math.Max(env.Release, 0.05);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int n = Math.Max(2, (int)b.Width - 8);
            for (int i = 0; i <= n; i++)
            {
                double t = total * i / n;
                double v = Core.Synth.SynthEngine.Envelope(env, t, hold);
                var p = new Point(4 + i, b.Bottom - 4 - v * (b.Height - 10));
                if (i == 0) ctx.BeginFigure(new Point(4, b.Bottom - 4), true);
                ctx.LineTo(p);
            }
            ctx.LineTo(new Point(4 + n, b.Bottom - 4));
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, _accent.R, _accent.G, _accent.B)), new Pen(new SolidColorBrush(_accent), 1.5), geo);
        double holdX = 4 + (b.Width - 8) * hold / total;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1, new DashStyle(new double[] { 2, 3 }, 0)),
            new Point(holdX, 4), new Point(holdX, b.Bottom - 4));
    }
}

/// <summary>The last rendered sample (both channels overlaid).</summary>
public sealed class WaveformView : Control
{
    public RenderedNote? Note { get; set; }

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(new SolidColorBrush(Color.Parse("#12141A")), b, 4);
        var note = Note;
        var labelBrush = new SolidColorBrush(Color.FromArgb(150, 200, 208, 224));
        if (note is null || note.Left.Length == 0)
        {
            var hint = new FormattedText("Preview (Space) or click a key to hear the sound", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Inter"), 11, labelBrush);
            dc.DrawText(hint, new Point((b.Width - hint.Width) / 2, (b.Height - hint.Height) / 2));
            return;
        }
        int width = Math.Max(1, (int)b.Width);
        double mid = b.Center.Y, h = b.Height / 2 - 4;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#4ECDC4")), 1);
        for (int x = 0; x < width; x++)
        {
            int i0 = (int)((long)x * note.Left.Length / width), i1 = Math.Max(i0 + 1, (int)((long)(x + 1) * note.Left.Length / width));
            float lo = 0, hi = 0;
            for (int i = i0; i < i1 && i < note.Left.Length; i++)
            {
                float v = (note.Left[i] + note.Right[i]) / 2;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            dc.DrawLine(pen, new Point(x + 0.5, mid - hi * h), new Point(x + 0.5, mid - lo * h + 0.5));
        }
        var label = new FormattedText($"{SynthPatch.NoteName(note.Midi)}  ·  {note.Seconds:0.00} s", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 10, labelBrush);
        dc.DrawText(label, new Point(6, 4));
    }
}

/// <summary>Two-octave keyboard: click a key to preview that note. Notes that will be saved get a dot.</summary>
public sealed class KeyboardView : Control
{
    private static readonly bool[] IsBlack = { false, true, false, true, false, false, true, false, true, false, true, false };

    public KeyboardView()
    {
        Height = 70;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>MIDI note of the leftmost C.</summary>
    public int LowestC { get; set; } = 48;
    public IReadOnlyCollection<int> Marked { get; set; } = Array.Empty<int>();
    public int? Pressed { get; set; }

    public event Action<int>? NotePressed;

    private const int Octaves = 2;

    private IEnumerable<(int Midi, Rect Rect, bool Black)> Keys()
    {
        double whiteW = Bounds.Width / (7 * Octaves + 1);
        int white = 0;
        var blacks = new List<(int, Rect, bool)>();
        for (int i = 0; i <= 12 * Octaves; i++)
        {
            int midi = LowestC + i;
            if (IsBlack[i % 12])
                blacks.Add((midi, new Rect(white * whiteW - whiteW * 0.3, 0, whiteW * 0.6, Bounds.Height * 0.6), true));
            else
            {
                yield return (midi, new Rect(white * whiteW, 0, whiteW - 1, Bounds.Height), false);
                white++;
            }
        }
        foreach (var k in blacks) yield return k;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        // Black keys are on top, so check them first.
        var hit = Keys().Reverse().FirstOrDefault(k => k.Rect.Contains(p));
        if (hit.Rect.Width > 0)
        {
            Pressed = hit.Midi;
            InvalidateVisual();
            NotePressed?.Invoke(hit.Midi);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Pressed = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        var accent = Color.Parse("#4ECDC4");
        foreach (var (midi, rect, black) in Keys())
        {
            bool pressed = Pressed == midi;
            var fill = pressed ? new SolidColorBrush(accent) : new SolidColorBrush(black ? Color.Parse("#1B1E25") : Color.Parse("#C9CFDA"));
            dc.FillRectangle(fill, rect, black ? 2 : 3);
            if (Marked.Contains(midi))
                dc.DrawEllipse(new SolidColorBrush(black ? accent : Color.Parse("#2A9D95")), null,
                    new Point(rect.Center.X, rect.Bottom - (black ? 8 : 10)), 3, 3);
            if (!black && midi % 12 == 0)
            {
                var ft = new FormattedText(SynthPatch.NoteName(midi), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Inter"), 9, new SolidColorBrush(Color.Parse("#5C6475")));
                dc.DrawText(ft, new Point(rect.X + 3, rect.Bottom - 26));
            }
        }
    }
}
