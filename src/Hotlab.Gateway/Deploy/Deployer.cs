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
            if (!TryResolveManifest(request, outDir, ref manifest, out var usage))
            {
                _log.Error(usage);
                return 2;
            }

            state = new DeploymentState(outDir, manifest!, DeploymentOutputs.ReadPasswords(outDir), _time);
            return await RunCoreAsync(request, state, ct).ConfigureAwait(false);
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

    private async Task<int> RunCoreAsync(DeployRequest request, DeploymentState state, CancellationToken ct)
    {
        var manifest = state.Manifest;
        var toDeploy = new List<string>();
        foreach (var user in request.Users)
        {
            var row = manifest.Find(user);
            if (row is { Status: DeployStatus.Deployed, VmId: int vmId } &&
                await _backend.FindVmNodeAsync(vmId, ct).ConfigureAwait(false) is not null)
            {
                _log.Info($"{user}: already deployed as VM {vmId}; skipping.");
                if (!state.Passwords.ContainsKey(user))
                    _log.Error($"Warning: {user}: no password on record in {DeploymentOutputs.PasswordsFile}.");
                continue;
            }
            toDeploy.Add(user);
        }

        string? target = request.UsersFileTarget is null ? null : Path.GetFullPath(request.UsersFileTarget);
        if (target is not null && !CheckConflicts(request, manifest, target))
            return 1;

        if (toDeploy.Count > 0)
            await DeployUsersAsync(request, state, toDeploy, ct).ConfigureAwait(false);

        var mappings = manifest.Users
            .Where(u => u.Status == DeployStatus.Deployed && u.VmId is not null)
            .Select(u => new UserMapping(u.User, manifest.Node, u.VmId!.Value))
            .ToList();
        DeploymentOutputs.WriteUsersJson(OutDirOf(request), mappings);
        if (target is not null)
        {
            UserMapFile.Merge(target, mappings);
            manifest.UsersFileTarget = target;
            _log.Info($"Merged {mappings.Count} mapping(s) into {target}.");
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

    private bool TryResolveManifest(
        DeployRequest request, string outDir, ref DeploymentManifest? manifest, out string error)
    {
        error = "";
        string? name = request.Name is null ? null : DeployNames.NormalizeDeploymentName(request.Name);
        if (request.Name is not null && name is null)
        {
            error = $"Invalid --name '{request.Name}'.";
            return false;
        }
        name ??= manifest?.Name ?? DeployNames.NormalizeDeploymentName(Path.GetFileName(outDir));
        if (name is null)
        {
            error = "Cannot derive a deployment name from --out; pass --name.";
            return false;
        }

        if (manifest is not null)
        {
            if (manifest.Name != name || manifest.SourceVmId != request.SourceVmId ||
                !string.Equals(manifest.Node, request.Node, StringComparison.Ordinal))
            {
                error = string.Create(CultureInfo.InvariantCulture,
                    $"{Path.Combine(outDir, DeploymentManifest.FileName)} belongs to deployment '{manifest.Name}' " +
                    $"(source {manifest.SourceVmId} on {manifest.Node}); use a different --out.");
                return false;
            }
        }
        else
        {
            var now = _time.GetUtcNow();
            manifest = new DeploymentManifest
            {
                Name = name,
                SourceVmId = request.SourceVmId,
                Node = request.Node,
                Created = now,
                Updated = now,
            };
        }

        manifest.Pool = DeployNames.PoolId(name);
        manifest.Address = request.Address;
        return true;
    }

    private bool CheckConflicts(DeployRequest request, DeploymentManifest manifest, string target)
    {
        var users = request.Users
            .Concat(manifest.Users.Where(u => u.Status == DeployStatus.Deployed).Select(u => u.User))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var owned = manifest.Users
            .Where(u => u.VmId is not null)
            .ToDictionary(u => u.User, u => (IReadOnlySet<int>)new HashSet<int> { u.VmId!.Value },
                StringComparer.OrdinalIgnoreCase);
        var conflicts = UserMapFile.FindConflicts(target, users, owned);
        if (conflicts.Count == 0)
            return true;
        if (request.Force)
        {
            _log.Info($"--force: remapping {string.Join(", ", conflicts)} in {target}.");
            return true;
        }
        _log.Error($"{target} already maps {string.Join(", ", conflicts)} to other VMs; " +
            "nothing was deployed. Use --force to remap them.");
        return false;
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
