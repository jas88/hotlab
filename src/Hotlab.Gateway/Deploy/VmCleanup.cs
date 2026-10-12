namespace Hotlab.Gateway.Deploy;

/// <summary>Where a recorded VM stands and whether this deployment may destroy it.</summary>
/// <param name="Exists">The VMID exists in the cluster.</param>
/// <param name="Owned">It is in the deployment's pool (and tagged, when required).</param>
/// <param name="Node">Node currently hosting it (when it exists).</param>
internal sealed record VmOwnership(bool Exists, bool Owned, string? Node);

/// <summary>
/// Safety checks and destruction for VMs a deployment created. A VM is only ever destroyed
/// when it is a member of the deployment's pool (and, for teardown and re-run cleanup, also
/// carries the <see cref="DeployNames.Tag"/> tag), so a VMID reused by someone else is never touched.
/// </summary>
internal static class VmCleanup
{
    public static async Task<VmOwnership> CheckAsync(
        IDeployBackend backend, string pool, int vmId, bool requireTag, CancellationToken ct)
    {
        var node = await backend.FindVmNodeAsync(vmId, ct).ConfigureAwait(false);
        if (node is null)
            return new VmOwnership(false, false, null);

        if (!await backend.PoolExistsAsync(pool, ct).ConfigureAwait(false))
            return new VmOwnership(true, false, node);
        var members = await backend.GetPoolMembersAsync(pool, ct).ConfigureAwait(false);
        var member = members.FirstOrDefault(m => m.VmId == vmId);
        if (member is null)
            return new VmOwnership(true, false, node);

        node = string.IsNullOrEmpty(member.Node) ? node : member.Node;
        if (requireTag)
        {
            var tags = await backend.GetTagsAsync(node, vmId, ct).ConfigureAwait(false);
            if (!tags.Contains(DeployNames.Tag))
                return new VmOwnership(true, false, node);
        }
        return new VmOwnership(true, true, node);
    }

    /// <summary>Stops the VM if it is running, then destroys it.</summary>
    /// <exception cref="DeployException">Destruction did not succeed.</exception>
    public static async Task DestroyAsync(IDeployBackend backend, string node, int vmId, CancellationToken ct)
    {
        if (await backend.GetStatusAsync(node, vmId, ct).ConfigureAwait(false) != "stopped")
        {
            var stop = await backend.StopAsync(node, vmId, ct).ConfigureAwait(false);
            if (stop != "OK")
                throw new DeployException($"stopping VM {vmId} failed: {stop}");
        }

        var exit = await backend.DestroyAsync(node, vmId, ct).ConfigureAwait(false);
        if (exit != "OK")
            throw new DeployException($"destroying VM {vmId} failed: {exit}");
    }
}
