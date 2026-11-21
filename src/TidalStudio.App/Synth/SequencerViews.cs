using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TidalStudio.Core.Synth;

namespace TidalStudio.App.Synth;

/// <summary>Colours and drawing helpers shared by the piano roll and the beat grid.</summary>
internal static class SeqPaint
{
    public static readonly IBrush Back = new SolidColorBrush(Color.Parse("#12141A"));
    public static readonly IBrush WhiteRow = new SolidColorBrush(Color.Parse("#1B1F27"));
    public static readonly IBrush BlackRow = new SolidColorBrush(Color.Parse("#15181E"));
    public static readonly IBrush ScaleRow = new SolidColorBrush(Color.Parse("#20252F"));
    public static readonly IBrush RootRow = new SolidColorBrush(Color.Parse("#26301F"));
    public static readonly IBrush BeatTint = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
    public static readonly IBrush OutsideTint = new SolidColorBrush(Color.FromArgb(160, 18, 20, 26));
    public static readonly IBrush PlayTint = new SolidColorBrush(Color.FromArgb(46, 181, 232, 83));
    public static readonly IBrush LabelBack = new SolidColorBrush(Color.Parse("#171A21"));
    public static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    public static readonly IBrush StrongLabelBrush = new SolidColorBrush(Color.Parse("#C9D1E0"));
    public static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#232833")), 1);
    public static readonly Pen BeatPen = new(new SolidColorBrush(Color.Parse("#2E3440")), 1);
    public static readonly Pen BarPen = new(new SolidColorBrush(Color.Parse("#4A5263")), 1);
    public static readonly Typeface Font = new("Inter");

    public static void Text(DrawingContext dc, string text, Point at, IBrush brush, double size = 10, FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
        dc.DrawText(ft, at);
    }

    public static bool IsBlackKey(int midi) => ((midi % 12) + 12) % 12 is 1 or 3 or 6 or 8 or 10;

    /// <summary>Columns shown: whole bars of 16, at least one.</summary>
    public static int Columns(int length) => Math.Max(16, (length + 15) / 16 * 16);
}

/// <summary>
/// A piano roll: pitch up, time to the right in sixteenths. Click to add a note and drag to set its length; drag a note to
/// move it, drag its right edge to resize; right-click (or double-click) deletes. Click a key on the left to hear it.
/// </summary>
public sealed class PianoRollView : Control
{
    public static readonly Color NoteColor = Color.Parse("#4ECDC4");
    private static readonly IBrush NoteBrush = new SolidColorBrush(NoteColor);
    private static readonly IBrush NoteSelectedBrush = new SolidColorBrush(Color.Parse("#8AF0E8"));
    private static readonly Pen NotePen = new(new SolidColorBrush(Color.Parse("#0E3B38")), 1);

    public const int LowestMidi = 24, HighestMidi = 108;
    public const double KeyWidth = 44, HeaderHeight = 16, RowHeight = 14;
    private const double ResizeZone = 7;

    private readonly Func<MelodySequence> _seq;

    private enum DragMode { None, Draw, Move, Resize, Erase }
    private DragMode _mode;
    private SeqNote? _active;
    private int _grabStep, _grabMidi, _origStep, _origMidi;
    private bool _changed;

    /// <summary>Raised before the first change of a gesture (time for an undo point).</summary>
    public event Action? BeforeEdit;
    /// <summary>Raised on every change.</summary>
    public event Action? Edited;
    /// <summary>Raised when a gesture that changed something ends.</summary>
    public event Action? Committed;
    /// <summary>Raised to play a single note (clicking a key, placing or moving a note).</summary>
    public event Action<int>? Audition;

    /// <summary>Length in steps new notes get (the last drawn length is remembered).</summary>
    public int DrawLength { get; set; } = 2;
    public int PlayStep { get; set; } = -1;

    public PianoRollView(Func<MelodySequence> seq)
    {
        _seq = seq;
        Height = HeaderHeight + (HighestMidi - LowestMidi + 1) * RowHeight;
        ClipToBounds = true;
        Focusable = true;
    }

