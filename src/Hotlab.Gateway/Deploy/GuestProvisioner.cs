using System.Globalization;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// Prepares one freshly cloned VM: start, wait for the guest agent, create or reuse the guest
/// account, set its password via the agent, and shut down again.
/// </summary>
public sealed class GuestProvisioner
{
    /// <summary>Lowest uid treated as a regular (non-system) account.</summary>
    public const int MinRegularUid = 1000;

    private const string Running = "running";
    private const string Stopped = "stopped";
    private const int MaxErrorChars = 200;

    private readonly IDeployBackend _backend;
    private readonly DeployTimings _timings;

    /// <summary>Creates a provisioner.</summary>
    public GuestProvisioner(IDeployBackend backend, DeployTimings timings)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(timings);
        _backend = backend;
        _timings = timings;
    }

    /// <summary>Provisions the guest; throws <see cref="DeployException"/> on a failed step.</summary>
    /// <param name="node">Node hosting the VM.</param>
    /// <param name="vmId">VMID.</param>
    /// <param name="account">Guest account to set the password on.</param>
    /// <param name="createAccount">Create <paramref name="account"/> (Linux only) if it is missing.</param>
    /// <param name="password">The new password. Never logged or placed on a command line.</param>
    /// <param name="leaveRunning">Skip the final shutdown.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task ProvisionAsync(
        string node, int vmId, string account, bool createAccount, string password, bool leaveRunning,
        CancellationToken ct)
    {
        await StartAsync(node, vmId, ct).ConfigureAwait(false);
        await WaitForAgentAsync(node, vmId, ct).ConfigureAwait(false);

        if (createAccount)
        {
            AgentOsInfo os;
            try
            {
                os = await _backend.AgentGetOsInfoAsync(node, vmId, ct).ConfigureAwait(false);
            }
            catch (ProxmoxApiException)
            {
                os = new AgentOsInfo("", ""); // Older agents lack get-osinfo; treat as Linux.
            }
            if (os.IsWindows)
                throw new DeployException(
                    "guest is Windows; creating accounts is only supported on Linux guests. " +
                    "Pass --guest-user <existing account> to set the password of an existing account instead.");
            await EnsureLinuxAccountAsync(node, vmId, account, ct).ConfigureAwait(false);
        }

        try
        {
            await _backend.AgentSetUserPasswordAsync(node, vmId, account, password, ct).ConfigureAwait(false);
        }
        catch (ProxmoxApiException ex)
        {
            // The API error text never contains the password (it is only in the request body).
            throw new DeployException($"setting the password of guest account '{account}' failed: {Trim(ex.ResponseBody)}", ex);
        }

        if (!leaveRunning)
            await ShutdownAsync(node, vmId, ct).ConfigureAwait(false);
    }

    /// <summary>Shuts the VM down cleanly, hard-stopping it if that fails or times out.</summary>
    public async Task ShutdownAsync(string node, int vmId, CancellationToken ct)
    {
        string exit;
        using (var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Proxmox enforces the timeout itself; the margin covers task start-up and polling.
            shutdownCts.CancelAfter(_timings.ShutdownTimeout + TimeSpan.FromSeconds(30));
            try
            {
                exit = await _backend.ShutdownAsync(node, vmId, _timings.ShutdownTimeout, shutdownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                exit = "timed out";
            }
            catch (ProxmoxApiException ex)
            {
                exit = Trim(ex.ResponseBody);
            }
        }

        if (exit == "OK" && await _backend.GetStatusAsync(node, vmId, ct).ConfigureAwait(false) == Stopped)
            return;

        var stop = await _backend.StopAsync(node, vmId, ct).ConfigureAwait(false);
        if (stop != "OK" && await _backend.GetStatusAsync(node, vmId, ct).ConfigureAwait(false) != Stopped)
            throw new DeployException($"clean shutdown failed ({exit}) and stop failed ({stop})");
    }

    private async Task StartAsync(string node, int vmId, CancellationToken ct)
    {
        if (await _backend.GetStatusAsync(node, vmId, ct).ConfigureAwait(false) == Running)
            return;
        var exit = await _backend.StartAsync(node, vmId, ct).ConfigureAwait(false);
        if (exit != "OK")
            throw new DeployException($"start failed: {exit}");
    }

    private async Task WaitForAgentAsync(string node, int vmId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timings.AgentTimeout);
        try
        {
            while (!await _backend.AgentPingAsync(node, vmId, timeout.Token).ConfigureAwait(false))
                await Task.Delay(_timings.PollInterval, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeployException(
                $"guest agent did not respond within {_timings.AgentTimeout} " +
                "(is qemu-guest-agent installed in the source and 'agent: 1' set?)");
        }
    }

    private async Task EnsureLinuxAccountAsync(string node, int vmId, string account, CancellationToken ct)
    {
        var id = await ExecAsync(node, vmId, ["id", "-u", account], ct).ConfigureAwait(false);
        if (id.ExitCode == 0)
        {
            if (!int.TryParse(id.OutData.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int uid))
                throw new DeployException($"could not parse uid of existing account '{account}'");
            if (uid < MinRegularUid)
                throw new DeployException(
                    $"guest account '{account}' exists with uid {uid}, a system account; refusing to set its password");
            return;
        }

        var add = await ExecAsync(node, vmId, ["useradd", "-m", "-s", "/bin/bash", account], ct).ConfigureAwait(false);
        if (add.ExitCode != 0)
            throw new DeployException(
                $"useradd {account} failed (exit {Describe(add)}): {Trim(add.ErrData)}");
    }

    private async Task<AgentExecStatus> ExecAsync(
        string node, int vmId, IReadOnlyList<string> command, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timings.CommandTimeout);
        try
        {
            int pid = await _backend.AgentExecAsync(node, vmId, command, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var status = await _backend.AgentExecStatusAsync(node, vmId, pid, timeout.Token).ConfigureAwait(false);
                if (status.Exited)
                    return status;
                await Task.Delay(_timings.PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeployException($"guest command '{command[0]}' did not finish within {_timings.CommandTimeout}");
        }
        catch (ProxmoxApiException ex)
        {
            throw new DeployException($"guest command '{command[0]}' failed: {Trim(ex.ResponseBody)}", ex);
        }
    }

    private static string Describe(AgentExecStatus s) =>
        s.ExitCode?.ToString(CultureInfo.InvariantCulture) ??
        (s.Signal is { } sig ? string.Create(CultureInfo.InvariantCulture, $"signal {sig}") : "unknown");

    private static string Trim(string text)
    {
        text = text.Trim().ReplaceLineEndings(" ");
        return text.Length <= MaxErrorChars ? text : text[..MaxErrorChars] + "...";
    }
}
