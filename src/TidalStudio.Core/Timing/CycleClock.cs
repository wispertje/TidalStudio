using TidalStudio.Core.Events;

namespace TidalStudio.Core.Timing;

/// <summary>
/// Follows Tidal's cycle clock from the (cycle, cps, time) triples carried by every event,
/// and extrapolates between events so visuals keep moving smoothly.
/// </summary>
public sealed class CycleClock
{
    private readonly object _lock = new();
    private double _refTime;
    private double _refCycle;
    private double _cps = 0.5625; // Tidal's default
    private double _lastEventTime = double.NegativeInfinity;
    private bool _hasReference;

    public double Cps
    {
        get { lock (_lock) return _cps; }
    }

    public bool HasReference
    {
        get { lock (_lock) return _hasReference; }
    }

    /// <summary>Time (in TimeBase seconds) of the most recent event.</summary>
    public double LastEventTime
    {
        get { lock (_lock) return _lastEventTime; }
    }

    public void Update(TidalEvent e) => Update(e.Cycle, e.Cps, e.Time);

    public void Update(double cycle, double cps, double time)
    {
        if (cps <= 0 || double.IsNaN(cps) || double.IsNaN(cycle)) return;
        lock (_lock)
        {
            // Only move the reference forward in time; late-arriving older events are ignored.
            if (_hasReference && time < _refTime) return;
            _refTime = time;
            _refCycle = cycle;
            _cps = cps;
            _hasReference = true;
            if (time > _lastEventTime) _lastEventTime = time;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _hasReference = false;
            _lastEventTime = double.NegativeInfinity;
        }
    }

    /// <summary>The cycle position at time <paramref name="time"/>.</summary>
    public double CycleAt(double time)
    {
        lock (_lock)
        {
            if (!_hasReference) return 0;
            return _refCycle + (time - _refTime) * _cps;
        }
    }

    /// <summary>The time at which cycle position <paramref name="cycle"/> occurs.</summary>
    public double TimeAt(double cycle)
    {
        lock (_lock)
        {
            if (!_hasReference || _cps <= 0) return 0;
            return _refTime + (cycle - _refCycle) / _cps;
        }
    }

    /// <summary>True when an event has been seen within <paramref name="cycles"/> cycles of <paramref name="now"/>.</summary>
    public bool IsPlaying(double now, double cycles = 2)
    {
        lock (_lock)
        {
            if (!_hasReference) return false;
            return now - _lastEventTime < cycles / Math.Max(_cps, 0.01);
        }
    }
}
