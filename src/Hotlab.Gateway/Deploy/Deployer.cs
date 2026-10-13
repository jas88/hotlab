using System.Globalization;
using Hotlab.Gateway.Tokens;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// Deploys one clone of a source VM per user: clones sequentially (Proxmox locks the source
/// during a clone), then provisions the clones in parallel, recording everything in the
/// output directory. Re-runs skip users whose recorded VM still exists.
/// </summary>
public sealed partial class Deployer
{
    private readonly IDeployBackend _backend;
    private readonly RoutingTokenService _tokens;
    private readonly GuestProvisioner _provisioner;
    private readonly TimeProvider _time;
    private readonly DeployLog _log;

    /// <summary>Creates a deployer.</summary>
    public Deployer(
        IDeployBackend backend, RoutingTokenService tokens, DeployTimings timings,
        TextWriter stdout, TextWriter stderr, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(timings);
        _backend = backend;
        _tokens = tokens;
        _provisioner = new GuestProvisioner(backend, timings);
        _time = timeProvider ?? TimeProvider.System;
        _log = new DeployLog(stdout, stderr);
    }

    /// <summary>Runs the deployment. Returns 0 when every user is deployed, 1 on any failure, 2 on a usage problem.</summary>
    public async Task<int> RunAsync(DeployRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_tokens.Enabled)
        {
            _log.Error($"Routing tokens are disabled: {RoutingTokenOptions.SectionName}:SigningKey is not set; " +
                "deploy needs it to write the .rdp files.");
            return 1;
        }

