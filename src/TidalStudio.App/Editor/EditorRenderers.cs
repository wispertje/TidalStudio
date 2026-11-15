using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TidalStudio.App.Services;
using TidalStudio.App.Visualizers;
using TidalStudio.Core.Settings;

namespace TidalStudio.App.Editor;

/// <summary>Highlights the mini-notation characters of events that are sounding right now.</summary>
public sealed class MiniNotationRenderer : IBackgroundRenderer
{
    private readonly AppServices _services;
    private readonly TextDocumentProvider _document;

    public MiniNotationRenderer(AppServices services, TextDocumentProvider document)
    {
        _services = services;
        _document = document;
    }

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Number of spans drawn in the last frame (used to decide whether to keep redrawing).</summary>
    public int LastDrawn { get; private set; }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        LastDrawn = 0;
        var settings = _services.Settings;
        var document = _document();
        if (!settings.HighlightMiniNotation || document is null || !textView.VisualLinesValid) return;

        double now = _services.Time.Now;
        var active = _services.Events.Active(now, minDuration: 0.06);
        if (active.Count == 0) return;

        int firstVisible = textView.VisualLines.Count > 0 ? textView.VisualLines[0].FirstDocumentLine.Offset : 0;
        int lastVisible = textView.VisualLines.Count > 0 ? textView.VisualLines[^1].LastDocumentLine.EndOffset : document.TextLength;

        // Merge identical ranges (several events from the same element) and keep the brightest.
        var ranges = new Dictionary<(int, int), (Color Color, double Strength)>();
        foreach (var e in active)
        {
            if (e.Spans.Count == 0) continue;
            double progress = e.Duration > 0 ? Math.Clamp((now - e.Time) / Math.Max(e.Duration, 0.06), 0, 1) : 0;
            double strength = 1 - 0.55 * progress;
            Color color = e.Color is { Length: > 0 } custom && VisualizerBase.TryParseColor(custom, out var c)
                ? c : Color.Parse(settings.PatternColor(e.PatternId));
            foreach (var span in e.Spans)
            {
                var range = _services.Literals.Resolve(document, span);
                if (range is not { } r || r.Start + r.Length < firstVisible || r.Start > lastVisible) continue;
                var key = (r.Start, r.Length);
                if (!ranges.TryGetValue(key, out var existing) || existing.Strength < strength) ranges[key] = (color, strength);
            }
        }

        double opacity = settings.HighlightOpacity;
        foreach (var ((start, length), (color, strength)) in ranges)
        {
            var builder = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, Length = length });
            var geometry = builder.CreateGeometry();
            if (geometry is null) continue;
            byte a = (byte)Math.Clamp(255 * opacity * strength, 0, 255);
            switch (settings.HighlightStyle)
            {
                case HighlightStyle.Outline:
                    drawingContext.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, a * 2), color.R, color.G, color.B)), 1.5), geometry);
                    break;
                case HighlightStyle.Underline:
                    var b = geometry.Bounds;
                    drawingContext.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, a * 2), color.R, color.G, color.B)),
                        new Rect(b.Left, b.Bottom - 2.5, b.Width, 2.5));
                    break;
                default:
                    drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B)),
                        new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, a * 1.6), color.R, color.G, color.B)), 1), geometry);
                    break;
            }
            LastDrawn++;
        }
    }
}

public delegate TextDocument? TextDocumentProvider();

/// <summary>Briefly flashes the code that was just run (green) or failed (red).</summary>
public sealed class EvalFlashRenderer : IBackgroundRenderer
{
    private readonly TimeProvider _time = TimeProvider.System;
    private TextDocument? _document;
    private TextAnchor? _start;
    private TextAnchor? _end;
    private long _startedAt;
    private Color _color;
    private const double DurationSeconds = 0.45;

    public KnownLayer Layer => KnownLayer.Background;

    public bool IsAnimating => _start is not null && _time.GetElapsedTime(_startedAt).TotalSeconds < DurationSeconds;

    public void Flash(TextDocument document, int start, int length, bool success)
    {
        _document = document;
        _start = document.CreateAnchor(start);
        _end = document.CreateAnchor(start + length);
        _end.MovementType = AnchorMovementType.AfterInsertion;
        _startedAt = _time.GetTimestamp();
        _color = success ? Color.FromRgb(78, 205, 196) : Color.FromRgb(255, 83, 112);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_start is null || _end is null || _start.IsDeleted || _end.IsDeleted || !ReferenceEquals(textView.Document, _document)) return;
        double t = _time.GetElapsedTime(_startedAt).TotalSeconds / DurationSeconds;
        if (t >= 1) return;
        var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true, ExtendToFullWidthAtLineEnd = true };
        int s = _start.Offset, e = Math.Max(_start.Offset, _end.Offset);
        builder.AddSegment(textView, new TextSegment { StartOffset = s, Length = e - s });
        var geometry = builder.CreateGeometry();
        if (geometry is null) return;
        byte a = (byte)(90 * (1 - t));
        drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(a, _color.R, _color.G, _color.B)), null, geometry);
    }
}

/// <summary>Marks lines that produced a ghci error. Marks are kept per document (one per tab).</summary>
public sealed class ErrorLineRenderer : IBackgroundRenderer
{
    private readonly Dictionary<TextDocument, List<(TextAnchor Anchor, string Message)>> _byDocument = new();
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.FromArgb(40, 255, 83, 112));
    private static readonly IBrush MarkBrush = new SolidColorBrush(Color.FromRgb(255, 83, 112));

    public KnownLayer Layer => KnownLayer.Background;

    private List<(TextAnchor Anchor, string Message)> For(TextDocument document)
    {
        if (!_byDocument.TryGetValue(document, out var list)) _byDocument[document] = list = new();
        return list;
    }

    public void Set(TextDocument document, IEnumerable<(int LineNumber, string Message)> errors)
    {
        var list = For(document);
        list.Clear();
        foreach (var (lineNumber, message) in errors)
        {
            if (lineNumber < 1 || lineNumber > document.LineCount) continue;
            var anchor = document.CreateAnchor(document.GetLineByNumber(lineNumber).Offset);
            list.Add((anchor, message));
        }
    }

    /// <summary>Removes error marks on lines inside [start, end).</summary>
    public void ClearRange(TextDocument document, int start, int end) =>
        For(document).RemoveAll(x => x.Anchor.IsDeleted || (x.Anchor.Offset >= start && x.Anchor.Offset <= end));

    /// <summary>Forgets a document's marks (when its tab closes).</summary>
    public void Clear(TextDocument document) => _byDocument.Remove(document);

    public string? MessageAt(TextDocument document, int lineNumber)
    {
        if (!_byDocument.TryGetValue(document, out var errors)) return null;
        foreach (var (anchor, message) in errors)
            if (!anchor.IsDeleted && document.GetLineByOffset(anchor.Offset).LineNumber == lineNumber) return message;
        return null;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is null || !_byDocument.TryGetValue(textView.Document, out var errors) || errors.Count == 0 || !textView.VisualLinesValid) return;
        foreach (var (anchor, _) in errors)
        {
            if (anchor.IsDeleted) continue;
            int lineNumber = textView.Document.GetLineByOffset(anchor.Offset).LineNumber;
            var visualLine = textView.GetVisualLine(lineNumber);
            if (visualLine is null) continue;
            double y = visualLine.VisualTop - textView.VerticalOffset;
            var rect = new Rect(0, y, textView.Bounds.Width, visualLine.Height);
            drawingContext.FillRectangle(LineBrush, rect);
            drawingContext.FillRectangle(MarkBrush, new Rect(0, y, 3, visualLine.Height));
        }
    }
}
