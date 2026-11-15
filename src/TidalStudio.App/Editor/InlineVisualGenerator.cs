using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TidalStudio.App.Services;
using TidalStudio.App.Visualizers;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Editor;

/// <summary>
/// Strudel-style inline visuals: a comment like <c>-- _punchcard</c> (or _pianoroll, _spiral, _pitchwheel, _scope,
/// _spectrum, _cycle) shows that visual at the end of the line, for the patterns of the surrounding block.
/// Options can follow as key=value pairs: <c>-- _pianoroll cycles=2 height=100 rows=pitch</c>.
/// </summary>
public sealed partial class InlineVisualGenerator : VisualLineElementGenerator
{
    private readonly AppServices _services;
    private readonly Dictionary<(DocumentLine Line, string Spec), InlineVisualHost> _cache = new();

    public InlineVisualGenerator(AppServices services)
    {
        _services = services;
    }

    public bool Enabled { get; set; } = true;

    [GeneratedRegex(@"\G--\s*_(punchcard|pianoroll|spiral|pitchwheel|scope|tscope|spectrum|cycle)\b((?:\s+[A-Za-z]+=[^\s]+)*)")]
    private static partial Regex Directive();

    /// <summary>Matches a directive only when it starts the line's comment (so prose in comments is ignored).</summary>
    public static Match MatchDirective(string lineText)
    {
        int comment = CommentStart(lineText);
        return comment < 0 ? Match.Empty : Directive().Match(lineText, comment);
    }

    private static int CommentStart(string text)
    {
        bool inString = false;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '-' && text[i + 1] == '-') return i;
        }
        return -1;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!Enabled) return -1;
        var line = CurrentContext.VisualLine.LastDocumentLine;
        if (startOffset > line.EndOffset) return -1;
        string text = CurrentContext.Document.GetText(line.Offset, line.Length);
        return MatchDirective(text).Success ? line.EndOffset : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        string text = CurrentContext.Document.GetText(line.Offset, line.Length);
        var match = MatchDirective(text);
        if (!match.Success) return null;

        string spec = match.Value;
        if (!_cache.TryGetValue((line, spec), out var host) || host.Parent is not null)
        {
            host = new InlineVisualHost(_services, CurrentContext.Document, line, match.Groups[1].Value, match.Groups[2].Value);
            _cache[(line, spec)] = host;
        }
        // Drop cache entries for deleted lines.
        foreach (var key in _cache.Keys.Where(k => k.Line.IsDeleted).ToList()) _cache.Remove(key);
        return new InlineObjectElement(0, host);
    }
}

/// <summary>Hosts one inline visualizer and restricts it to the events of its block.</summary>
public sealed class InlineVisualHost : Border
{
    private readonly AppServices _services;
    private readonly TextDocument _document;
    private readonly DocumentLine _line;
    private HashSet<int> _ids = new();
    private long _idsComputedFrame = -1;
    private long _frame;

    public InlineVisualHost(AppServices services, TextDocument document, DocumentLine line, string type, string optionText)
    {
        _services = services;
        _document = document;
        _line = line;

        var options = new Dictionary<string, string>();
        double height = type is "spiral" or "pitchwheel" ? 140 : 90;
        double width = 360;
        foreach (var pair in optionText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "height" && double.TryParse(kv[1], System.Globalization.CultureInfo.InvariantCulture, out var h)) height = Math.Clamp(h, 30, 600);
            else if (kv[0] == "width" && double.TryParse(kv[1], System.Globalization.CultureInfo.InvariantCulture, out var w)) width = Math.Clamp(w, 60, 1600);
            else options[kv[0]] = kv[1];
        }

        var vis = VisualizerFactory.Create(type, services) ?? new CycleVisualizer(services);
        vis.BindOptions(options);
        vis.EventFilter = BelongsToBlock;
        vis.Width = width;
        vis.Height = height;

        Child = vis;
        Margin = new Thickness(12, 4, 0, 4);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        ClipToBounds = true;
    }

    private IDisposable? _tick;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tick = _services.Ticker.Subscribe(_ => _frame++);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tick?.Dispose();
        _tick = null;
    }

    private bool BelongsToBlock(TidalEvent e)
    {
        if (_idsComputedFrame != _frame)
        {
            _ids = ComputeIds();
            _idsComputedFrame = _frame;
        }
        foreach (var span in e.Spans)
            if (_ids.Contains(span.LiteralId)) return true;
        return false;
    }

    /// <summary>Literal ids inside the block (lines between blank lines) that contains the directive.</summary>
    private HashSet<int> ComputeIds()
    {
        if (_line.IsDeleted) return new HashSet<int>();
        var first = _line;
        while (first.PreviousLine is { } prev && !IsBlank(prev)) first = prev;
        var last = _line;
        while (last.NextLine is { } next && !IsBlank(next)) last = next;
        return _services.Literals.IdsInRange(_document, first.Offset, last.EndOffset);
    }

    private bool IsBlank(DocumentLine line) => string.IsNullOrWhiteSpace(_document.GetText(line.Offset, line.Length));
}
