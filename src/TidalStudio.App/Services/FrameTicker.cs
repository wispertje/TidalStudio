using Avalonia.Controls;
using Avalonia.Threading;

namespace TidalStudio.App.Services;

/// <summary>Calls registered callbacks once per rendered frame (animation loop for visualizers and highlights).</summary>
public sealed class FrameTicker
{
    private readonly List<Action<TimeSpan>> _callbacks = new();
    private TopLevel? _topLevel;
    private bool _running;

    public void Attach(TopLevel topLevel)
    {
        _topLevel = topLevel;
        if (_running) return;
        _running = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    public void Detach()
    {
        _running = false;
        _topLevel = null;
    }

    public IDisposable Subscribe(Action<TimeSpan> callback)
    {
        _callbacks.Add(callback);
        return new Unsubscriber(this, callback);
    }

    private void OnFrame(TimeSpan time)
    {
        if (!_running || _topLevel is null) return;
        foreach (var cb in _callbacks.ToArray())
        {
            try { cb(time); }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
        }
        _topLevel.RequestAnimationFrame(OnFrame);
    }

    private sealed class Unsubscriber(FrameTicker owner, Action<TimeSpan> callback) : IDisposable
    {
        public void Dispose() => Dispatcher.UIThread.Post(() => owner._callbacks.Remove(callback));
    }
}
