using TidalStudio.Core.Events;
using TidalStudio.Core.Timing;

namespace TidalStudio.Tests;

public class ClockAndStoreTests
{
    [Fact]
    public void Clock_extrapolates_from_last_event()
    {
        var clock = new CycleClock();
        clock.Update(cycle: 10, cps: 0.5, time: 100);
        Assert.Equal(10, clock.CycleAt(100), 6);
        Assert.Equal(11, clock.CycleAt(102), 6);
        Assert.Equal(9.5, clock.CycleAt(99), 6);
        Assert.Equal(104, clock.TimeAt(12), 6);
    }

    [Fact]
    public void Clock_ignores_older_events_and_follows_tempo_changes()
    {
        var clock = new CycleClock();
        clock.Update(10, 0.5, 100);
        clock.Update(5, 0.5, 90); // late packet
        Assert.Equal(10, clock.CycleAt(100), 6);
        clock.Update(11, 1.0, 102);
        Assert.Equal(13, clock.CycleAt(104), 6);
        Assert.Equal(1.0, clock.Cps);
    }

    [Fact]
    public void Clock_reports_playing_only_shortly_after_events()
    {
        var clock = new CycleClock();
        Assert.False(clock.IsPlaying(0));
        clock.Update(0, 1, 10);
        Assert.True(clock.IsPlaying(11));
        Assert.False(clock.IsPlaying(13));
    }

    [Fact]
    public void Store_keeps_events_sorted_and_queries_windows()
    {
        var store = new EventStore();
        store.Add(new TidalEvent { Time = 2, Duration = 0.5, Sound = "b" });
        store.Add(new TidalEvent { Time = 1, Duration = 0.5, Sound = "a" });
        store.Add(new TidalEvent { Time = 3, Duration = 0.5, Sound = "c" });

        Assert.Equal(new[] { "a", "b", "c" }, store.Query(0, 10).Select(e => e.Sound));
        Assert.Equal(new[] { "b" }, store.Query(2.2, 2.4).Select(e => e.Sound));
        Assert.Equal(new[] { "c" }, store.Active(3.1).Select(e => e.Sound));
        Assert.Empty(store.Active(3.6));
    }

    [Fact]
    public void Store_drops_events_past_retention()
    {
        var store = new EventStore { RetentionSeconds = 10 };
        store.Add(new TidalEvent { Time = 0, Duration = 1 });
        store.Add(new TidalEvent { Time = 20, Duration = 1 });
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Event_pitch_follows_superdirt_conventions()
    {
        Assert.Equal(64, new TidalEvent { Note = 4 }.Midi());
        Assert.Equal(64, new TidalEvent { Note = 4, N = 2 }.Midi());
        Assert.Equal(62, new TidalEvent { N = 2 }.Midi());
        Assert.Null(new TidalEvent { N = 2 }.Midi(treatNAsPitch: false));
        Assert.Equal("bd:3", new TidalEvent { Sound = "bd", N = 3 }.Label);
        Assert.Equal("bd", new TidalEvent { Sound = "bd", N = 0 }.Label);
    }
}
