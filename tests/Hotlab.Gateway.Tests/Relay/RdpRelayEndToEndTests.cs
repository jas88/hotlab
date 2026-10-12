using System.Net;
using System.Net.Sockets;
using static Hotlab.Gateway.Tests.Relay.RelayHarness;

namespace Hotlab.Gateway.Tests.Relay;

public sealed class RdpRelayEndToEndTests
{
    [Fact]
    public async Task ValidRequest_ForwardsByteIdenticalStreamBothWays()
    {
        await using var h = await StartAsync();
        var cr = BuildConnectionRequest("alice");
        var extra = RandomBytes(100_000, seed: 1);

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(cr.Concat(extra).ToArray(), SocketFlags.None);

        using var backend = await h.AcceptBackendAsync();
        var received = await ReadExactlyAsync(backend, cr.Length + extra.Length);
        Assert.Equal(cr.Concat(extra).ToArray(), received);
        Assert.Equal("pve1", h.Activator.LastTarget!.Node);
        Assert.Equal(101, h.Activator.LastTarget.VmId);

        var reply = RandomBytes(200_000, seed: 2);
        await backend.SendAsync(reply, SocketFlags.None);
        Assert.Equal(reply, await ReadExactlyAsync(client, reply.Length));
    }

    [Fact]
    public async Task ClientHalfClose_PropagatesFin_AndBackendDataStillFlows()
    {
        await using var h = await StartAsync();
        var cr = BuildConnectionRequest("alice");

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(cr, SocketFlags.None);
        using var backend = await h.AcceptBackendAsync();
        await ReadExactlyAsync(backend, cr.Length);

        client.Shutdown(SocketShutdown.Send);
        await AssertEofAsync(backend);

        var late = RandomBytes(5000, seed: 3);
        await backend.SendAsync(late, SocketFlags.None);
        Assert.Equal(late, await ReadExactlyAsync(client, late.Length));

        backend.Shutdown(SocketShutdown.Send);
        await AssertEofAsync(client);
        await WaitForNoActiveConnectionsAsync(h);
    }

    [Fact]
    public async Task BackendHalfClose_PropagatesFin_AndClientDataStillFlows()
    {
        await using var h = await StartAsync();
        var cr = BuildConnectionRequest("alice");

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(cr, SocketFlags.None);
        using var backend = await h.AcceptBackendAsync();
        await ReadExactlyAsync(backend, cr.Length);

        backend.Shutdown(SocketShutdown.Send);
        await AssertEofAsync(client);

        var late = RandomBytes(5000, seed: 4);
        await client.SendAsync(late, SocketFlags.None);
        Assert.Equal(late, await ReadExactlyAsync(backend, late.Length));

        client.Shutdown(SocketShutdown.Send);
        await AssertEofAsync(backend);
        await WaitForNoActiveConnectionsAsync(h);
    }

    [Fact]
    public async Task TruncatedMstscCookie_RoutesToUniquePrefixMatch()
    {
        await using var h = await StartAsync();
        var cr = BuildConnectionRequest("domain\\jo"); // mstsc 9-char truncation of DOMAIN-backslash-johnsmith

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(cr, SocketFlags.None);

        using var backend = await h.AcceptBackendAsync();
        Assert.Equal(cr, await ReadExactlyAsync(backend, cr.Length));
        Assert.Equal(new Hotlab.Gateway.Routing.VmTarget("pve2", 202), h.Activator.LastTarget);
    }

    [Fact]
    public async Task UnknownUser_ClosesClient_WithoutActivating()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectClientAsync();
        await client.SendAsync(BuildConnectionRequest("mallory"), SocketFlags.None);

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
    }

    [Fact]
    public async Task NoCookie_ClosesClient_WithoutActivating()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectClientAsync();
        await client.SendAsync(BuildConnectionRequest(null), SocketFlags.None);

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
    }

    [Fact]
    public async Task GarbageFirstPdu_ClosesClient()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectClientAsync();
        await client.SendAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), SocketFlags.None);

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
    }

    [Fact]
    public async Task SilentClient_IsClosedAfterFirstPduTimeout()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectClientAsync();

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
    }

    [Fact]
    public async Task ActivationFailure_ClosesClient()
    {
        await using var h = await StartAsync();
        h.Activator.Behaviour = (_, _) => throw new InvalidOperationException("boom");

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(BuildConnectionRequest("alice"), SocketFlags.None);

        await AssertClosedAsync(client);
        Assert.Equal(1, h.Activator.Calls);
    }

    [Fact]
    public async Task BackendRefusesConnection_ClosesClient()
    {
        await using var h = await StartAsync();
        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var deadEp = (IPEndPoint)dead.LocalEndpoint;
        dead.Stop();
        h.Activator.Behaviour = (_, _) => Task.FromResult(deadEp);

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(BuildConnectionRequest("alice"), SocketFlags.None);

        await AssertClosedAsync(client);
    }

    [Fact]
    public async Task SlowActivation_StillRelays()
    {
        await using var h = await StartAsync();
        h.Activator.Behaviour = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return h.BackendEndPoint;
        };
        var cr = BuildConnectionRequest("alice");
        var extra = RandomBytes(1000, seed: 5);

        using var client = await h.ConnectClientAsync();
        await client.SendAsync(cr, SocketFlags.None);
        await client.SendAsync(extra, SocketFlags.None); // arrives while activation is pending

        using var backend = await h.AcceptBackendAsync();
        Assert.Equal(cr.Concat(extra).ToArray(), await ReadExactlyAsync(backend, cr.Length + extra.Length));

        await backend.SendAsync(extra, SocketFlags.None);
        Assert.Equal(extra, await ReadExactlyAsync(client, extra.Length));
    }

    [Fact]
    public async Task Stop_TearsDownActiveRelays()
    {
        var h = await StartAsync();
        try
        {
            var cr = BuildConnectionRequest("alice");
            using var client = await h.ConnectClientAsync();
            await client.SendAsync(cr, SocketFlags.None);
            using var backend = await h.AcceptBackendAsync();
            await ReadExactlyAsync(backend, cr.Length);
            Assert.Equal(1, h.Service.ActiveConnections);

            using var cts = new CancellationTokenSource(TestTimeout);
            await h.Service.StopAsync(cts.Token);

            Assert.Equal(0, h.Service.ActiveConnections);
            await AssertClosedAsync(client);
            await AssertClosedAsync(backend);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectionCap_ClosesExcessConnections()
    {
        await using var h = await StartAsync(maxConnections: 1);
        var release = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Activator.Behaviour = (_, _) => release.Task;

        using var first = await h.ConnectClientAsync();
        await first.SendAsync(BuildConnectionRequest("alice"), SocketFlags.None);
        await WaitUntilAsync(() => h.Activator.Calls == 1);

        using var second = await h.ConnectClientAsync();
        await AssertClosedAsync(second);
        Assert.Equal(1, h.Activator.Calls);

        release.SetResult(h.BackendEndPoint);
        using var backend = await h.AcceptBackendAsync();
        await ReadExactlyAsync(backend, BuildConnectionRequest("alice").Length);
    }

    private static Task WaitForNoActiveConnectionsAsync(RelayHarness h) =>
        WaitUntilAsync(() => h.Service.ActiveConnections == 0);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met before timeout");
            await Task.Delay(20);
        }
    }
}
