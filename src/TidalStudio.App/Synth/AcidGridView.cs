using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TidalStudio.Core.Synth;

namespace TidalStudio.App.Synth;

/// <summary>
/// The step editor of Tidepool Acid: a 13-note piano roll (root up to its octave) and rows for octave, accent and slide.
/// Click a cell to place a note (click it again for a rest), drag to paint; right-click makes a rest.
/// </summary>
public sealed class AcidGridView : Control
{
    internal static readonly Color NoteColor = Color.Parse("#B5E853");
    internal static readonly Color AccentColor = Color.Parse("#FFD166");
    internal static readonly Color SlideColor = Color.Parse("#4ECDC4");
    internal static readonly Color OctaveColor = Color.Parse("#A78BFA");

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#12141A"));
    private static readonly IBrush WhiteRow = new SolidColorBrush(Color.Parse("#1B1F27"));
    private static readonly IBrush BlackRow = new SolidColorBrush(Color.Parse("#15181E"));
    private static readonly IBrush BeatTint = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
    private static readonly IBrush OutsideTint = new SolidColorBrush(Color.FromArgb(150, 18, 20, 26));
    private static readonly IBrush PlayTint = new SolidColorBrush(Color.FromArgb(46, 181, 232, 83));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush RootLabelBrush = new SolidColorBrush(Color.Parse("#C9D1E0"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#232833")), 1);
    private static readonly Pen BarPen = new(new SolidColorBrush(Color.Parse("#3A4150")), 1);
    private static readonly Typeface Font = new("Inter");

    private const double LabelWidth = 46, HeaderHeight = 15, RowHeight = 17, Gap = 7, FlagHeight = 19;
    private const int NoteRows = 13;

    private readonly Func<AcidBox> _box;
    private readonly Func<AcidPattern> _pattern;

    // Dragging: what the first click did, repeated on every step the pointer moves over.
    private Action<AcidStep>? _paint;
    private int _lastColumn = -1;
    private bool _edited;

    /// <summary>Raised when the pointer is released after an edit (time to preview).</summary>
    public event Action? Committed;
    /// <summary>Raised on every edit.</summary>
    public event Action? Edited;

    /// <summary>The step being played (highlighted), or -1.</summary>
    public int PlayStep { get; set; } = -1;

    public AcidGridView(Func<AcidBox> box, Func<AcidPattern> pattern)
    {
        _box = box;
        _pattern = pattern;
        Height = HeaderHeight + NoteRows * RowHeight + Gap + 3 * FlagHeight + 2;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Click a cell for a note (again for a rest) and drag to paint. Right-click = rest.\n" +
            "OCT: upper half = octave up, lower half = octave down. ACC = accent, SLIDE = glide into the next note.");
    }

    private int Columns => _pattern().Length <= 16 ? 16 : AcidPattern.MaxSteps;
    private double ColumnWidth => Math.Max(8, (Bounds.Width - LabelWidth) / Columns);
    private double FlagsTop => HeaderHeight + NoteRows * RowHeight + Gap;

    private static bool IsBlackKey(int semitone) => ((semitone % 12) + 12) % 12 is 1 or 3 or 6 or 8 or 10;

    public override void Render(DrawingContext dc)
    {
        var bounds = new Rect(Bounds.Size);
        dc.FillRectangle(Back, bounds, 4);
        var box = _box();
        var p = _pattern();
        int columns = Columns;
        double cw = ColumnWidth;
        double gridRight = LabelWidth + columns * cw;

        // Note rows (top = octave, bottom = root).
        for (int row = 0; row < NoteRows; row++)
        {
            double y = RowY(row);
            var rect = new Rect(LabelWidth, y, gridRight - LabelWidth, RowHeight);
            dc.FillRectangle(IsBlackKey(row) ? BlackRow : WhiteRow, rect);
            dc.DrawLine(GridPen, new Point(LabelWidth, y + RowHeight), new Point(gridRight, y + RowHeight));
            bool root = row is 0 or 12;
            Label(dc, SynthPatch.NoteName(box.Root + row), new Point(6, y + 2), root ? RootLabelBrush : LabelBrush, root ? FontWeight.SemiBold : FontWeight.Normal);
        }
        // Flag rows.
        string[] flags = { "OCT", "ACC", "SLIDE" };
        for (int f = 0; f < 3; f++)
        {
            double y = FlagsTop + f * FlagHeight;
            dc.FillRectangle(WhiteRow, new Rect(LabelWidth, y, gridRight - LabelWidth, FlagHeight - 2));
            Label(dc, flags[f], new Point(6, y + 3), LabelBrush, FontWeight.SemiBold, 9.5);
        }

        // Columns: beat tint, step numbers, bar lines.
        for (int c = 0; c < columns; c++)
        {
            double x = LabelWidth + c * cw;
            if ((c / 4) % 2 == 1)
            {
                dc.FillRectangle(BeatTint, new Rect(x, HeaderHeight, cw, NoteRows * RowHeight));
                dc.FillRectangle(BeatTint, new Rect(x, FlagsTop, cw, 3 * FlagHeight - 2));
            }
            if (c == PlayStep)
            {
                dc.FillRectangle(PlayTint, new Rect(x, 0, cw, Bounds.Height));
            }
            if (c % 4 == 0 || cw >= 22)
                Label(dc, (c + 1).ToString(CultureInfo.InvariantCulture), new Point(x + 3, 1), c % 4 == 0 ? RootLabelBrush : LabelBrush, FontWeight.Normal, 9);
            var pen = c % 4 == 0 ? BarPen : GridPen;
            dc.DrawLine(pen, new Point(x, HeaderHeight), new Point(x, HeaderHeight + NoteRows * RowHeight));
            dc.DrawLine(pen, new Point(x, FlagsTop), new Point(x, FlagsTop + 3 * FlagHeight - 2));
        }

        // Notes, slides and flags.
        for (int c = 0; c < columns; c++)
        {
            var s = p.Steps[c];
            double x = LabelWidth + c * cw;
            bool inside = c < p.Length;
            byte alpha = (byte)(inside ? 255 : 90);
            if (s.Gate)
            {
                var color = s.Accent ? AccentColor : NoteColor;
                double y = RowY(s.Note);
                var cell = new Rect(x + 2, y + 2, cw - 4, RowHeight - 4);
                dc.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), cell, 3);
                if (s.Octave != 0)
                {
                    // A small arrow inside the note for its octave.
                    Triangle(dc, new Point(cell.Right - 6, cell.Center.Y), s.Octave > 0, 3.5, new SolidColorBrush(Color.FromArgb(alpha, 18, 20, 26)));
                }
                // Slide: a wire to the next note.
                int nextIndex = (c + 1) % p.Length;
                if (s.Slide && inside && p.Steps[nextIndex].Gate && c + 1 < p.Length)
                {
                    var next = p.Steps[nextIndex];
                    var from = new Point(cell.Right, cell.Center.Y);
                    var to = new Point(LabelWidth + (c + 1) * cw + 2, RowY(next.Note) + RowHeight / 2);
                    dc.DrawLine(new Pen(new SolidColorBrush(SlideColor), 2.2), from, to);
                }
            }

            // OCT row
            double oy = FlagsTop;
            if (s.Octave != 0)
                Triangle(dc, new Point(x + cw / 2, oy + (FlagHeight - 2) / 2), s.Octave > 0, 5,
                    new SolidColorBrush(Color.FromArgb(alpha, OctaveColor.R, OctaveColor.G, OctaveColor.B)));
            // ACC row
            if (s.Accent)
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, AccentColor.R, AccentColor.G, AccentColor.B)), null,
                    new Point(x + cw / 2, FlagsTop + FlagHeight + (FlagHeight - 2) / 2), 4.5, 4.5);
            // SLIDE row
            if (s.Slide)
                dc.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, SlideColor.R, SlideColor.G, SlideColor.B)),
                    new Rect(x + 3, FlagsTop + 2 * FlagHeight + (FlagHeight - 2) / 2 - 2.5, cw - 6 + (c + 1 < p.Length ? 6 : 0), 5), 2);
        }

        // Steps beyond the pattern's length are dimmed.
        if (p.Length < columns)
        {
            double x = LabelWidth + p.Length * cw;
            dc.FillRectangle(OutsideTint, new Rect(x, HeaderHeight, gridRight - x, Bounds.Height - HeaderHeight));
        }
    }

    private static double RowY(int note) => HeaderHeight + (NoteRows - 1 - note) * RowHeight;

    private static void Triangle(DrawingContext dc, Point center, bool up, double size, IBrush brush)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double d = up ? -1 : 1;
            ctx.BeginFigure(new Point(center.X, center.Y + d * size), true);
            ctx.LineTo(new Point(center.X - size, center.Y - d * size * 0.7));
            ctx.LineTo(new Point(center.X + size, center.Y - d * size * 0.7));
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(brush, null, geo);
    }

    private static void Label(DrawingContext dc, string text, Point at, IBrush brush, FontWeight weight, double size = 10)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
        dc.DrawText(ft, at);
    }

    // ------------------------------------------------------------------ editing

    private (int Column, int Row, double RowFraction)? Hit(Point point)
    {
        if (point.X < LabelWidth) return null;
        int column = (int)((point.X - LabelWidth) / ColumnWidth);
        if (column < 0 || column >= Columns) return null;
        if (point.Y >= HeaderHeight && point.Y < HeaderHeight + NoteRows * RowHeight)
            return (column, NoteRows - 1 - (int)((point.Y - HeaderHeight) / RowHeight), 0);
        if (point.Y >= FlagsTop && point.Y < FlagsTop + 3 * FlagHeight)
        {
            double inRow = (point.Y - FlagsTop) / FlagHeight;
            int flag = (int)inRow;
            return (column, -1 - flag, inRow - flag); // -1 = OCT, -2 = ACC, -3 = SLIDE
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var hit = Hit(e.GetPosition(this));
        if (hit is not { } h) return;
        var p = _pattern();
        if (h.Column >= p.Length) return;
        var s = p.Steps[h.Column];
        bool right = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;

        if (h.Row >= 0)
        {
            int note = h.Row;
            if (right || (s.Gate && s.Note == note)) _paint = step => step.Gate = false;
            else { _paint = step => { step.Gate = true; step.Note = note; }; _noteDrag = true; }
        }
        else if (h.Row == -1)
        {
            int target = h.RowFraction < 0.5 ? 1 : -1;
            int value = right || s.Octave == target ? 0 : target;
            _paint = step => step.Octave = value;
        }
        else
        {
            bool accent = h.Row == -2;
            bool value = !right && !(accent ? s.Accent : s.Slide);
            _paint = accent ? step => step.Accent = value : step => step.Slide = value;
        }
        _lastColumn = -1;
        Apply(h.Column);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_paint is null) return;
        var point = e.GetPosition(this);
        var hit = Hit(new Point(point.X, Math.Clamp(point.Y, HeaderHeight, Bounds.Height - 1)));
        if (hit is not { } h) return;
        // Painting notes follows the pointer's row (draw a line); erasing and flags stay as they started.
        if (_noteDrag && h.Row >= 0 && h.Column != _lastColumn)
        {
            int note = h.Row;
            _paint = step => { step.Gate = true; step.Note = note; };
        }
        Apply(h.Column);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_paint is null) return;
        _paint = null;
        _noteDrag = false;
        e.Pointer.Capture(null);
        if (_edited) Committed?.Invoke();
        _edited = false;
    }

    private void Apply(int column)
    {
        var p = _pattern();
        if (column == _lastColumn || column < 0 || column >= p.Length || _paint is null) return;
        _lastColumn = column;
        _paint(p.Steps[column]);
        _edited = true;
        Edited?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The drag started by placing a note (not erasing, not a flag row).</summary>
    private bool _noteDrag;
}
