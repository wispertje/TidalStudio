using System.Diagnostics;

namespace TidalStudio.Core.Timing;

/// <summary>
/// The app's monotonic clock, in seconds since startup, anchored to UTC so OSC (NTP) time tags
/// from Tidal can be converted into the same time base.
/// </summary>
public sealed class TimeBase
{
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DateTime _startUtc;

    public TimeBase() : this(DateTime.UtcNow) { }

    public TimeBase(DateTime startUtc)
    {
        _startUtc = startUtc.ToUniversalTime();
    }

    public static TimeBase Shared { get; } = new();

    /// <summary>Seconds since this time base was created.</summary>
    public double Now => _stopwatch.Elapsed.TotalSeconds;

    /// <summary>Converts a 64-bit NTP time tag to seconds in this time base.</summary>
    public double FromNtp(ulong ntp)
    {
        double ntpSeconds = (ntp >> 32) + (ntp & 0xFFFFFFFFUL) / 4294967296.0;
        double startNtpSeconds = (_startUtc - NtpEpoch).TotalSeconds;
        return ntpSeconds - startNtpSeconds;
    }

    public static ulong ToNtp(DateTime utc)
    {
        double seconds = (utc.ToUniversalTime() - NtpEpoch).TotalSeconds;
        ulong whole = (ulong)Math.Floor(seconds);
        ulong frac = (ulong)((seconds - Math.Floor(seconds)) * 4294967296.0);
        return (whole << 32) | frac;
    }
}
