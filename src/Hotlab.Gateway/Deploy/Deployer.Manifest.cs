using System.Globalization;
using Hotlab.Gateway.Tokens;

namespace Hotlab.Gateway.Deploy;

/// <summary>Manifest reconciliation and re-run checks.</summary>
public sealed partial class Deployer
{
    private bool TryResolveManifest(
        DeployRequest request, string outDir, ref DeploymentManifest? manifest, out bool addressChanged, out string error)
    {
        error = "";
        addressChanged = false;
        string? name = request.Name is null ? null : DeployNames.NormalizeDeploymentName(request.Name);
        if (request.Name is not null && name is null)
        {
            error = $"Invalid --name '{request.Name}' ({DeployNames.DeploymentNameRule}).";
            return false;
        }
        name ??= manifest?.Name ?? DeployNames.NormalizeDeploymentName(Path.GetFileName(outDir));
        if (name is null)
        {
            error = $"Cannot derive a deployment name ({DeployNames.DeploymentNameRule}) from --out; pass --name.";
            return false;
        }

        if (manifest is not null)
        {
            var manifestPath = Path.Combine(outDir, DeploymentManifest.FileName);
            if (manifest.Name != name || manifest.SourceVmId != request.SourceVmId ||
                !string.Equals(manifest.Node, request.Node, StringComparison.Ordinal))
            {
                error = string.Create(CultureInfo.InvariantCulture,
                    $"{manifestPath} belongs to deployment '{manifest.Name}' " +
                    $"(source {manifest.SourceVmId} on {manifest.Node}); use a different --out.");
                return false;
            }

            if (request.UsersFileTarget is not null && manifest.UsersFileTarget is { } recorded &&
                !DeploymentOutputs.SamePath(recorded, Path.GetFullPath(request.UsersFileTarget)))
            {
                error = $"{manifestPath} merged its mappings into {recorded}; re-run with that " +
                    "--users-file-target (to move the deployment to another gateway users file, tear it down first).";
                return false;
            }

            addressChanged = manifest.Address.Length > 0 &&
                !string.Equals(manifest.Address, request.Address, StringComparison.Ordinal);
            if (addressChanged && !request.Force)
            {
                error = $"{manifestPath} was deployed with --address {manifest.Address}; pass --force to change it " +
                    "(every deployed user's .rdp file is then regenerated with a new token).";
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

    /// <summary>
    /// True when the row is deployed and its VM is still recognisably ours: in the pool, tagged
    /// and with the recorded name. A VMID that now belongs to something else is not ours to skip
    /// (or touch); the user is redeployed on a new VM.
    /// </summary>
    private async Task<bool> IsDeployedAsync(DeploymentManifest manifest, DeployedUser? row, CancellationToken ct)
    {
        if (row is not { Status: DeployStatus.Deployed, VmId: int vmId })
            return false;
        var rule = new VmOwnershipRule(
            manifest.Pool, RequireTag: true, row.VmName ?? DeployNames.VmName(manifest.Name, row.User));
        var check = await VmCleanup.CheckAsync(_backend, rule, vmId, ct).ConfigureAwait(false);
        if (check.Owned)
            return true;
        if (check.Exists)
            _log.Error($"{row.User}: recorded VM {vmId} is no longer {rule.Describe()}; deploying a new VM.");
        return false;
    }

    /// <summary>After an --address change: rewrites the .rdp files of users not (re)deployed in this run.</summary>
    private void RegenerateRdpFiles(DeployRequest request, DeploymentState state, IReadOnlyCollection<string> deployedNow)
    {
        var manifest = state.Manifest;
        var rows = manifest.Users
            .Where(u => u.Status == DeployStatus.Deployed && !deployedNow.Contains(u.User, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var row in rows)
        {
            var token = _tokens.Mint(row.User, request.Lifetime);
            DeploymentOutputs.WriteRdp(state.OutDir, row.User,
                RdpFileRenderer.Render(manifest.Address, row.GuestAccount, token));
        }
        _log.Info($"Address changed to {manifest.Address}: regenerated {rows.Count} existing .rdp file(s).");
    }

    private bool CheckConflicts(DeployRequest request, DeploymentManifest manifest, string target)
    {
        var users = request.Users
            .Concat(manifest.Users.Where(u => u.Status == DeployStatus.Deployed).Select(u => u.User))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var user in UserMapFile.FindMalformed(target, users))
            _log.Error($"Warning: {target} has a malformed mapping for {user}; it will be overwritten.");

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
}
