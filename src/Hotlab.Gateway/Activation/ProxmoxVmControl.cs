using Hotlab.Proxmox;

namespace Hotlab.Gateway.Activation;

/// <summary><see cref="IVmControl"/> backed by a <see cref="ProxmoxClient"/>.</summary>
public sealed class ProxmoxVmControl : IVmControl
{
    private readonly ProxmoxClient _client;
    private readonly TimeSpan? _taskPollInterval;

    /// <summary>Wraps an existing client (not owned; the caller disposes it).</summary>
    /// <param name="client">The Proxmox API client.</param>
    /// <param name="taskPollInterval">Interval for polling the start task; null for the client default.</param>
    public ProxmoxVmControl(ProxmoxClient client, TimeSpan? taskPollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _taskPollInterval = taskPollInterval;
    }

    /// <inheritdoc />
    public Task<string> GetStatusAsync(string node, int vmId, CancellationToken ct) =>
        _client.GetVmStatusAsync(node, vmId, ct);

    /// <inheritdoc />
    public async Task<string> StartAndWaitAsync(string node, int vmId, CancellationToken ct)
    {
        var upid = await _client.StartVmAsync(node, vmId, ct).ConfigureAwait(false);
        return await _client.WaitForTaskAsync(node, upid, _taskPollInterval, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetAgentIPv4AddressesAsync(string node, int vmId, CancellationToken ct) =>
        _client.AgentGetIpAddressesAsync(node, vmId, ct);
}
