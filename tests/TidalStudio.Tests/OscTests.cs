using TidalStudio.Core.Events;
using TidalStudio.Core.Osc;
using TidalStudio.Core.Timing;

namespace TidalStudio.Tests;

public class OscTests
{
    [Fact]
    public void Message_round_trips_all_argument_types()
    {
        var msg = new OscMessage("/test", 42, 1.5f, "hello", 2.25, 7L, new byte[] { 1, 2, 3 }, true, false, null);
        var decoded = Assert.IsType<OscMessage>(OscCodec.Decode(OscCodec.Encode(msg)));
        Assert.Equal("/test", decoded.Address);
        Assert.Equal(new object?[] { 42, 1.5f, "hello", 2.25, 7L }, decoded.Arguments.Take(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])decoded.Arguments[5]!);
        Assert.Equal(true, decoded.Arguments[6]);
        Assert.Equal(false, decoded.Arguments[7]);
        Assert.Null(decoded.Arguments[8]);
    }

    [Fact]
    public void Bundle_round_trips_with_time_tag()
    {
        var bundle = new OscBundle(0xEEA41E1B9B02E800UL, new OscPacket[]
        {
            new OscMessage("/a", 1),
            new OscBundle(OscBundle.Immediately, new OscPacket[] { new OscMessage("/b", "x") }),
        });
        var decoded = Assert.IsType<OscBundle>(OscCodec.Decode(OscCodec.Encode(bundle)));
        Assert.Equal(bundle.TimeTag, decoded.TimeTag);
        Assert.Equal(2, decoded.Packets.Count);
        Assert.Equal("/b", Assert.IsType<OscMessage>(Assert.IsType<OscBundle>(decoded.Packets[1]).Packets[0]).Address);
    }

    [Fact]
    public void Garbage_is_rejected_without_throwing()
    {
        Assert.False(OscCodec.TryDecode(new byte[] { 1, 2, 3, 4, 5 }, out _));
        Assert.False(OscCodec.TryDecode("/abc\0\0\0\0,iii\0\0\0\0"u8.ToArray(), out _));
    }

    [Fact]
    public void Ntp_conversion_matches_time_base_origin()
    {
        var start = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tb = new TimeBase(start);
        Assert.Equal(0, tb.FromNtp(TimeBase.ToNtp(start)), 6);
        Assert.Equal(2.5, tb.FromNtp(TimeBase.ToNtp(start.AddSeconds(2.5))), 6);
    }

    [Fact]
    public void Dirt_play_bundle_decodes_into_event()
    {
        var start = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tb = new TimeBase(start);
        var msg = new OscMessage("/dirt/play",
            "_ctx", "4,8,6,8;1,9,3,9", "_id_", "1", "cps", 0.6f, "cycle", 12.25f, "delta", 0.5f,
            "n", 3f, "s", "sn", "orbit", 1, "color", "red");
        var bundle = new OscBundle(TimeBase.ToNtp(start.AddSeconds(10)), new OscPacket[] { msg });

        var e = Assert.Single(EventDecoder.Decode(bundle, tb));
        Assert.Equal(10, e.Time, 4);
        Assert.Equal(0.5, e.Duration, 4);
        Assert.Equal(12.25, e.Cycle, 4);
        Assert.Equal(0.6, e.Cps, 4);
        Assert.Equal("1", e.PatternId);
        Assert.Equal("sn", e.Sound);
        Assert.Equal(3, e.N);
        Assert.Equal(1, e.Orbit);
        Assert.Equal("red", e.Color);
        Assert.Equal(new[] { new SourceSpan(7, 3, 5), new SourceSpan(8, 0, 2) }, e.Spans);
    }

    [Fact]
    public void Spans_from_unwrapped_literals_are_ignored()
    {
        Assert.Empty(EventDecoder.ParseSpans("1,1,3,1"));
        Assert.Empty(EventDecoder.ParseSpans("garbage"));
        Assert.Empty(EventDecoder.ParseSpans(null));
    }

    [Fact]
    public void Real_tidal_packet_decodes()
    {
        // Captured from Tidal 1.10.3 (see spike): /dirt/play with _ctx, _id_, _p, cps, cycle, delta, n, s.
        var msg = new OscMessage("/dirt/play", "_ctx", "[((4,8),(6,8))]", "_id_", "1", "_p", "1",
            "cps", 0.5625f, "cycle", 0.5f, "delta", 0.8888889f, "n", 3f, "s", "sn");
        var e = EventDecoder.FromMessage(msg, 1.0);
        Assert.Equal("sn", e.Sound);
        Assert.Empty(e.Spans); // old debug format is not ours
        Assert.Equal(0.8888889, e.Duration, 5);
    }
}
