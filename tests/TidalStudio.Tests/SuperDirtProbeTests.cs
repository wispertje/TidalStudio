using System.Net;
using System.Net.Sockets;
using TidalStudio.Core.Osc;
using TidalStudio.Core.SuperCollider;

namespace TidalStudio.Tests;

public class SuperDirtProbeTests
{
    [Fact]
    public async Task Answers_from_a_handshake_responder_count_as_running()
    {
        using var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)fake.Client.LocalEndPoint!).Port;
        var responder = Task.Run(async () =>
        {
            var request = await fake.ReceiveAsync();
            if (OscCodec.Decode(request.Buffer) is OscMessage { Address: "/dirt/handshake" })
                await fake.SendAsync(OscCodec.Encode(new OscMessage("/dirt/handshake/reply", "&serverPort", 57110)), request.RemoteEndPoint);
        });

        Assert.True(await SuperDirtProbe.IsRunningAsync(port, timeoutMs: 2000));
        await responder;
    }

    [Fact]
    public async Task Nothing_listening_is_not_running()
    {
        int port;
        using (var temp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)temp.Client.LocalEndPoint!).Port;
        Assert.False(await SuperDirtProbe.IsRunningAsync(port, timeoutMs: 300));
    }

    [Fact]
    public async Task A_listener_that_does_not_answer_like_bare_sclang_is_not_running()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        Assert.False(await SuperDirtProbe.IsRunningAsync(port, timeoutMs: 300));
    }
}