    private MelodySequence Seq => _seq();
    private int Columns => SeqPaint.Columns(Seq.Length);
    private double ColumnWidth => Math.Max(10, (Bounds.Width - KeyWidth) / Columns);

    /// <summary>Top of the row of a MIDI note (high notes at the top).</summary>
    public static double RowY(int midi) => HeaderHeight + (HighestMidi - midi) * RowHeight;

    private (int Step, int Midi) Cell(Point p)
    {
        int step = (int)Math.Floor((p.X - KeyWidth) / ColumnWidth);
        int midi = HighestMidi - (int)Math.Floor((p.Y - HeaderHeight) / RowHeight);
        return (step, Math.Clamp(midi, LowestMidi, HighestMidi));
    }

    private SeqNote? NoteAt(Point p)
    {
        var (step, midi) = Cell(p);
        return Seq.Notes.LastOrDefault(n => n.Midi == midi && step >= n.Step && step < n.Step + n.Length);
    }

    private bool InResizeZone(SeqNote n, Point p) => p.X >= KeyWidth + (n.Step + n.Length) * ColumnWidth - ResizeZone;

    public override void Render(DrawingContext dc)
    {
        var seq = Seq;
        int columns = Columns;
        double cw = ColumnWidth;
        double right = KeyWidth + columns * cw;
        dc.FillRectangle(SeqPaint.Back, new Rect(Bounds.Size));
        var scale = AcidLines.Intervals(seq.Scale).ToHashSet();

        for (int midi = LowestMidi; midi <= HighestMidi; midi++)
        {
            double y = RowY(midi);
            int degree = ((midi - seq.Root) % 12 + 12) % 12;
            IBrush row = degree == 0 ? SeqPaint.RootRow : scale.Contains(degree) ? SeqPaint.ScaleRow
                : SeqPaint.IsBlackKey(midi) ? SeqPaint.BlackRow : SeqPaint.WhiteRow;
            dc.FillRectangle(row, new Rect(KeyWidth, y, right - KeyWidth, RowHeight));
            dc.DrawLine(midi % 12 == 0 ? SeqPaint.BeatPen : SeqPaint.GridPen, new Point(KeyWidth, y + RowHeight), new Point(right, y + RowHeight));
            // Key column.
            bool black = SeqPaint.IsBlackKey(midi);
            dc.FillRectangle(black ? SeqPaint.BlackRow : SeqPaint.LabelBack, new Rect(0, y, KeyWidth - 2, RowHeight - 1));
            if (midi % 12 == 0 || degree == 0)
                SeqPaint.Text(dc, SynthPatch.NoteName(midi), new Point(4, y + 1), midi % 12 == 0 ? SeqPaint.StrongLabelBrush : SeqPaint.LabelBrush, 9.5,
                    midi % 12 == 0 ? FontWeight.SemiBold : FontWeight.Normal);
        }

        for (int c = 0; c < columns; c++)
        {
            double x = KeyWidth + c * cw;
            if ((c / 4) % 2 == 1) dc.FillRectangle(SeqPaint.BeatTint, new Rect(x, HeaderHeight, cw, Bounds.Height));
            if (c == PlayStep) dc.FillRectangle(SeqPaint.PlayTint, new Rect(x, 0, cw, Bounds.Height));
            var pen = c % 16 == 0 ? SeqPaint.BarPen : c % 4 == 0 ? SeqPaint.BeatPen : SeqPaint.GridPen;
            dc.DrawLine(pen, new Point(x, HeaderHeight), new Point(x, Bounds.Height));
        }
        if (seq.Length < columns)
            dc.FillRectangle(SeqPaint.OutsideTint, new Rect(KeyWidth + seq.Length * cw, HeaderHeight, (columns - seq.Length) * cw, Bounds.Height));

        foreach (var n in seq.Notes)
        {
            var rect = new Rect(KeyWidth + n.Step * cw + 1, RowY(n.Midi) + 1, Math.Max(4, n.Length * cw - 2), RowHeight - 2);
            dc.DrawRectangle(n == _active ? NoteSelectedBrush : NoteBrush, NotePen, rect, 3, 3);
            if (rect.Width > 26)
                SeqPaint.Text(dc, SynthPatch.NoteName(n.Midi), new Point(rect.X + 3, rect.Y), Brushes.Black, 9);
        }

        // Bar.beat numbers, kept at the top of the visible part while scrolling.
        double top = Math.Max(0, HeaderOffset);
        dc.FillRectangle(SeqPaint.Back, new Rect(0, top, Bounds.Width, HeaderHeight));
        for (int c = 0; c < columns; c++)
        {
            double x = KeyWidth + c * cw;
            if (c == PlayStep) dc.FillRectangle(SeqPaint.PlayTint, new Rect(x, top, cw, HeaderHeight));
            if (c % 4 == 0)
                SeqPaint.Text(dc, c % 16 == 0 ? $"{c / 16 + 1}" : $"{c / 16 + 1}.{c % 16 / 4 + 1}", new Point(x + 3, top + 2),
                    c % 16 == 0 ? SeqPaint.StrongLabelBrush : SeqPaint.LabelBrush, 9);
        }
    }

