using System.Text.Json;

namespace Hotlab.Proxmox;

/// <summary>Resource pool operations (<c>/pools</c>).</summary>
public sealed partial class ProxmoxClient
{
    /// <summary>Returns the ids of all pools visible to this token (<c>GET /pools</c>).</summary>
    public async Task<IReadOnlyList<string>> ListPoolsAsync(CancellationToken ct = default)
    {
        using var data = await GetAsync("/pools", ct).ConfigureAwait(false);
        var pools = new List<string>();
        foreach (var pool in data.RootElement.EnumerateArray())
        {
            if (pool.TryGetProperty("poolid", out var id) && id.GetString() is { Length: > 0 } poolId)
                pools.Add(poolId);
        }
        return pools;
    }

    /// <summary>True if the pool exists (and is visible to this token).</summary>
    public async Task<bool> PoolExistsAsync(string poolId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(poolId);
        var pools = await ListPoolsAsync(ct).ConfigureAwait(false);
        return pools.Contains(poolId, StringComparer.Ordinal);
    }

    /// <summary>Creates a pool (<c>POST /pools</c> with <c>poolid</c>).</summary>
    public async Task CreatePoolAsync(string poolId, string? comment = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(poolId);
        var form = new Dictionary<string, string> { ["poolid"] = poolId };
        if (comment is not null)
            form["comment"] = comment;
        using var _ = await PostAsync("/pools", form, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a pool (<c>DELETE /pools/{poolid}</c>). Proxmox refuses to delete a pool
    /// that still has members.
    /// </summary>
    public async Task DeletePoolAsync(string poolId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(poolId);
        using var _ = await SendAsync(HttpMethod.Delete, $"/pools/{Uri.EscapeDataString(poolId)}", form: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Returns the pool's members (<c>GET /pools/{poolid}</c>).</summary>
    public async Task<IReadOnlyList<ProxmoxPoolMember>> GetPoolMembersAsync(
        string poolId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(poolId);
        using var data = await GetAsync($"/pools/{Uri.EscapeDataString(poolId)}", ct).ConfigureAwait(false);
        var members = new List<ProxmoxPoolMember>();
        if (!data.RootElement.TryGetProperty("members", out var list) || list.ValueKind != JsonValueKind.Array)
            return members;

        foreach (var m in list.EnumerateArray())
        {
            int? vmId = m.TryGetProperty("vmid", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            members.Add(new ProxmoxPoolMember(
                StringProperty(m, "id"),
                StringProperty(m, "type"),
                vmId,
                m.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null));
        }

        return members;
    }
}
