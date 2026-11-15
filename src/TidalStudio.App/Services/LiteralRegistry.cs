using AvaloniaEdit.Document;
using TidalStudio.Core.Events;

namespace TidalStudio.App.Services;

/// <summary>
/// Remembers where each evaluated string literal lives in the document, using text anchors so the
/// positions follow edits. Event source spans (literal id + range) are resolved through it.
/// </summary>
public sealed class LiteralRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly Queue<int> _order = new();
    private int _nextId;
    private const int MaxEntries = 20000;

    private sealed record Entry(TextDocument Document, TextAnchor Anchor, int ContentLength);

    public int NextId() => Interlocked.Increment(ref _nextId);

    public void Register(TextDocument document, int id, int quoteOffset, int contentLength)
    {
        var anchor = document.CreateAnchor(quoteOffset);
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        anchor.SurviveDeletion = false;
        lock (_lock)
        {
            _entries[id] = new Entry(document, anchor, contentLength);
            _order.Enqueue(id);
            while (_order.Count > MaxEntries) _entries.Remove(_order.Dequeue());
        }
    }

    /// <summary>Resolves a span to a document range, or null when the literal was deleted or edited.</summary>
    public (int Start, int Length)? Resolve(TextDocument document, SourceSpan span)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(span.LiteralId, out entry)) return null;
        }
        if (!ReferenceEquals(entry.Document, document) || entry.Anchor.IsDeleted) return null;
        int quote = entry.Anchor.Offset;
        int closing = quote + 1 + entry.ContentLength;
        if (closing >= document.TextLength) return null;
        if (document.GetCharAt(quote) != '"' || document.GetCharAt(closing) != '"') return null; // literal changed
        int start = quote + 1 + span.Start;
        int end = quote + 1 + Math.Min(span.End, entry.ContentLength);
        if (end <= start) return null;
        return (start, end - start);
    }

    /// <summary>Ids of literals whose opening quote lies in [start, end) of the document.</summary>
    public HashSet<int> IdsInRange(TextDocument document, int start, int end)
    {
        var result = new HashSet<int>();
        lock (_lock)
        {
            foreach (var (id, entry) in _entries)
            {
                if (!ReferenceEquals(entry.Document, document) || entry.Anchor.IsDeleted) continue;
                int o = entry.Anchor.Offset;
                if (o >= start && o < end) result.Add(id);
            }
        }
        return result;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _order.Clear();
        }
    }
}