    /// <summary>How far the roll is scrolled down, so the step numbers stay in view.</summary>
    public double HeaderOffset { get; set; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        if (p.Y < HeaderHeight || p.Y < HeaderOffset + HeaderHeight) return;
        var (step, midi) = Cell(p);
        if (p.X < KeyWidth)
        {
            Audition?.Invoke(midi);
            e.Handled = true;
            return;
        }
        if (step < 0 || step >= SequenceBase.MaxSteps) return;
        var hit = NoteAt(p);
        _changed = false;
        _grabStep = step;
        _grabMidi = midi;

        if (point.Properties.IsRightButtonPressed || (e.ClickCount == 2 && hit is not null))
        {
            _mode = DragMode.Erase;
            if (hit is not null) Change(() => Seq.Notes.Remove(hit));
        }
        else if (hit is not null)
        {
            _active = hit;
            _origStep = hit.Step;
            _origMidi = hit.Midi;
            _mode = InResizeZone(hit, p) ? DragMode.Resize : DragMode.Move;
            if (_mode == DragMode.Move) Audition?.Invoke(hit.Midi);
        }
        else
        {
            _mode = DragMode.Draw;
            var note = new SeqNote { Step = step, Midi = midi, Length = Math.Clamp(DrawLength, 1, SequenceBase.MaxSteps - step) };
            _active = note;
            Change(() => Seq.Notes.Add(note));
            Audition?.Invoke(midi);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_mode == DragMode.None)
        {
            var hover = p.X > KeyWidth ? NoteAt(p) : null;
            Cursor = new Cursor(hover is not null && InResizeZone(hover, p) ? StandardCursorType.SizeWestEast
                : hover is not null ? StandardCursorType.SizeAll : StandardCursorType.Hand);
            return;
        }
        var (step, midi) = Cell(p);
        step = Math.Clamp(step, 0, SequenceBase.MaxSteps - 1);
        switch (_mode)
        {
            case DragMode.Draw or DragMode.Resize when _active is { } n:
                int length = Math.Clamp(step - n.Step + 1, 1, SequenceBase.MaxSteps - n.Step);
                if (length != n.Length) Change(() => { n.Length = length; DrawLength = length; });
                break;
            case DragMode.Move when _active is { } n:
                int newStep = Math.Clamp(_origStep + step - _grabStep, 0, SequenceBase.MaxSteps - n.Length);
                int newMidi = Math.Clamp(_origMidi + midi - _grabMidi, LowestMidi, HighestMidi);
                if (newStep != n.Step || newMidi != n.Midi)
                {
                    bool pitch = newMidi != n.Midi;
                    Change(() => { n.Step = newStep; n.Midi = newMidi; });
                    if (pitch) Audition?.Invoke(newMidi);
                }
                break;
            case DragMode.Erase:
                if (NoteAt(p) is { } hit) Change(() => Seq.Notes.Remove(hit));
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == DragMode.None) return;
        if (_mode == DragMode.Resize && _active is { } n) DrawLength = n.Length;
        _mode = DragMode.None;
        _active = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (_changed) Committed?.Invoke();
        _changed = false;
    }

