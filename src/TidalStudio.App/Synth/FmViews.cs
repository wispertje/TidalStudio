using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TidalStudio.Core.Synth;

namespace TidalStudio.App.Synth;

/// <summary>
/// Diagram of the current FM algorithm: operators as boxes, modulators above the operator they modulate, carriers
/// on the bottom row feeding the output bar. Operator 6's feedback loop is drawn as a hook. Click an operator to
/// jump to its controls.
/// </summary>
public sealed class AlgorithmView : Control
{
    public static readonly Color[] OpColors =
    {
        Color.Parse("#4ECDC4"), Color.Parse("#60A5FA"), Color.Parse("#A78BFA"),
        Color.Parse("#F472B6"), Color.Parse("#FFD166"), Color.Parse("#FF8A5B"),
    };

    private const double BoxW = 40, BoxH = 26, ColW = 56, RowH = 40;

    private readonly Func<FmPatch> _patch;
    private readonly Dictionary<int, Rect> _boxes = new();
    private int? _hover;

    public AlgorithmView(Func<FmPatch> patch)
    {
        _patch = patch;
        Height = 250;
        MinWidth = 220;
        ClipToBounds = true;
    }

    /// <summary>Operator 1..6 was clicked.</summary>
    public event Action<int>? OperatorClicked;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int? hit = HitTest(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitTest(e.GetPosition(this)) is { } op) OperatorClicked?.Invoke(op);
    }

    private int? HitTest(Point p)
    {
        foreach (var (op, rect) in _boxes)
            if (rect.Contains(p)) return op;
        return null;
    }

    /// <summary>Column and depth (0 = carrier row) of every operator.</summary>
    internal static Dictionary<int, (int Col, int Depth)> Layout(FmAlgorithm algorithm)
    {
        var pos = new Dictionary<int, (int Col, int Depth)>();
        int nextCol = 0;
        void Place(int op, int col, int depth)
        {
            if (pos.ContainsKey(op)) return;
            pos[op] = (col, depth);
            bool first = true;
            foreach (int m in algorithm.ModulatorsOf(op).OrderByDescending(m => m))
            {
                if (pos.ContainsKey(m)) continue;
                if (first) { Place(m, col, depth + 1); first = false; }
                else { nextCol++; Place(m, nextCol, depth + 1); }
            }
        }
        foreach (int c in algorithm.Carriers)
        {
            Place(c, nextCol, 0);
            nextCol++;
        }
        return pos;
    }

    public override void Render(DrawingContext dc)
    {
        var b = new Rect(Bounds.Size);
        dc.FillRectangle(SignalFlowView.Back, b, 6);
        var patch = _patch();
        var algorithm = patch.CurrentAlgorithm;
        var layout = Layout(algorithm);
        int cols = layout.Values.Max(p => p.Col) + 1;
        int rows = layout.Values.Max(p => p.Depth) + 1;

        double outY = b.Height - 22;
        double gridW = (cols - 1) * ColW + BoxW;
        double left = Math.Max(10, (b.Width - gridW) / 2);
        double bottomBoxY = outY - 18 - BoxH;
        double rowH = rows > 1 ? Math.Min(RowH, (bottomBoxY - 14) / (rows - 1)) : RowH;

        _boxes.Clear();
        foreach (var (op, (col, depth)) in layout)
            _boxes[op] = new Rect(left + col * ColW, bottomBoxY - depth * rowH, BoxW, BoxH);

        var wire = new Pen(new SolidColorBrush(SignalFlowView.AudioWire), 1.4);
        var fadedWire = new Pen(new SolidColorBrush(Color.FromArgb(70, 201, 207, 218)), 1.2, new DashStyle(new double[] { 3, 3 }, 0));
        bool On(int op) => patch.Operators[op - 1] is { Enabled: true, Level: > 0 };

        // Modulation wires: from the bottom of a modulator to the top of its target.
        foreach (var (from, to) in algorithm.Links)
        {
            var a = _boxes[from];
            var t = _boxes[to];
            var start = new Point(a.Center.X, a.Bottom);
            var end = new Point(t.Center.X, t.Top);
            var pen = On(from) && On(to) ? wire : fadedWire;
            if (Math.Abs(start.X - end.X) < 0.5) dc.DrawLine(pen, start, end);
            else
            {
                double midY = end.Y - 7;
                dc.DrawLine(pen, start, new Point(start.X, midY));
                dc.DrawLine(pen, new Point(start.X, midY), new Point(end.X, midY));
                dc.DrawLine(pen, new Point(end.X, midY), end);
            }
        }

        // Carriers into the output bar.
        var carriers = algorithm.Carriers;
        double barL = carriers.Min(c => _boxes[c].Center.X) - 12, barR = carriers.Max(c => _boxes[c].Center.X) + 12;
        foreach (int c in carriers)
            dc.DrawLine(On(c) ? wire : fadedWire, new Point(_boxes[c].Center.X, _boxes[c].Bottom), new Point(_boxes[c].Center.X, outY));
        dc.FillRectangle(new SolidColorBrush(SignalFlowView.AudioWire), new Rect(barL, outY, barR - barL, 3), 1.5f);
        SignalFlowView.DrawCentered(dc, "OUT", new Point((barL + barR) / 2, outY + 12), 10, SignalFlowView.FontBold, SignalFlowView.AudioWire);

        // Feedback hook on operator 6.
        if (_boxes.TryGetValue(6, out var six) && patch.Feedback > 0)
        {
            var fbPen = new Pen(new SolidColorBrush(OpColors[5]), 1.4);
            double x = six.Right + 6;
            dc.DrawLine(fbPen, new Point(six.Right, six.Center.Y + 6), new Point(x, six.Center.Y + 6));
            dc.DrawLine(fbPen, new Point(x, six.Center.Y + 6), new Point(x, six.Top - 5));
            dc.DrawLine(fbPen, new Point(x, six.Top - 5), new Point(six.Center.X, six.Top - 5));
            dc.DrawLine(fbPen, new Point(six.Center.X, six.Top - 5), new Point(six.Center.X, six.Top));
        }

        // Operator boxes.
        foreach (var (op, rect) in _boxes)
        {
            var color = OpColors[op - 1];
            bool on = On(op);
            bool carrier = algorithm.IsCarrier(op);
            var fill = carrier && on ? new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)) : SignalFlowView.BoxFill;
            var border = new Pen(new SolidColorBrush(on ? color : SignalFlowView.Faded(color)), _hover == op ? 2.4 : 1.4,
                on ? null : new DashStyle(new double[] { 3, 2 }, 0));
            dc.DrawRectangle(fill, border, rect, 5, 5);
            SignalFlowView.DrawCentered(dc, op.ToString(), rect.Center, 13, SignalFlowView.FontBold, on ? color : SignalFlowView.Faded(color));
        }

        SignalFlowView.DrawText(dc, algorithm.Name, new Point(8, 6), 11, SignalFlowView.FontBold, SignalFlowView.AudioWire);
        SignalFlowView.DrawText(dc, "filled = carrier (heard)", new Point(8, 21), 10, SignalFlowView.Font, Color.Parse("#8A93A6"));
    }
}
