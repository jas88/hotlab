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
        var row = state.GetOrAdd(user, request.GuestUser ?? user);
        var name = DeployNames.VmName(manifest.Name, user);
        // Set once this attempt may have created a VM; only then does failure cleanup run.
        int? createdVmId = null;
        try
        {
            if (row.VmId is int previous)
                await RemoveLeftoverAsync(state, row, previous, ct).ConfigureAwait(false);

            // /cluster/nextid is not a reservation: another client can take the id before our
            // clone request, which Proxmox then rejects.
            int vmId = await _backend.GetNextVmIdAsync(ct).ConfigureAwait(false);
            // Recorded before the request, so a clone that is interrupted or fails before it is
            // tagged is recognised on a re-run by pool and name (see RemoveLeftoverAsync).
            state.Update(row, r =>
            {
                r.VmId = vmId;
                r.VmName = name;
                r.GuestAccount = request.GuestUser ?? user;
                r.Status = DeployStatus.Cloning;
                r.UntaggedClone = true;
                r.Error = null;
            });
            _log.Info($"{user}: cloning {manifest.SourceVmId} -> VM {vmId} ({name})...");

            string task;
            try
            {
                task = await _backend.StartCloneAsync(
                    manifest.Node, manifest.SourceVmId, vmId, name, fullClone: !isTemplate, manifest.Pool, ct)
                    .ConfigureAwait(false);
            }
            catch (ProxmoxApiException ex) when (IsDefiniteRejection(ex))
            {
                // Rejected outright (a 4xx): nothing of ours exists under that id, so stop
                // pointing the row at it.
                state.Update(row, r =>
                {
                    r.VmId = null;
                    r.VmName = null;
                    r.UntaggedClone = false;
                });
                throw;
            }
            catch (Exception ex) when (IsUserFailure(ex, ct))
            {
                // A 5xx (e.g. a proxy's 502/504) or transport error: Proxmox may have accepted
                // and started the clone, so keep the row and clean up by pool and name.
                createdVmId = vmId;
                throw;
            }
            createdVmId = vmId;

            var exit = await _backend.WaitForTaskAsync(manifest.Node, task, ct).ConfigureAwait(false);
            if (exit != "OK")
                throw new DeployException($"clone failed: {exit}");

            // Clones inherit the source's tags; add ours without dropping them.
            var tags = await _backend.GetTagsAsync(manifest.Node, vmId, ct).ConfigureAwait(false);
            if (!tags.Contains(DeployNames.Tag))
                await _backend.SetTagsAsync(manifest.Node, vmId, [.. tags, DeployNames.Tag], ct).ConfigureAwait(false);

            state.Update(row, r =>
            {
                r.Status = DeployStatus.Provisioning;
                r.UntaggedClone = false;
            });
            return row;
        }
        catch (Exception ex) when (IsUserFailure(ex, ct))
        {
            await FailAsync(state, row, Describe(ex), createdVmId, name, ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task ProvisionUserAsync(
        DeployRequest request, DeploymentState state, DeployedUser row, CancellationToken ct)
    {
        var manifest = state.Manifest;
        int vmId = row.VmId!.Value;
        var name = row.VmName!;
        try
        {
            var password = DeployNames.GeneratePassword();
            _log.Info($"{row.User}: starting VM {vmId} and setting up guest account '{row.GuestAccount}'...");
            await _provisioner.ProvisionAsync(
                manifest.Node, vmId, row.GuestAccount, createAccount: request.GuestUser is null,
                password, request.LeaveRunning, ct).ConfigureAwait(false);

            var token = _tokens.Mint(row.User, request.Lifetime);
            var rdp = RdpFileRenderer.Render(request.Address, row.GuestAccount, token);
            var rdpFile = DeploymentOutputs.RdpFileName(row.User);
            state.RecordDeployed(row, new PasswordRow(row.User, row.GuestAccount, password, vmId, rdpFile), rdp);
            _log.Info($"{row.User}: deployed (VM {vmId}{(request.LeaveRunning ? ", left running" : ", shut down")}).");
        }
        catch (Exception ex) when (IsUserFailure(ex, ct))
        {
            await FailAsync(state, row, Describe(ex), vmId, name, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records the failure and, when this attempt created (or may have created) VM
    /// <paramref name="createdVmId"/>, destroys it if it is in our pool with the expected name.
    /// </summary>
    private async Task FailAsync(
        DeploymentState state, DeployedUser row, string error, int? createdVmId, string expectedName, CancellationToken ct)
    {
        _log.Error($"{row.User}: FAILED: {error}");
        state.MarkFailed(row, error);
        if (createdVmId is not int vmId)
            return;

        try
        {
            // No tag needed: the clone went straight into our pool, possibly before it was
            // tagged. The name guards against a VMID someone else took after ours was freed.
            var rule = new VmOwnershipRule(state.Manifest.Pool, RequireTag: false, expectedName);
            var check = await VmCleanup.CheckAsync(_backend, rule, vmId, ct).ConfigureAwait(false);
            if (!check.Exists)
                return;
            if (!check.Owned)
            {
                _log.Error($"{row.User}: VM {vmId} is not {rule.Describe()}; not destroying it.");
                return;
            }
            await VmCleanup.DestroyAsync(_backend, rule, check.Node!, vmId, ct).ConfigureAwait(false);
            // Gone: nothing is left for the relaxed (untagged) ownership rule to cover.
            state.Update(row, r => r.UntaggedClone = false);
            _log.Info($"{row.User}: destroyed partial clone VM {vmId}.");
        }
        catch (Exception ex) when (IsUserFailure(ex, ct))
        {
            // The row keeps UntaggedClone, so a re-run still recognises (and retries removing)
            // an untagged partial clone by pool and name.
            _log.Error($"{row.User}: could not destroy partial clone VM {vmId} (a re-run or teardown retries; otherwise clean it up manually): {Describe(ex)}");
        }
    }

    /// <summary>
    /// Before redeploying a user whose earlier VM is gone, failed or no longer ours: destroy a
    /// leftover VM if it is still ours, otherwise leave it alone. A clone that was interrupted,
    /// or failed and could not be cleaned up, before it was tagged is recognised by pool
    /// membership and the recorded name (<see cref="DeployedUser.UntaggedClone"/>, or a row still
    /// in the cloning state); otherwise the tag is required too.
    /// </summary>
    private async Task RemoveLeftoverAsync(DeploymentState state, DeployedUser row, int vmId, CancellationToken ct)
    {
        var manifest = state.Manifest;
        bool interruptedClone = (row.UntaggedClone || row.Status == DeployStatus.Cloning) && row.VmName is not null;
        var rule = new VmOwnershipRule(manifest.Pool, RequireTag: !interruptedClone, row.VmName);
        var check = await VmCleanup.CheckAsync(_backend, rule, vmId, ct).ConfigureAwait(false);
        if (!check.Exists)
            return;
        if (!check.Owned)
        {
            _log.Error($"{row.User}: earlier VM {vmId} still exists but is not {rule.Describe()}; leaving it alone.");
            return;
        }
        _log.Info($"{row.User}: destroying leftover VM {vmId} from an earlier run...");
        await VmCleanup.DestroyAsync(_backend, rule, check.Node!, vmId, ct).ConfigureAwait(false);
        state.Update(row, r => r.UntaggedClone = false);
    }

    /// <summary>
    /// True when Proxmox definitely refused the request: a 4xx other than 408 (timeout) and 429
    /// (throttled), which a proxy may return without knowing whether the request went through.
    /// </summary>
    private static bool IsDefiniteRejection(ProxmoxApiException ex) =>
        (int)ex.StatusCode is >= 400 and < 500 and not 408 and not 429;

    /// <summary>Any failure is the user's alone, except cancellation of the whole run.</summary>
    private static bool IsUserFailure(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;

    private static string Describe(Exception ex) =>
        ex is ProxmoxApiException api ? $"Proxmox API error {(int)api.StatusCode}: {api.ResponseBody.Trim()}" : ex.Message;
}