    private void Change(Action change)
    {
        if (!_changed) BeforeEdit?.Invoke();
        _changed = true;
        change();
        InvalidateVisual();
        Edited?.Invoke();
    }
}

/// <summary>A drum-machine grid: one row per drum, one cell per sixteenth.</summary>
public sealed class BeatGridView : Control
{
    public static readonly Color HitColor = Color.Parse("#FB923C");
    private static readonly IBrush[] HitBrushes =
    {
        Brushes.Transparent,
        new SolidColorBrush(Color.Parse("#7A4A25")),  // soft
        new SolidColorBrush(HitColor),                // normal
        new SolidColorBrush(Color.Parse("#FFD166")),  // accent
    };
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#E05A5A"));

    public const double LabelWidth = 120, HeaderHeight = 16, RowHeight = 26, MuteWidth = 20;

    private readonly Func<BeatSequence> _seq;
    private readonly Func<IReadOnlyList<SequencerDrum>> _drums;

    private int _paintValue = -1;
    private int _paintRow = -1;
    private bool _changed;

    public event Action? BeforeEdit;
    public event Action? Edited;
    public event Action? Committed;
    /// <summary>Raised to play one drum (clicking its name).</summary>
    public event Action<string>? Audition;

    public int PlayStep { get; set; } = -1;

