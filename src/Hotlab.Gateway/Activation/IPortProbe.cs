using System.Net;

namespace Hotlab.Gateway.Activation;

/// <summary>Checks whether a TCP endpoint accepts connections.</summary>
public interface IPortProbe
{
    /// <summary>
    /// Attempts one connection. Returns true if it was accepted, false if refused,
    /// unreachable, or not accepted within <paramref name="timeout"/>.
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<bool> CanConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken ct);
}
