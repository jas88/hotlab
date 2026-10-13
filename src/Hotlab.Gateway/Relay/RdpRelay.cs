using System.Buffers;
using System.Net.Sockets;

namespace Hotlab.Gateway.Relay;

/// <summary>Outcome of a bidirectional splice.</summary>
/// <param name="ClientToBackendBytes">Bytes copied client → backend.</param>
/// <param name="BackendToClientBytes">Bytes copied backend → client.</param>
/// <param name="Error">The first failure that tore the relay down, if any.</param>
public sealed record RelayResult(long ClientToBackendBytes, long BackendToClientBytes, Exception? Error);

/// <summary>Byte-for-byte TCP splice between two connected sockets.</summary>
public static class RdpRelay
{
    private const int BufferSize = 16 * 1024;

    /// <summary>
    /// Copies data in both directions until both have reached EOF. When one side sends
    /// FIN, the other side's send direction is shut down (half-close) and the opposite
    /// direction keeps flowing for at most <paramref name="halfCloseTimeout"/> (unbounded
    /// if null). If either direction fails, or <paramref name="ct"/> is cancelled, both
    /// directions are aborted. The caller still owns (and must dispose) both sockets.
    /// </summary>
    public static async Task<RelayResult> RunAsync(
        Socket client, Socket backend, CancellationToken ct, TimeSpan? halfCloseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(backend);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = new Counter();
        var down = new Counter();

        var upTask = PumpAsync(client, backend, up, cts.Token);
        var downTask = PumpAsync(backend, client, down, cts.Token);

        Exception? error = null;
        var first = await Task.WhenAny(upTask, downTask).ConfigureAwait(false);
        if (!first.IsCompletedSuccessfully)
        {
            // A cancelled task carries no Exception; record the cancellation it observed.
            error = first.IsCanceled
                ? new OperationCanceledException(cts.Token)
                : first.Exception?.GetBaseException();
            await cts.CancelAsync().ConfigureAwait(false);
        }
        else if (halfCloseTimeout is { } timeout)
        {
            cts.CancelAfter(timeout);
        }

        var second = first == upTask ? downTask : upTask;
        try
        {
            await second.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error ??= ex;
            await cts.CancelAsync().ConfigureAwait(false);
        }

        // Only a pump that actually observed cancellation reports it: if both directions
        // finished cleanly, a late cancellation of ct is not an error.
        if (error is OperationCanceledException)
        {
            error = ct.IsCancellationRequested
                ? new OperationCanceledException(ct)
                : new TimeoutException("Half-closed connection did not finish in time.");
        }

        return new RelayResult(up.Value, down.Value, error);
    }

    /// <summary>
    /// Sends all of <paramref name="data"/>, looping over partial sends (a single
    /// <see cref="Socket.SendAsync(ReadOnlyMemory{byte}, SocketFlags, CancellationToken)"/>
    /// may accept fewer bytes than offered).
    /// </summary>
    public static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        while (!data.IsEmpty)
        {
            var n = await socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            data = data[n..];
        }
    }

    private static async Task PumpAsync(Socket from, Socket to, Counter counter, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var n = await from.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false);
                if (n == 0)
                    break;

                var sent = 0;
                while (sent < n)
                {
                    var chunk = await to.SendAsync(buffer.AsMemory(sent, n - sent), SocketFlags.None, ct)
                        .ConfigureAwait(false);
                    sent += chunk;
                    counter.Value += chunk; // count what reached the socket even if a later send fails
                }
            }

            // Propagate the FIN; the other direction keeps running.
            try
            {
                to.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // Peer already gone; the other pump will observe it.
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class Counter
    {
        public long Value;
    }
}
