namespace TidalStudio.Core.Osc;

/// <summary>An OSC packet: either a message or a bundle.</summary>
public abstract record OscPacket;

/// <summary>An OSC message with an address and typed arguments (int, float, string, blob, long, double, bool, null).</summary>
public sealed record OscMessage(string Address, IReadOnlyList<object?> Arguments) : OscPacket
{
    public OscMessage(string address, params object?[] args) : this(address, (IReadOnlyList<object?>)args) { }
}

/// <summary>An OSC bundle: an NTP time tag plus nested packets.</summary>
public sealed record OscBundle(ulong TimeTag, IReadOnlyList<OscPacket> Packets) : OscPacket
{
    /// <summary>The special "immediately" time tag.</summary>
    public const ulong Immediately = 1;
}
