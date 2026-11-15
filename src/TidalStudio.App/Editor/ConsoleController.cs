using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Editor;

/// <summary>The output console: batched appends from any thread, colour per line kind, bounded length.</summary>
public sealed class ConsoleController
{
    private const int MaxLines = 3000;
    private readonly TextEditor _view;
    private readonly ConcurrentQueue<OutputLine> _pending = new();
    private readonly List<OutputKind> _kinds = new();
    private readonly DispatcherTimer _timer;

    public ConsoleController(TextEditor view)
    {
        _view = view;
        _view.Document = new TextDocument();
        _view.TextArea.TextView.LineTransformers.Add(new Colorizer(this));
        _view.Options.EnableHyperlinks = false;
        _view.Options.EnableEmailHyperlinks = false;
        _view.Options.AllowScrollBelowDocument = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Flush());
        _timer.Start();
    }

    public void Add(OutputLine line) => _pending.Enqueue(line);

    public void Add(OutputKind kind, string text)
    {
        foreach (var l in text.Replace("\r\n", "\n").Split('\n')) Add(new OutputLine(kind, l));
    }

    public void Clear()
    {
        _pending.Clear();
        _kinds.Clear();
        _view.Document.Text = "";
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var doc = _view.Document;
        var sb = new System.Text.StringBuilder();
        bool first = doc.TextLength == 0;
        while (_pending.TryDequeue(out var line))
        {
            if (!first) sb.Append('\n');
            first = false;
            string prefix = line.Kind switch
            {
                OutputKind.Input => "› ",
                OutputKind.Info => "· ",
                _ => "",
            };
            sb.Append(prefix).Append(line.Text);
            _kinds.Add(line.Kind);
        }

        bool atEnd = _view.TextArea.TextView.VerticalOffset + _view.TextArea.TextView.Bounds.Height >= _view.TextArea.TextView.DocumentHeight - 4;
        doc.BeginUpdate();
        doc.Insert(doc.TextLength, sb.ToString());
        int excess = doc.LineCount - MaxLines;
        if (excess > 0)
        {
            var cut = doc.GetLineByNumber(excess + 1);
            doc.Remove(0, cut.Offset);
            _kinds.RemoveRange(0, Math.Min(excess, _kinds.Count));
        }
        doc.EndUpdate();
        // The colourizer relies on _kinds matching lines; resync defensively.
        while (_kinds.Count < doc.LineCount) _kinds.Insert(0, OutputKind.Output);
        if (_kinds.Count > doc.LineCount) _kinds.RemoveRange(0, _kinds.Count - doc.LineCount);
        if (atEnd) _view.ScrollToEnd();
    }

    private sealed class Colorizer(ConsoleController owner) : DocumentColorizingTransformer
    {
        private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF7A8C"));
        private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#7FB4FF"));
        private static readonly IBrush InputBrush = new SolidColorBrush(Color.Parse("#6E7787"));

        protected override void ColorizeLine(DocumentLine line)
        {
            int index = line.LineNumber - 1;
            if (index < 0 || index >= owner._kinds.Count || line.Length == 0) return;
            IBrush? brush = owner._kinds[index] switch
            {
                OutputKind.Error => ErrorBrush,
                OutputKind.Info => InfoBrush,
                OutputKind.Input => InputBrush,
                _ => null,
            };
            if (brush is null) return;
            ChangeLinePart(line.Offset, line.EndOffset, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
