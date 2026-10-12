using System.Net;
using System.Net.Sockets;
using Hotlab.Gateway.Relay;

namespace Hotlab.Gateway.Tests.Relay;

public sealed class RdpRelayTests
{
    /// <summary>Connected socket pair over loopback: (near end handed to the relay, far end held by the test).</summary>
    private static async Task<(Socket Near, Socket Far)> PairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var far = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptSocketAsync();
        await far.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        return (await accept, far);
    }

    [Fact]
    public async Task HalfClose_WithSilentPeer_EndsAfterHalfCloseTimeout()
    {
        var (clientNear, clientFar) = await PairAsync();
        var (backendNear, backendFar) = await PairAsync();
        using var _1 = clientNear; using var _2 = clientFar;
        using var _3 = backendNear; using var _4 = backendFar;

        var relay = RdpRelay.RunAsync(clientNear, backendNear, CancellationToken.None,
            halfCloseTimeout: TimeSpan.FromMilliseconds(200));

        clientFar.Shutdown(SocketShutdown.Send); // backend never closes its side

        var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<TimeoutException>(result.Error);
    }

    [Fact]
    public async Task HalfClose_WithoutTimeout_WaitsForOtherDirection()
    {
        var (clientNear, clientFar) = await PairAsync();
        var (backendNear, backendFar) = await PairAsync();
        using var _1 = clientNear; using var _2 = clientFar;
        using var _3 = backendNear; using var _4 = backendFar;

        var relay = RdpRelay.RunAsync(clientNear, backendNear, CancellationToken.None);
        clientFar.Shutdown(SocketShutdown.Send);

        await Task.Delay(300);
        Assert.False(relay.IsCompleted);

        backendFar.Shutdown(SocketShutdown.Send);
        var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error);
    }
}