    public BeatGridView(Func<BeatSequence> seq, Func<IReadOnlyList<SequencerDrum>> drums)
    {
        _seq = seq;
        _drums = drums;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Click a cell for a hit (again to remove it) and drag to paint.\n" +
            "Right-click a cell: soft → normal → accent (Tidal: # gain). M mutes a drum, click its name to hear it.");
    }

    /// <summary>Sets the height for the current drums.</summary>
    public void UpdateSize()
    {
        Height = HeaderHeight + Math.Max(1, _drums().Count) * RowHeight + 2;
        InvalidateVisual();
    }

    private int Columns => SeqPaint.Columns(_seq().Length);
    private double ColumnWidth => Math.Max(12, (Bounds.Width - LabelWidth) / Columns);

    public override void Render(DrawingContext dc)
    {
        var seq = _seq();
        var drums = _drums();
        int columns = Columns;
        double cw = ColumnWidth;
        double right = LabelWidth + columns * cw;
        dc.FillRectangle(SeqPaint.Back, new Rect(Bounds.Size));

        for (int c = 0; c < columns; c++)
        {
            double x = LabelWidth + c * cw;
            if (c == PlayStep) dc.FillRectangle(SeqPaint.PlayTint, new Rect(x, 0, cw, Bounds.Height));
            if (c % 4 == 0)
                SeqPaint.Text(dc, c % 16 == 0 ? $"{c / 16 + 1}" : $"{c / 16 + 1}.{c % 16 / 4 + 1}", new Point(x + 3, 2),
                    c % 16 == 0 ? SeqPaint.StrongLabelBrush : SeqPaint.LabelBrush, 9);
        }

        for (int r = 0; r < drums.Count; r++)
        {
            var drum = drums[r];
            double y = HeaderHeight + r * RowHeight;
            bool muted = seq.Muted.Contains(drum.Drum);
            dc.FillRectangle(SeqPaint.LabelBack, new Rect(0, y + 1, LabelWidth - 4, RowHeight - 2), 3);
            dc.DrawRectangle(muted ? MutedBrush : SeqPaint.WhiteRow, null, new Rect(3, y + 5, MuteWidth - 4, RowHeight - 10), 2, 2);
            SeqPaint.Text(dc, "M", new Point(7, y + 6), muted ? Brushes.White : SeqPaint.LabelBrush, 10, FontWeight.SemiBold);
            SeqPaint.Text(dc, drum.Drum, new Point(MuteWidth + 6, y + 3), muted ? SeqPaint.LabelBrush : SeqPaint.StrongLabelBrush, 12, FontWeight.SemiBold);
            SeqPaint.Text(dc, drum.Label, new Point(MuteWidth + 6, y + 16), SeqPaint.LabelBrush, 8.5);

            var row = seq.Row(drum.Drum);
            for (int c = 0; c < columns; c++)
            {
                double x = LabelWidth + c * cw;
                var cell = new Rect(x + 1.5, y + 2, cw - 3, RowHeight - 4);
                dc.FillRectangle((c / 4) % 2 == 0 ? SeqPaint.WhiteRow : SeqPaint.ScaleRow, cell, 3);
                int v = row[c];
                if (v > 0)
                {
                    double inset = v == BeatSequence.Soft ? 5 : 2;
                    var hit = cell.Deflate(inset);
                    var brush = HitBrushes[v];
                    using (dc.PushOpacity(muted ? 0.35 : 1)) dc.FillRectangle(brush, hit, 3);
                }
            }
        }
        for (int c = 0; c <= columns; c += 4)
        {
            double x = LabelWidth + c * cw;
            dc.DrawLine(c % 16 == 0 ? SeqPaint.BarPen : SeqPaint.BeatPen, new Point(x, HeaderHeight), new Point(x, Bounds.Height));
        }
        if (seq.Length < columns)
            dc.FillRectangle(SeqPaint.OutsideTint, new Rect(LabelWidth + seq.Length * cw, HeaderHeight, (columns - seq.Length) * cw, Bounds.Height));
    }

    private (int Row, int Step) Cell(Point p) =>
        ((int)Math.Floor((p.Y - HeaderHeight) / RowHeight), (int)Math.Floor((p.X - LabelWidth) / ColumnWidth));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var drums = _drums();
        var (r, step) = Cell(p);
        if (r < 0 || r >= drums.Count) return;
        var drum = drums[r].Drum;
        var seq = _seq();
        _changed = false;
        if (p.X < LabelWidth)
        {
            if (p.X < MuteWidth)
                Change(() => { if (!seq.Muted.Remove(drum)) seq.Muted.Add(drum); });
            else Audition?.Invoke(drum);
            if (_changed) Committed?.Invoke();
            e.Handled = true;
            return;
        }
        if (step < 0 || step >= SequenceBase.MaxSteps) return;
        var row = seq.Row(drum);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            int next = row[step] switch { BeatSequence.Soft => BeatSequence.Normal, BeatSequence.Normal => BeatSequence.Accent, _ => BeatSequence.Soft };
            Change(() => row[step] = next);
            _paintValue = next;
        }
        else
        {
            _paintValue = row[step] > 0 ? BeatSequence.Off : BeatSequence.Normal;
            Change(() => row[step] = _paintValue);
            if (_paintValue > 0) Audition?.Invoke(drum);
        }
        _paintRow = r;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_paintValue < 0) return;
        var drums = _drums();
        var (_, step) = Cell(e.GetPosition(this));
        if (_paintRow >= drums.Count || step < 0 || step >= SequenceBase.MaxSteps) return;
        var row = _seq().Row(drums[_paintRow].Drum);
        if (row[step] != _paintValue) Change(() => row[step] = _paintValue);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_paintValue < 0) return;
        _paintValue = -1;
        _paintRow = -1;
        e.Pointer.Capture(null);
        if (_changed) Committed?.Invoke();
        _changed = false;
    }

    private void Change(Action change)
    {
        if (!_changed) BeforeEdit?.Invoke();
        _changed = true;
        change();
        InvalidateVisual();
        Edited?.Invoke();
    }
}
