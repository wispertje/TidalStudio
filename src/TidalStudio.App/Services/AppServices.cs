using TidalStudio.Core.Audio;
using TidalStudio.Core.Events;
using TidalStudio.Core.Osc;
using TidalStudio.Core.Settings;
using TidalStudio.Core.SuperCollider;
using TidalStudio.Core.Tidal;
using TidalStudio.Core.Timing;

namespace TidalStudio.App.Services;

/// <summary>Everything shared between the editor, the visualizers and the window.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public AppServices()
    {
        Settings = AppSettings.Load();
        Time = TimeBase.Shared;
        Events = new EventStore();
        Clock = new CycleClock();
        Osc = new OscListener(Events, Clock, Time);
        Ghci = new GhciHost();
        SuperDirt = new SuperDirtLauncher();
        Audio = new AudioRingBuffer(1 << 16);
        AudioCapture = new AudioCapture(Audio);
        Literals = new LiteralRegistry();
    }

    public AppSettings Settings { get; }
    public TimeBase Time { get; }
    public EventStore Events { get; }
    public CycleClock Clock { get; }
    public OscListener Osc { get; }
    public GhciHost Ghci { get; }
    public SuperDirtLauncher SuperDirt { get; }
    public AudioRingBuffer Audio { get; }
    public AudioCapture AudioCapture { get; }
    public LiteralRegistry Literals { get; }
    public FrameTicker Ticker { get; } = new();

    public async ValueTask DisposeAsync()
    {
        Osc.Dispose();
        AudioCapture.Dispose();
        await Ghci.DisposeAsync();
        await SuperDirt.DisposeAsync();
    }
}
