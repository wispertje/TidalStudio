namespace TidalStudio.Core.Events;

/// <summary>Thread-safe, time-ordered store of recent Tidal events. Written by the OSC thread, read by the UI.</summary>
public sealed class EventStore
{
    private readonly object _lock = new();
    private readonly List<TidalEvent> _events = new();
    private long _version;

    /// <summary>How long events are kept, in seconds.</summary>
    public double RetentionSeconds { get; set; } = 120;

    /// <summary>Increases whenever events are added or cleared.</summary>
    public long Version => Interlocked.Read(ref _version);

    public event Action<TidalEvent>? EventAdded;

    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    public void Add(TidalEvent e)
    {
        lock (_lock)
        {
            // Events arrive almost sorted (bundles are sent in time order per target); insert from the end.
            int i = _events.Count;
            while (i > 0 && _events[i - 1].Time > e.Time) i--;
            _events.Insert(i, e);

            double cutoff = e.Time - RetentionSeconds;
            int remove = 0;
            while (remove < _events.Count && _events[remove].End < cutoff) remove++;
            if (remove > 0) _events.RemoveRange(0, remove);
        }
        Interlocked.Increment(ref _version);
        EventAdded?.Invoke(e);
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
        Interlocked.Increment(ref _version);
    }

    /// <summary>Events that overlap the time window [from, to).</summary>
    public List<TidalEvent> Query(double from, double to)
    {
        var result = new List<TidalEvent>();
        lock (_lock)
        {
            foreach (var e in _events)
            {
                if (e.Time >= to) break;
                if (e.End > from || (e.Duration <= 0 && e.Time >= from)) result.Add(e);
            }
        }
        return result;
    }

    /// <summary>Events sounding at <paramref name="now"/>.</summary>
    public List<TidalEvent> Active(double now, double minDuration = 0.05)
    {
        var result = new List<TidalEvent>();
        lock (_lock)
        {
            // Durations are short; scan back from the end until well before now.
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                if (e.Time > now) continue;
                if (now < e.Time + Math.Max(e.Duration, minDuration)) result.Add(e);
                else if (e.Time < now - 30) break;
            }
        }
        result.Reverse();
        return result;
    }

    public TidalEvent? Latest
    {
        get { lock (_lock) return _events.Count == 0 ? null : _events[^1]; }
    }
}
