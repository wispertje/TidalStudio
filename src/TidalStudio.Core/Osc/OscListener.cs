using System.Net;
using System.Net.Sockets;
using TidalStudio.Core.Events;
using TidalStudio.Core.Timing;

namespace TidalStudio.Core.Osc;

/// <summary>Receives Tidal's OSC event stream on a UDP port and feeds the event store and cycle clock.</summary>
public sealed class OscListener : IDisposable
{
    private readonly EventStore _store;
    private readonly CycleClock _clock;
    private readonly TimeBase _timeBase;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public OscListener(EventStore store, CycleClock clock, TimeBase timeBase)
    {
        _store = store;
        _clock = clock;
        _timeBase = timeBase;
    }

    public int Port { get; private set; }
    public bool IsListening => _udp is not null;
    public long PacketsReceived;
    public long PacketsRejected;

    public event Action<string>? Error;

    public void Start(int port)
    {
        Stop();
        Port = port;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var udp = _udp;
        _ = Task.Run(() => ReceiveLoop(udp, token), token);
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // ICMP port unreachable from an earlier send; harmless on Windows
            }
            catch (Exception e)
            {
                Error?.Invoke($"OSC receive failed: {e.Message}");
                break;
            }

            Interlocked.Increment(ref PacketsReceived);
            if (!OscCodec.TryDecode(result.Buffer, out var packet) || packet is null)
            {
                Interlocked.Increment(ref PacketsRejected);
                continue;
            }
            Handle(packet);
        }
    }

    /// <summary>Processes one decoded packet (public for tests and for feeding packets from elsewhere).</summary>
    public void Handle(OscPacket packet)
    {
        foreach (var e in EventDecoder.Decode(packet, _timeBase))
        {
            _clock.Update(e);
            _store.Add(e);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
