using System.Net;
using Hotlab.Gateway.Routing;

namespace Hotlab.Gateway.Activation;

/// <summary>Ensures a target VM is running with its RDP listener reachable.</summary>
public interface IVmActivator
{
    /// <summary>
    /// Starts the VM if needed and waits until its RDP port accepts TCP connections.
    /// Concurrent calls for the same VM share one activation.
    /// </summary>
    Task<IPEndPoint> EnsureReadyAsync(VmTarget target, CancellationToken ct);
}
