using System.Net;
using System.Net.Sockets;
using System.Text;
using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Relay;
using Hotlab.Gateway.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Relay;

/// <summary>Activator double that records calls and returns a configurable endpoint.</summary>
internal sealed class FakeActivator : IVmActivator
{
    private int _calls;

    public Func<VmTarget, CancellationToken, Task<IPEndPoint>> Behaviour { get; set; } =
        (_, _) => throw new InvalidOperationException("no behaviour configured");

    public int Calls => Volatile.Read(ref _calls);

    public VmTarget? LastTarget { get; private set; }

    public Task<IPEndPoint> EnsureReadyAsync(VmTarget target, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        LastTarget = target;
        return Behaviour(target, ct);
    }
}

/// <summary>Options monitor over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// Gateway listening on 127.0.0.1:0 with a fake activator pointing at a loopback
/// "guest" listener.
/// </summary>
internal sealed class RelayHarness : IAsyncDisposable
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private RelayHarness(RdpListenerService service, TcpListener backend, FakeActivator activator)
    {
        Service = service;
        Backend = backend;
        Activator = activator;
    }

    public RdpListenerService Service { get; }

    public TcpListener Backend { get; }

    public FakeActivator Activator { get; }

    public IPEndPoint BackendEndPoint => (IPEndPoint)Backend.LocalEndpoint;

    public static async Task<RelayHarness> StartAsync(int maxConnections = 0)
    {
        var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var activator = new FakeActivator();
        activator.Behaviour = (_, _) => Task.FromResult((IPEndPoint)backend.LocalEndpoint);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hotlab:Users:alice:Node"] = "pve1",
            ["Hotlab:Users:alice:VmId"] = "101",
            ["Hotlab:Users:johnsmith:Node"] = "pve2",
            ["Hotlab:Users:johnsmith:VmId"] = "202",
        }).Build();
        var users = new UserMapOptions();
        config.GetSection(UserMapOptions.SectionName).Bind(users);
        var resolver = new ConfigUserTargetResolver(
            new StaticOptionsMonitor<UserMapOptions>(users), NullLogger<ConfigUserTargetResolver>.Instance);
        var options = Options.Create(new GatewayOptions
        {
            Listen = "127.0.0.1:0",
            FirstPduTimeout = TimeSpan.FromSeconds(2),
            MaxConnections = maxConnections,
        });
        var handler = new RdpConnectionHandler(
            resolver, activator, options, NullLogger<RdpConnectionHandler>.Instance);
        var service = new RdpListenerService(handler, options, NullLogger<RdpListenerService>.Instance);
        await service.StartAsync(CancellationToken.None);
        return new RelayHarness(service, backend, activator);
    }

    public async Task<Socket> ConnectClientAsync()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(Service.LocalEndPoint!);
        return socket;
    }

    public async Task<Socket> AcceptBackendAsync()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        return await Backend.AcceptSocketAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await Service.StopAsync(cts.Token);
        Service.Dispose();
        Backend.Stop();
        Backend.Dispose();
    }

    /// <summary>
    /// Builds a TPKT + X.224 Connection Request by hand, with an optional mstshash cookie
    /// and an RDP_NEG_REQ asking for TLS|CredSSP.
    /// </summary>
    public static byte[] BuildConnectionRequest(string? user)
    {
        var cookie = user is null ? [] : Encoding.ASCII.GetBytes($"Cookie: mstshash={user}\r\n");
        byte[] negReq = [0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00];
        var li = 6 + cookie.Length + negReq.Length;
        var total = 4 + 1 + li;

        var pdu = new List<byte> { 0x03, 0x00, (byte)(total >> 8), (byte)total };
        pdu.Add((byte)li);
        pdu.AddRange([0xE0, 0x00, 0x00, 0x00, 0x00, 0x00]);
        pdu.AddRange(cookie);
        pdu.AddRange(negReq);
        return [.. pdu];
    }

    public static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes or fails on timeout/EOF.</summary>
    public static async Task<byte[]> ReadExactlyAsync(Socket socket, int count)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cts.Token);
            Assert.True(n > 0, $"EOF after {read} of {count} bytes");
            read += n;
        }

        return buffer;
    }

    /// <summary>Asserts the peer closed: EOF or reset within the timeout.</summary>
    public static async Task AssertClosedAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
                if (n == 0)
                    return;
            }
        }
        catch (SocketException)
        {
            // Reset also counts as closed.
        }
    }

    /// <summary>Asserts the next read returns EOF (orderly FIN, no further data).</summary>
    public static async Task AssertEofAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var n = await socket.ReceiveAsync(new byte[16], SocketFlags.None, cts.Token);
        Assert.Equal(0, n);
    }
}
