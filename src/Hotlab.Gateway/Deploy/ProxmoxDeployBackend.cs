using Hotlab.Proxmox;

namespace Hotlab.Gateway.Deploy;

/// <summary><see cref="IDeployBackend"/> backed by a <see cref="ProxmoxClient"/>.</summary>
public sealed class ProxmoxDeployBackend : IDeployBackend, IDisposable
{
    private static readonly char[] TagSeparators = [';', ',', ' '];

    private readonly ProxmoxClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan? _taskPollInterval;

    /// <summary>Wraps a client.</summary>
    /// <param name="client">The Proxmox API client.</param>
    /// <param name="ownsClient">Dispose the client with this backend.</param>
    /// <param name="taskPollInterval">Task polling interval; null for the client default.</param>
    public ProxmoxDeployBackend(ProxmoxClient client, bool ownsClient = true, TimeSpan? taskPollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = ownsClient;
        _taskPollInterval = taskPollInterval;
    }

    /// <summary>Parses a Proxmox <c>tags</c> value ("a;b", older releases also ',' or ' ').</summary>
    public static IReadOnlySet<string> ParseTags(string? tags) =>
        (tags ?? "").Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task<int> GetNextVmIdAsync(CancellationToken ct) => _client.GetNextVmIdAsync(ct);

    public Task<bool> IsTemplateAsync(string node, int vmId, CancellationToken ct) =>
        _client.IsTemplateAsync(node, vmId, ct);

    public Task<bool> PoolExistsAsync(string pool, CancellationToken ct) => _client.PoolExistsAsync(pool, ct);

    public Task CreatePoolAsync(string pool, string comment, CancellationToken ct) =>
        _client.CreatePoolAsync(pool, comment, ct);

    public Task DeletePoolAsync(string pool, CancellationToken ct) => _client.DeletePoolAsync(pool, ct);

    public Task<IReadOnlyList<ProxmoxPoolMember>> GetPoolMembersAsync(string pool, CancellationToken ct) =>
        _client.GetPoolMembersAsync(pool, ct);

    public async Task<string> CloneAsync(
        string node, int sourceVmId, int newVmId, string name, bool fullClone, string pool, CancellationToken ct)
    {
        var upid = await _client.CloneVmAsync(node, sourceVmId, newVmId, name, fullClone, pool, ct).ConfigureAwait(false);
        return await WaitAsync(node, upid, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetTagsAsync(string node, int vmId, CancellationToken ct)
    {
        var config = await _client.GetVmConfigAsync(node, vmId, ct).ConfigureAwait(false);
        return ParseTags(config.GetValueOrDefault("tags"));
    }

    public Task SetTagsAsync(string node, int vmId, IReadOnlyCollection<string> tags, CancellationToken ct) =>
        _client.SetVmConfigAsync(node, vmId, new Dictionary<string, string> { ["tags"] = string.Join(';', tags) }, ct);

    public Task<string?> FindVmNodeAsync(int vmId, CancellationToken ct) => _client.FindVmNodeAsync(vmId, ct);

    public Task<string> GetStatusAsync(string node, int vmId, CancellationToken ct) =>
        _client.GetVmStatusAsync(node, vmId, ct);

    public async Task<string> StartAsync(string node, int vmId, CancellationToken ct) =>
        await WaitAsync(node, await _client.StartVmAsync(node, vmId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<string> ShutdownAsync(string node, int vmId, TimeSpan timeout, CancellationToken ct) =>
        await WaitAsync(node, await _client.ShutdownVmAsync(node, vmId, timeout, forceStop: false, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

    public async Task<string> StopAsync(string node, int vmId, CancellationToken ct) =>
        await WaitAsync(node, await _client.StopVmAsync(node, vmId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<string> DestroyAsync(string node, int vmId, CancellationToken ct) =>
        await WaitAsync(node, await _client.DestroyVmAsync(node, vmId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public Task<bool> AgentPingAsync(string node, int vmId, CancellationToken ct) => _client.AgentPingAsync(node, vmId, ct);

    public Task<AgentOsInfo> AgentGetOsInfoAsync(string node, int vmId, CancellationToken ct) =>
        _client.AgentGetOsInfoAsync(node, vmId, ct);

    public Task<int> AgentExecAsync(string node, int vmId, IReadOnlyList<string> command, CancellationToken ct) =>
        _client.AgentExecAsync(node, vmId, command, inputData: null, ct);

    public Task<AgentExecStatus> AgentExecStatusAsync(string node, int vmId, int pid, CancellationToken ct) =>
        _client.AgentExecStatusAsync(node, vmId, pid, ct);

    public Task AgentSetUserPasswordAsync(string node, int vmId, string user, string password, CancellationToken ct) =>
        _client.AgentSetUserPasswordAsync(node, vmId, user, password, crypted: false, ct);

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private Task<string> WaitAsync(string node, string upid, CancellationToken ct) =>
        _client.WaitForTaskAsync(node, upid, _taskPollInterval, ct);
}
