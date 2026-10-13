using Hotlab.Proxmox;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// The Proxmox operations <see cref="Deployer"/> and <see cref="Teardown"/> need. A thin seam
/// over <see cref="ProxmoxClient"/> so the orchestration can be tested without a cluster: one
/// API call each, and task-producing calls wait for the task and return its exit status, except
/// cloning, which is split into <see cref="StartCloneAsync"/> (returns the task id once Proxmox
/// has accepted the request) and <see cref="WaitForTaskAsync"/>, so the caller knows whether a
/// failure happened before or after a VM may have been created.
/// </summary>
public interface IDeployBackend
{
    /// <summary>Next free VMID in the cluster.</summary>
    Task<int> GetNextVmIdAsync(CancellationToken ct);

    /// <summary>True if the VM is a template.</summary>
    Task<bool> IsTemplateAsync(string node, int vmId, CancellationToken ct);

    /// <summary>True if the pool exists.</summary>
    Task<bool> PoolExistsAsync(string pool, CancellationToken ct);

    /// <summary>Creates a pool.</summary>
    Task CreatePoolAsync(string pool, string comment, CancellationToken ct);

    /// <summary>Deletes an (empty) pool.</summary>
    Task DeletePoolAsync(string pool, CancellationToken ct);

    /// <summary>The pool's members.</summary>
    Task<IReadOnlyList<ProxmoxPoolMember>> GetPoolMembersAsync(string pool, CancellationToken ct);

    /// <summary>
    /// Starts cloning <paramref name="sourceVmId"/> into <paramref name="pool"/>. Returns the task
    /// id once Proxmox has accepted the request (throws if it was rejected, e.g. the VMID is taken).
    /// </summary>
    Task<string> StartCloneAsync(
        string node, int sourceVmId, int newVmId, string name, bool fullClone, string pool, CancellationToken ct);

    /// <summary>Waits for a task started by <see cref="StartCloneAsync"/>; returns its exit status.</summary>
    Task<string> WaitForTaskAsync(string node, string taskId, CancellationToken ct);

    /// <summary>The VM's tags (case-insensitive set).</summary>
    Task<IReadOnlySet<string>> GetTagsAsync(string node, int vmId, CancellationToken ct);

    /// <summary>The VM's configured name, or null if it has none.</summary>
    Task<string?> GetNameAsync(string node, int vmId, CancellationToken ct);

    /// <summary>Replaces the VM's tags.</summary>
    Task SetTagsAsync(string node, int vmId, IReadOnlyCollection<string> tags, CancellationToken ct);

    /// <summary>The node currently hosting the VM, or null if it does not exist.</summary>
    Task<string?> FindVmNodeAsync(int vmId, CancellationToken ct);

    /// <summary>Power status, e.g. "running" or "stopped".</summary>
    Task<string> GetStatusAsync(string node, int vmId, CancellationToken ct);

    /// <summary>Starts the VM and waits; returns the task exit status.</summary>
    Task<string> StartAsync(string node, int vmId, CancellationToken ct);

    /// <summary>Requests a clean shutdown (Proxmox gives up after <paramref name="timeout"/>) and waits.</summary>
    Task<string> ShutdownAsync(string node, int vmId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Hard-stops the VM and waits; returns the task exit status.</summary>
    Task<string> StopAsync(string node, int vmId, CancellationToken ct);

    /// <summary>Destroys the VM and its disks and waits; returns the task exit status.</summary>
    Task<string> DestroyAsync(string node, int vmId, CancellationToken ct);

    /// <summary>True when the guest agent answers.</summary>
    Task<bool> AgentPingAsync(string node, int vmId, CancellationToken ct);

    /// <summary>Guest OS identity.</summary>
    Task<AgentOsInfo> AgentGetOsInfoAsync(string node, int vmId, CancellationToken ct);

    /// <summary>Starts a guest program (argv, no shell); returns its PID.</summary>
    Task<int> AgentExecAsync(string node, int vmId, IReadOnlyList<string> command, CancellationToken ct);

    /// <summary>State of a guest program.</summary>
    Task<AgentExecStatus> AgentExecStatusAsync(string node, int vmId, int pid, CancellationToken ct);

    /// <summary>Sets a guest account's password (plain text, sent only in the request body).</summary>
    Task AgentSetUserPasswordAsync(string node, int vmId, string user, string password, CancellationToken ct);
}
