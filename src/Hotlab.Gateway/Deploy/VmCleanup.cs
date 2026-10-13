namespace Hotlab.Gateway.Deploy;

/// <summary>Where a recorded VM stands and whether this deployment may destroy it.</summary>
/// <param name="Exists">The VMID exists in the cluster.</param>
/// <param name="Owned">It satisfies the <see cref="VmOwnershipRule"/> it was checked against.</param>
/// <param name="Node">Node currently hosting it (when it exists).</param>
internal sealed record VmOwnership(bool Exists, bool Owned, string? Node);

/// <summary>What proves a VM belongs to a deployment.</summary>
/// <param name="Pool">The VM must be a member of this pool.</param>
/// <param name="RequireTag">The VM must also carry the <see cref="DeployNames.Tag"/> tag.</param>
/// <param name="ExpectedName">When set, the VM's name must be exactly this.</param>
internal sealed record VmOwnershipRule(string Pool, bool RequireTag, string? ExpectedName)
{
    public string Describe() =>
        $"in pool {Pool}" + (RequireTag ? $" with tag {DeployNames.Tag}" : "") +
        (ExpectedName is null ? "" : $" named {ExpectedName}");
}

/// <summary>
/// Safety checks and destruction for VMs a deployment created. A VM is only ever destroyed
/// when it satisfies a <see cref="VmOwnershipRule"/> (always pool membership; plus the
/// <see cref="DeployNames.Tag"/> tag and/or the expected name), re-checked immediately before
/// the destroy call, so a VMID reused by someone else is never touched.
/// </summary>
internal static class VmCleanup
{
    public static async Task<VmOwnership> CheckAsync(
        IDeployBackend backend, VmOwnershipRule rule, int vmId, CancellationToken ct)
    {
        var node = await backend.FindVmNodeAsync(vmId, ct).ConfigureAwait(false);
        if (node is null)
            return new VmOwnership(false, false, null);

        if (!await backend.PoolExistsAsync(rule.Pool, ct).ConfigureAwait(false))
            return new VmOwnership(true, false, node);
        var members = await backend.GetPoolMembersAsync(rule.Pool, ct).ConfigureAwait(false);
        var member = members.FirstOrDefault(m => m.VmId == vmId);
        if (member is null)
            return new VmOwnership(true, false, node);

        node = string.IsNullOrEmpty(member.Node) ? node : member.Node;
        if (rule.RequireTag)
        {
            var tags = await backend.GetTagsAsync(node, vmId, ct).ConfigureAwait(false);
            if (!tags.Contains(DeployNames.Tag))
                return new VmOwnership(true, false, node);
        }
        if (rule.ExpectedName is { } expected &&
            !string.Equals(await backend.GetNameAsync(node, vmId, ct).ConfigureAwait(false), expected, StringComparison.Ordinal))
        {
            return new VmOwnership(true, false, node);
        }
        return new VmOwnership(true, true, node);
    }

    /// <summary>
    /// Stops the VM if it is running, re-verifies ownership, then destroys it. Callers check
    /// ownership first (<see cref="CheckAsync"/>); the re-check closes the window in which the
    /// VMID could have been freed and reused while the VM was being stopped.
    /// </summary>
    /// <exception cref="DeployException">Destruction did not succeed, or the VM is no longer ours.</exception>
    public static async Task DestroyAsync(
        IDeployBackend backend, VmOwnershipRule rule, string node, int vmId, CancellationToken ct)
    {
        if (await backend.GetStatusAsync(node, vmId, ct).ConfigureAwait(false) != "stopped")
        {
            var stop = await backend.StopAsync(node, vmId, ct).ConfigureAwait(false);
            if (stop != "OK")
                throw new DeployException($"stopping VM {vmId} failed: {stop}");
        }

        var recheck = await CheckAsync(backend, rule, vmId, ct).ConfigureAwait(false);
        if (!recheck.Exists)
            return;
        if (!recheck.Owned)
            throw new DeployException($"VM {vmId} is no longer {rule.Describe()}; not destroying it");

        var exit = await backend.DestroyAsync(recheck.Node!, vmId, ct).ConfigureAwait(false);
        if (exit != "OK")
            throw new DeployException($"destroying VM {vmId} failed: {exit}");
    }
}
