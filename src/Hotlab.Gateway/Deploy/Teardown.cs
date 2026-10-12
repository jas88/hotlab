using System.Globalization;
using System.Text.Json;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// Destroys a deployment recorded in <c>deployment.json</c>: every VM that is still in the
/// deployment's pool and tagged <see cref="DeployNames.Tag"/>, then the pool once empty, the
/// gateway mappings (unless kept) and the passwords of destroyed VMs.
/// </summary>
public sealed class Teardown
{
    private readonly IDeployBackend _backend;
    private readonly TimeProvider _time;
    private readonly DeployLog _log;

    /// <summary>Creates a teardown runner.</summary>
    public Teardown(IDeployBackend backend, TextWriter stdout, TextWriter stderr, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _time = timeProvider ?? TimeProvider.System;
        _log = new DeployLog(stdout, stderr);
    }

    /// <summary>Runs the teardown. Returns 0 when everything was removed, 1 otherwise, 2 if there is no manifest.</summary>
    public async Task<int> RunAsync(string outDir, bool keepMappings, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        outDir = Path.GetFullPath(outDir);
        DeploymentManifest? manifest;
        try
        {
            manifest = DeploymentManifest.Load(outDir);
        }
        catch (InvalidDataException ex)
        {
            _log.Error(ex.Message);
            return 1;
        }
        if (manifest is null)
        {
            _log.Error($"No {DeploymentManifest.FileName} in {outDir}.");
            return 2;
        }

        DeploymentState state;
        try
        {
            state = new DeploymentState(outDir, manifest, DeploymentOutputs.ReadPasswords(outDir), _time);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _log.Error($"Cannot read {DeploymentOutputs.PasswordsFile}: {ex.Message}");
            return 1;
        }

        int problems = 0;
        try
        {
            foreach (var row in manifest.Users.Where(u => u.Status != DeployStatus.Destroyed))
            {
                if (!await DestroyUserAsync(state, row, ct).ConfigureAwait(false))
                    problems++;
            }

            if (!await RemovePoolAsync(manifest.Pool, ct).ConfigureAwait(false))
                problems++;
        }
        catch (Exception ex) when (ex is ProxmoxApiException or HttpRequestException || IsFileError(ex))
        {
            _log.Error($"Teardown stopped: {ex.Message}");
            problems++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Error("Cancelled.");
            problems++;
        }

        try
        {
            FinishFiles(outDir, state, keepMappings);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _log.Error($"Could not update the deployment's files: {ex.Message}");
            problems++;
        }
        PrintSummary(manifest);
        return problems == 0 ? 0 : 1;
    }

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException;

    private async Task<bool> DestroyUserAsync(DeploymentState state, DeployedUser row, CancellationToken ct)
    {
        if (row.VmId is not int vmId)
        {
            state.Update(row, r => r.Status = DeployStatus.Destroyed);
            return true;
        }

        try
        {
            var rule = new VmOwnershipRule(state.Manifest.Pool, RequireTag: true, ExpectedName: null);
            var check = await VmCleanup.CheckAsync(_backend, rule, vmId, ct).ConfigureAwait(false);
            if (check.Exists && !check.Owned)
            {
                var message = $"VM {vmId} is not {rule.Describe()}; not destroying it";
                _log.Error($"{row.User}: {message}.");
                state.Update(row, r => r.Error = message);
                return false;
            }
            if (check.Exists)
            {
                _log.Info($"{row.User}: destroying VM {vmId}...");
                await VmCleanup.DestroyAsync(_backend, rule, check.Node!, vmId, ct).ConfigureAwait(false);
            }
            state.Update(row, r =>
            {
                r.Status = DeployStatus.Destroyed;
                r.Error = null;
            });
            return true;
        }
        catch (DeployException ex)
        {
            _log.Error($"{row.User}: {ex.Message}");
            state.Update(row, r => r.Error = ex.Message);
            return false;
        }
    }

    private async Task<bool> RemovePoolAsync(string pool, CancellationToken ct)
    {
        if (!await _backend.PoolExistsAsync(pool, ct).ConfigureAwait(false))
            return true;
        var members = await _backend.GetPoolMembersAsync(pool, ct).ConfigureAwait(false);
        if (members.Count > 0)
        {
            _log.Error($"Pool {pool} still has {members.Count} member(s) ({string.Join(", ", members.Select(m => m.Id))}); keeping it.");
            return false;
        }
        await _backend.DeletePoolAsync(pool, ct).ConfigureAwait(false);
        _log.Info($"Removed pool {pool}.");
        return true;
    }

    private void FinishFiles(string outDir, DeploymentState state, bool keepMappings)
    {
        var manifest = state.Manifest;
        var destroyed = manifest.Users.Where(u => u.Status == DeployStatus.Destroyed).ToList();

        // Passwords and .rdp files (signed routing tokens) of destroyed VMs are useless; keep
        // any for VMs that survived.
        foreach (var row in destroyed)
        {
            state.Passwords.Remove(row.User);
            File.Delete(Path.Combine(outDir, DeploymentOutputs.RdpFileName(row.User)));
        }
        DeploymentOutputs.WritePasswords(outDir, state.Passwords.Values);

        // When the gateway users file is this users.json, it is handled below (or kept).
        var usersJson = Path.Combine(outDir, DeploymentOutputs.UsersJsonFile);
        if (manifest.UsersFileTarget is null || !DeploymentOutputs.SamePath(manifest.UsersFileTarget, usersJson))
        {
            DeploymentOutputs.WriteUsersJson(outDir, manifest.Users
                .Where(u => u.Status == DeployStatus.Deployed && u.VmId is not null)
                .Select(u => new UserMapping(u.User, manifest.Node, u.VmId!.Value)));
        }
        state.Save();

        // Last, so a broken gateway users file does not stop the local cleanup; a re-run retries it.
        if (!keepMappings && manifest.UsersFileTarget is { } target)
        {
            int removed = UserMapFile.Remove(
                target, destroyed.Where(u => u.VmId is not null).Select(u => (u.User, u.VmId!.Value)));
            _log.Info(string.Create(CultureInfo.InvariantCulture, $"Removed {removed} mapping(s) from {target}."));
        }
    }

    private void PrintSummary(DeploymentManifest manifest)
    {
        int destroyed = manifest.Users.Count(u => u.Status == DeployStatus.Destroyed);
        int remaining = manifest.Users.Count - destroyed;
        _log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Teardown of '{manifest.Name}': {destroyed} VM(s) removed, {remaining} remaining."));
        foreach (var row in manifest.Users.Where(u => u.Status != DeployStatus.Destroyed))
            _log.Info($"  {row.User}: VM {row.VmId} {row.Status}{(row.Error is null ? "" : ": " + row.Error)}");
    }
}
