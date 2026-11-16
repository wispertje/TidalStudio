using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TidalStudio.App.Synth;

/// <summary>
/// A rotary knob: drag up/down (Shift = fine), mouse wheel, double-click resets. Shows its value while in use.
/// Values can map logarithmically (frequencies) or with a curve (times) so the useful range gets most of the travel.
/// </summary>
public sealed class Knob : Control
{
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#2A2F3A"));
    private static readonly IBrush FaceBrush = new SolidColorBrush(Color.Parse("#1F232C"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush ValueBrush = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly Typeface Face = new("Inter");

    private double _value;
    private bool _dragging, _hover;
    private double _dragStartY, _dragStartNorm;

    public Knob()
    {
        Width = 50;
        Height = 60;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    public string Label { get; init; } = "";
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public double Default { get; init; }
    /// <summary>Logarithmic mapping (Minimum must be &gt; 0).</summary>
    public bool Log { get; init; }
    /// <summary>Exponent for non-log mapping; &gt; 1 gives more travel to small values.</summary>
    public double Curve { get; init; } = 1;
    /// <summary>Snap to multiples of this (e.g. 1 for octaves); 0 = continuous.</summary>
    public double Step { get; init; }
    /// <summary>Knobs with a centre (pan, fine) draw their arc from the middle.</summary>
    public bool Bipolar { get; init; }
    public Func<double, string> Format { get; init; } = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public Color Accent { get; init; } = Color.Parse("#4ECDC4");

    /// <summary>Raised while the value changes.</summary>
    public event Action<double>? ValueChanged;
    /// <summary>Raised when a gesture ends (mouse released, wheel, reset): a good moment to re-render.</summary>
    public event Action? Committed;

    public double Value
    {
        get => _value;
        set
        {
            double v = Snap(Math.Clamp(value, Minimum, Maximum));
            if (v.Equals(_value)) return;
            _value = v;
            InvalidateVisual();
        }
    }

    private double Snap(double v) => Step > 0 ? Math.Round(v / Step) * Step : v;

    private double ToNorm(double v)
    {
        if (Maximum <= Minimum) return 0;
        if (Log && Minimum > 0) return Math.Log(v / Minimum) / Math.Log(Maximum / Minimum);
        return Math.Pow((v - Minimum) / (Maximum - Minimum), 1 / Curve);
    }

    private double FromNorm(double n)
    {
        n = Math.Clamp(n, 0, 1);
        if (Log && Minimum > 0) return Minimum * Math.Pow(Maximum / Minimum, n);
        return Minimum + (Maximum - Minimum) * Math.Pow(n, Curve);
    }

    private void SetFromUser(double v)
    {
        double before = _value;
        Value = v;
        if (!_value.Equals(before)) ValueChanged?.Invoke(_value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            SetFromUser(Default);
            Committed?.Invoke();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartNorm = ToNorm(_value);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        double sensitivity = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 900 : 180;
        double dy = _dragStartY - e.GetPosition(this).Y;
        SetFromUser(FromNorm(_dragStartNorm + dy / sensitivity));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Committed?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double step = Step > 0 ? Step * Math.Sign(e.Delta.Y) : 0;
        if (step != 0) SetFromUser(_value + step);
        else SetFromUser(FromNorm(ToNorm(_value) + e.Delta.Y * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.005 : 0.025)));
        Committed?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); _hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = false; InvalidateVisual(); }

    public override void Render(DrawingContext dc)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height - 16);
        var c = new Point(Bounds.Width / 2, size / 2 + 2);
        double r = size / 2 - 4;
        const double start = 135, sweep = 270; // degrees, clockwise from 3 o'clock

        dc.DrawEllipse(FaceBrush, null, c, r - 4, r - 4);
        DrawArc(dc, c, r, start, sweep, new Pen(TrackBrush, 3.5, lineCap: PenLineCap.Round));
        double n = ToNorm(_value);
        double from = Bipolar ? 0.5 : 0;
        double a0 = Math.Min(from, n), a1 = Math.Max(from, n);
        if (a1 - a0 > 0.002)
            DrawArc(dc, c, r, start + sweep * a0, sweep * (a1 - a0), new Pen(new SolidColorBrush(Accent), 3.5, lineCap: PenLineCap.Round));
        double angle = (start + sweep * n) * Math.PI / 180;
        dc.DrawLine(new Pen(ValueBrush, 2, lineCap: PenLineCap.Round),
            new Point(c.X + Math.Cos(angle) * (r - 12), c.Y + Math.Sin(angle) * (r - 12)),
            new Point(c.X + Math.Cos(angle) * (r - 5), c.Y + Math.Sin(angle) * (r - 5)));

        string text = _dragging || _hover ? Format(_value) : Label;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5,
            _dragging || _hover ? ValueBrush : LabelBrush);
        dc.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, Bounds.Height - ft.Height));
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
            ctx.BeginFigure(new Point(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r), false);
            ctx.ArcTo(new Point(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geo);
    }
}
