namespace Hotlab.Gateway.Activation;

/// <summary>
/// The hypervisor operations <see cref="VmActivator"/> needs. A thin seam over
/// the Proxmox API so activation logic can be tested without a cluster.
/// </summary>
public interface IVmControl
{
    /// <summary>Returns the VM's power status, e.g. "running" or "stopped".</summary>
    Task<string> GetStatusAsync(string node, int vmId, CancellationToken ct);

    /// <summary>
    /// Starts the VM and waits for the start task to finish.
    /// Returns the task's exit status ("OK" on success).
    /// </summary>
    Task<string> StartAndWaitAsync(string node, int vmId, CancellationToken ct);

    /// <summary>
    /// Returns the guest's non-loopback IPv4 addresses via the guest agent.
    /// Throws (or returns empty) while the agent is not yet available.
    /// </summary>
    Task<IReadOnlyList<string>> GetAgentIPv4AddressesAsync(string node, int vmId, CancellationToken ct);
}
