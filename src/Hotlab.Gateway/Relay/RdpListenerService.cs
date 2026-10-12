using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Relay;

/// <summary>
/// Accepts RDP connections on <see cref="GatewayOptions.Listen"/> and serves each with
/// <see cref="RdpConnectionHandler"/> on its own task. On shutdown, stops accepting,
/// cancels in-flight connections and waits for them to finish.
/// </summary>
public sealed class RdpListenerService : BackgroundService
{
    private readonly RdpConnectionHandler _handler;
    private readonly GatewayOptions _options;
    private readonly ILogger<RdpListenerService> _logger;
    private readonly ConcurrentDictionary<long, Task> _active = new();
    private Socket? _listener;
    private long _nextId;

    /// <summary>Creates the listener service.</summary>
    public RdpListenerService(
        RdpConnectionHandler handler,
        IOptions<GatewayOptions> options,
        ILogger<RdpListenerService> logger)
    {
        _handler = handler;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The bound endpoint once started (useful when listening on port 0).</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>Number of connections currently being served.</summary>
    public int ActiveConnections => _active.Count;

    /// <summary>Binds the listen socket, then starts the accept loop.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var ep = _options.GetListenEndPoint();
        var listener = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (ep.AddressFamily == AddressFamily.InterNetworkV6 && ep.Address.Equals(IPAddress.IPv6Any))
                listener.DualMode = true;
            listener.Bind(ep);
            listener.Listen(512);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        LocalEndPoint = (IPEndPoint)listener.LocalEndPoint!;
        _logger.LogInformation("RDP gateway listening on {Endpoint}", LocalEndPoint);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = _listener ?? throw new InvalidOperationException("StartAsync was not called.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // Transient accept failures (e.g. ECONNABORTED, EMFILE) must not kill the loop.
                    _logger.LogWarning(ex, "Accept failed");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_options.MaxConnections > 0 && _active.Count >= _options.MaxConnections)
                {
                    _logger.LogWarning("Rejecting {Remote}: connection limit {Limit} reached",
                        client.RemoteEndPoint, _options.MaxConnections);
                    client.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref _nextId);
                // Gate the handler until it is registered, so its own removal cannot race the add.
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _active[id] = ServeAsync(id, client, registered.Task, stoppingToken);
                registered.SetResult();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Dispose();
            var draining = _active.Values.ToArray();
            if (draining.Length > 0)
            {
                _logger.LogInformation("Draining {Count} connection(s)", draining.Length);
                await Task.WhenAll(draining).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(long id, Socket client, Task registered, CancellationToken ct)
    {
        try
        {
            await registered.ConfigureAwait(false);
            await _handler.HandleAsync(client, ct).ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _listener?.Dispose();
        base.Dispose();
    }
}