        var outDir = Path.GetFullPath(request.OutDir);
        DeploymentState? state = null;
        try
        {
            if (!AtomicFile.EnsurePrivateDirectory(outDir) && AtomicFile.IsGroupOrOtherAccessible(outDir))
                _log.Error($"Warning: {outDir} is accessible to group/others; passwords.csv itself is 0600.");

            var manifest = DeploymentManifest.Load(outDir);
            if (!TryResolveManifest(request, outDir, ref manifest, out bool addressChanged, out var usage))
            {
                _log.Error(usage);
                return 2;
            }

            state = new DeploymentState(outDir, manifest!, DeploymentOutputs.ReadPasswords(outDir), _time);
            return await RunCoreAsync(request, state, addressChanged, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state?.Save();
            _log.Error("Cancelled. Re-run the same command to resume; finished users are kept.");
            return 1;
        }
        catch (Exception ex) when (ex is ProxmoxApiException or HttpRequestException or IOException
            or InvalidDataException or UnauthorizedAccessException or DeployException)
        {
            state?.Save();
            _log.Error($"Deployment failed: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> RunCoreAsync(
        DeployRequest request, DeploymentState state, bool addressChanged, CancellationToken ct)
    {
        var manifest = state.Manifest;
        var toDeploy = new List<string>();
        foreach (var user in request.Users)
        {
            if (await IsDeployedAsync(manifest, manifest.Find(user), ct).ConfigureAwait(false))
            {
                _log.Info($"{user}: already deployed as VM {manifest.Find(user)!.VmId}; skipping.");
                if (!state.Passwords.ContainsKey(user))
                    _log.Error($"Warning: {user}: no password on record in {DeploymentOutputs.PasswordsFile}.");
                continue;
            }
            toDeploy.Add(user);
        }

        string? target = request.UsersFileTarget is null ? null : Path.GetFullPath(request.UsersFileTarget);
        if (target is not null && !CheckConflicts(request, manifest, target))
            return 1;

        // VMIDs this deployment may have mapped its users to before this run, so mappings of
        // users no longer deployed can be pruned from the gateway users file afterwards.
        var mappedBefore = manifest.Users
            .Where(u => u.Status == DeployStatus.Deployed && u.VmId is not null)
            .Select(u => (u.User, VmId: u.VmId!.Value))
            .ToList();

        if (toDeploy.Count > 0)
            await DeployUsersAsync(request, state, toDeploy, ct).ConfigureAwait(false);
        if (addressChanged)
            RegenerateRdpFiles(request, state, toDeploy);

        var mappings = manifest.Users
            .Where(u => u.Status == DeployStatus.Deployed && u.VmId is not null)
            .Select(u => new UserMapping(u.User, manifest.Node, u.VmId!.Value))
            .ToList();
        var usersJson = Path.Combine(OutDirOf(request), DeploymentOutputs.UsersJsonFile);
        // When the gateway users file is (or may be) the output users.json itself, the merge
        // below writes it: never overwrite the gateway's file with this deployment's map alone.
        if (target is null || !DeploymentOutputs.MaybeSamePath(target, usersJson))
            DeploymentOutputs.WriteUsersJson(OutDirOf(request), mappings);
        if (target is not null)
        {
            var stale = mappedBefore
                .Where(m => !mappings.Any(d => string.Equals(d.User, m.User, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            int pruned = UserMapFile.Merge(target, mappings, stale);
            manifest.UsersFileTarget = target;
            _log.Info($"Merged {mappings.Count} mapping(s) into {target}" +
                (pruned > 0 ? $"; removed {pruned} of users no longer deployed." : "."));
        }
        state.Save();

        return PrintSummary(request, state);
    }

    private static string OutDirOf(DeployRequest request) => Path.GetFullPath(request.OutDir);

    private async Task DeployUsersAsync(
        DeployRequest request, DeploymentState state, List<string> users, CancellationToken ct)
    {
        var manifest = state.Manifest;
        bool isTemplate = await _backend.IsTemplateAsync(manifest.Node, manifest.SourceVmId, ct).ConfigureAwait(false);
        if (!await _backend.PoolExistsAsync(manifest.Pool, ct).ConfigureAwait(false))
        {
            await _backend.CreatePoolAsync(manifest.Pool, $"Hotlab deployment {manifest.Name}", ct).ConfigureAwait(false);
            _log.Info($"Created pool {manifest.Pool}.");
        }

        _log.Info($"Deploying {users.Count} user(s) from {(isTemplate ? "template" : "VM")} {manifest.SourceVmId} " +
            $"({(isTemplate ? "linked" : "full")} clones) into pool {manifest.Pool}.");

        using var gate = new SemaphoreSlim(Math.Max(1, request.Parallel));
        var provisioning = new List<Task>();
        try
        {
            foreach (var user in users)
            {
                ct.ThrowIfCancellationRequested();
                var row = await CloneUserAsync(request, state, user, isTemplate, ct).ConfigureAwait(false);
                if (row is null)
                    continue;
                provisioning.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await ProvisionUserAsync(request, state, row, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }
        }
        finally
        {
            // Never leave provisioning tasks running unobserved, even when cloning was cancelled.
            await Task.WhenAll(provisioning.Select(t => t.ContinueWith(
                static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default))).ConfigureAwait(false);
        }
        await Task.WhenAll(provisioning).ConfigureAwait(false);
    }

    private int PrintSummary(DeployRequest request, DeploymentState state)
    {
        var rows = state.Manifest.Users
            .Where(u => request.Users.Contains(u.User, StringComparer.OrdinalIgnoreCase))
            .ToList();
        _log.Info("");
        _log.Info($"{"USER",-32} {"VMID",8}  STATUS");
        foreach (var r in rows)
        {
            var vmId = r.VmId?.ToString(CultureInfo.InvariantCulture) ?? "-";
            _log.Info($"{r.User,-32} {vmId,8}  {r.Status}{(r.Error is null ? "" : ": " + r.Error)}");
        }

        int failed = rows.Count(r => r.Status != DeployStatus.Deployed);
        var outDir = OutDirOf(request);
        _log.Info("");
        _log.Info($"Passwords: {Path.Combine(outDir, DeploymentOutputs.PasswordsFile)} (0600). " +
            "Hand each user their .rdp file and password separately.");
        _log.Info($"Gateway mappings: {Path.Combine(outDir, DeploymentOutputs.UsersJsonFile)}");
        if (failed > 0)
            _log.Error($"{failed} user(s) failed; re-run the same command to retry them.");
        return failed > 0 ? 1 : 0;
    }
}
