using System.Net;
using System.Net.Sockets;
using TidalStudio.Core.Osc;

namespace TidalStudio.Core.SuperCollider;

/// <summary>
/// Checks whether SuperDirt is running by sending it Tidal's <c>/dirt/handshake</c>; SuperDirt answers the sender
/// with <c>/dirt/handshake/reply</c>. This tells SuperDirt apart from a bare sclang, which also holds port 57120
/// but does not answer.
/// </summary>
public static class SuperDirtProbe
{
    public const int DefaultPort = 57120;

    public static async Task<bool> IsRunningAsync(int port = DefaultPort, int timeoutMs = 600, CancellationToken token = default)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, port);
            byte[] handshake = OscCodec.Encode(new OscMessage("/dirt/handshake"));
            await udp.SendAsync(handshake, target, token).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (OscCodec.TryDecode(result.Buffer, out var packet) && packet is OscMessage { Address: "/dirt/handshake/reply" })
                    return true;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false; // no answer in time
        }
        catch (SocketException)
        {
            // Windows reports "connection reset" when nothing listens on the port at all.
            return false;
        }
    }
}
