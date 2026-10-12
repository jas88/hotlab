using Hotlab.Gateway.Tokens;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Deploy;

/// <summary>Per-user clone, provision and failure handling.</summary>
public sealed partial class Deployer
{
    /// <summary>Clones one user's VM. Returns the row, or null when the user failed (already recorded).</summary>
    private async Task<DeployedUser?> CloneUserAsync(
        DeployRequest request, DeploymentState state, string user, bool isTemplate, CancellationToken ct)
    {
        var manifest = state.Manifest;
        var row = state.GetOrAdd(user);
        try
        {
            if (row.VmId is int previous)
                await RemoveLeftoverAsync(manifest, row, previous, ct).ConfigureAwait(false);

            int vmId = await _backend.GetNextVmIdAsync(ct).ConfigureAwait(false);
            var name = DeployNames.VmName(manifest.Name, user);
            state.Update(row, r =>
            {
                r.VmId = vmId;
                r.VmName = name;
                r.GuestAccount = request.GuestUser ?? user;
                r.Status = DeployStatus.Cloning;
                r.Error = null;
            });
            _log.Info($"{user}: cloning {manifest.SourceVmId} -> VM {vmId} ({name})...");

            var exit = await _backend.CloneAsync(
                manifest.Node, manifest.SourceVmId, vmId, name, fullClone: !isTemplate, manifest.Pool, ct)
                .ConfigureAwait(false);
            if (exit != "OK")
                throw new DeployException($"clone failed: {exit}");

            // Clones inherit the source's tags; add ours without dropping them.
            var tags = await _backend.GetTagsAsync(manifest.Node, vmId, ct).ConfigureAwait(false);
            if (!tags.Contains(DeployNames.Tag))
                await _backend.SetTagsAsync(manifest.Node, vmId, [.. tags, DeployNames.Tag], ct).ConfigureAwait(false);

            state.Update(row, r => r.Status = DeployStatus.Provisioning);
            return row;
        }
        catch (Exception ex) when (IsUserFailure(ex, ct))
        {
            await FailAsync(state, row, Describe(ex), ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task ProvisionUserAsync(
        DeployRequest request, DeploymentState state, DeployedUser row, CancellationToken ct)
    {
        var manifest = state.Manifest;
        int vmId = row.VmId!.Value;
        try
        {
            var password = DeployNames.GeneratePassword();
            _log.Info($"{row.User}: starting VM {vmId} and setting up guest account '{row.GuestAccount}'...");
            await _provisioner.ProvisionAsync(
                manifest.Node, vmId, row.GuestAccount, createAccount: request.GuestUser is null,
                password, request.LeaveRunning, ct).ConfigureAwait(false);

            var token = _tokens.Mint(row.User, request.Lifetime);
            var rdp = RdpFileRenderer.Render(manifest.Address, row.GuestAccount, token);
            var rdpFile = DeploymentOutputs.RdpFileName(row.User);
            state.RecordDeployed(row, new PasswordRow(row.User, row.GuestAccount, password, vmId, rdpFile), rdp);
            _log.Info($"{row.User}: deployed (VM {vmId}{(request.LeaveRunning ? ", left running" : ", shut down")}).");
        }
        catch (Exception ex) when (IsUserFailure(ex, ct))
        {
            await FailAsync(state, row, Describe(ex), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Records the failure and destroys the partial clone if it is in our pool.</summary>
    private async Task FailAsync(DeploymentState state, DeployedUser row, string error, CancellationToken ct)
    {
        _log.Error($"{row.User}: FAILED: {error}");
        state.MarkFailed(row, error);
        if (row.VmId is not int vmId)
            return;

        try
        {
            // Pool membership is enough here: the clone went straight into our pool, possibly
            // before it was tagged. A VMID taken by someone else is never in our pool.
            var check = await VmCleanup.CheckAsync(_backend, state.Manifest.Pool, vmId, requireTag: false, ct)
                .ConfigureAwait(false);
            if (!check.Exists)
                return;
            if (!check.Owned)
            {
                _log.Error($"{row.User}: VM {vmId} is not in pool {state.Manifest.Pool}; not destroying it.");
                return;
            }
            await VmCleanup.DestroyAsync(_backend, check.Node!, vmId, ct).ConfigureAwait(false);
            _log.Info($"{row.User}: destroyed partial clone VM {vmId}.");
        }
        catch (Exception ex) when (IsUserFailure(ex, ct))
        {
            _log.Error($"{row.User}: could not destroy partial clone VM {vmId}; clean it up manually: {Describe(ex)}");
        }
    }

    /// <summary>
    /// Before redeploying a user whose earlier VM is gone or failed: destroy a leftover VM if it
    /// is still ours (pool and tag); otherwise leave it alone.
    /// </summary>
    private async Task RemoveLeftoverAsync(DeploymentManifest manifest, DeployedUser row, int vmId, CancellationToken ct)
    {
        var check = await VmCleanup.CheckAsync(_backend, manifest.Pool, vmId, requireTag: true, ct).ConfigureAwait(false);
        if (!check.Exists)
            return;
        if (!check.Owned)
        {
            _log.Error($"{row.User}: earlier VM {vmId} still exists but is not in pool {manifest.Pool} " +
                $"with tag {DeployNames.Tag}; leaving it alone.");
            return;
        }
        _log.Info($"{row.User}: destroying leftover VM {vmId} from an earlier run...");
        await VmCleanup.DestroyAsync(_backend, check.Node!, vmId, ct).ConfigureAwait(false);
    }

    private static bool IsUserFailure(Exception ex, CancellationToken ct) =>
        ex is DeployException or ProxmoxApiException or HttpRequestException ||
        (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static string Describe(Exception ex) =>
        ex is ProxmoxApiException api ? $"Proxmox API error {(int)api.StatusCode}: {api.ResponseBody.Trim()}" : ex.Message;
}
