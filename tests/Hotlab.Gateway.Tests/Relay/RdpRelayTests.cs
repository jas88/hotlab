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

        var result = await relay.WaitAsync(RelayHarness.TestTimeout);
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
        var result = await relay.WaitAsync(RelayHarness.TestTimeout);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Cancellation_WhileRelaying_IsReportedAsCancelled()
    {
        var (clientNear, clientFar) = await PairAsync();
        var (backendNear, backendFar) = await PairAsync();
        using var _1 = clientNear; using var _2 = clientFar;
        using var _3 = backendNear; using var _4 = backendFar;
        using var cts = new CancellationTokenSource();

        var relay = RdpRelay.RunAsync(clientNear, backendNear, cts.Token);
        await cts.CancelAsync();

        var result = await relay.WaitAsync(RelayHarness.TestTimeout);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
    }

    [Fact]
    public async Task CleanCompletion_ReportsNoError_AndCountsBytes()
    {
        var (clientNear, clientFar) = await PairAsync();
        var (backendNear, backendFar) = await PairAsync();
        using var _1 = clientNear; using var _2 = clientFar;
        using var _3 = backendNear; using var _4 = backendFar;
        using var cts = new CancellationTokenSource();

        var relay = RdpRelay.RunAsync(clientNear, backendNear, cts.Token);
        await RelayHarness.SendAllAsync(clientFar, new byte[1000]);
        clientFar.Shutdown(SocketShutdown.Send);
        await RelayHarness.ReadExactlyAsync(backendFar, 1000);
        await RelayHarness.AssertEofAsync(backendFar);
        backendFar.Shutdown(SocketShutdown.Send);

        var result = await relay.WaitAsync(RelayHarness.TestTimeout);
        await cts.CancelAsync(); // after a clean finish, cancelling changes nothing
        Assert.Null(result.Error);
        Assert.Equal(1000, result.ClientToBackendBytes);
        Assert.Equal(0, result.BackendToClientBytes);
    }

    [Fact]
    public async Task SendAllAsync_DeliversLargeBuffersCompletely()
    {
        var (near, far) = await PairAsync();
        using var _1 = near; using var _2 = far;
        var payload = RelayHarness.RandomBytes(4 * 1024 * 1024, seed: 7);

        var send = RdpRelay.SendAllAsync(near, payload, CancellationToken.None);
        var received = await RelayHarness.ReadExactlyAsync(far, payload.Length);
        await send.WaitAsync(RelayHarness.TestTimeout);

        Assert.Equal(payload, received);
    }
}
