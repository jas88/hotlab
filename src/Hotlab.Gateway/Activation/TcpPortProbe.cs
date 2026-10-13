using System.Net;
using System.Net.Sockets;

namespace Hotlab.Gateway.Activation;

/// <summary><see cref="IPortProbe"/> that opens (and immediately closes) a real TCP connection.</summary>
public sealed class TcpPortProbe : IPortProbe
{
    /// <inheritdoc />
    public async Task<bool> CanConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
