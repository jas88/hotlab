using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Hotlab.Gateway.Routing;
using Hotlab.Proxmox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Activation;

/// <summary>
/// Brings a user's VM to a state where its RDP port accepts connections:
/// starts it if stopped, discovers its address (guest agent unless
/// <see cref="VmTarget.Host"/> is set), then waits for the port to open.
/// Concurrent requests for the same target (VM, host override and port) share a single
/// in-flight activation.
/// </summary>
public sealed partial class VmActivator : IVmActivator
{
    private const string RunningStatus = "running";

    /// <summary>
    /// Minimum gap between Warning-level logs of retried guest-agent failures, and how long
    /// reported addresses must stay unreachable before that is logged as a Warning.
    /// </summary>
    private static readonly TimeSpan AgentWarningInterval = TimeSpan.FromSeconds(15);

    private readonly IVmControl _control;
    private readonly IPortProbe _probe;
    private readonly VmActivatorOptions _options;
    private readonly ILogger<VmActivator> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<InflightKey, Task<IPEndPoint>> _inflight = new();

    /// <summary>Creates an activator.</summary>
    /// <param name="control">Hypervisor operations.</param>
    /// <param name="options">Timing options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="probe">TCP reachability probe; null for a real <see cref="TcpPortProbe"/>.</param>
    /// <param name="timeProvider">
    /// Clock for the ready timeout, poll delays and log throttling; null for <see cref="TimeProvider.System"/>.
    /// </param>
    public VmActivator(
        IVmControl control,
        IOptions<VmActivatorOptions> options,
        ILogger<VmActivator> logger,
        IPortProbe? probe = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _control = control;
        _options = options.Value;
        _logger = logger;
        _probe = probe ?? new TcpPortProbe();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The shared activation runs under <see cref="VmActivatorOptions.ReadyTimeout"/>, not under
    /// <paramref name="ct"/>: cancelling one caller abandons only that caller's wait. Concurrent
    /// calls are keyed by (Node, VmId, Host, Port), so callers only share an activation whose
    /// endpoint is the one they asked for; calls for the same VM with a different
    /// <see cref="VmTarget.Host"/> or <see cref="VmTarget.Port"/> run their own.
    /// </remarks>
    /// <exception cref="VmActivationException">The VM could not be made ready in time.</exception>
    public async Task<IPEndPoint> EnsureReadyAsync(VmTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var key = new InflightKey(target.Node, target.VmId, target.Host, target.Port);

        while (true)
        {
            if (_inflight.TryGetValue(key, out var existing))
            {
                LogJoining(target.VmId, target.Node);
                return await existing.WaitAsync(ct).ConfigureAwait(false);
            }

            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflight.TryAdd(key, tcs.Task))
                continue;

            // Observe faults so callers that gave up waiting don't cause UnobservedTaskException.
            _ = tcs.Task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = Task.Run(() => RunSharedAsync(key, target, tcs), CancellationToken.None);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunSharedAsync(
        InflightKey key, VmTarget target, TaskCompletionSource<IPEndPoint> tcs)
    {
        IPEndPoint? result = null;
        Exception? error = null;
        try
        {
            result = await ActivateAsync(target).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure is handed to every waiter via the shared task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex;
        }
        finally
        {
            // Remove before completing so a caller arriving after completion re-checks the VM.
            _inflight.TryRemove(new KeyValuePair<InflightKey, Task<IPEndPoint>>(key, tcs.Task));
        }

        if (error is not null)
            tcs.SetException(error);
        else
            tcs.SetResult(result!);
    }

    private async Task<IPEndPoint> ActivateAsync(VmTarget target)
    {
        using var timeoutCts = new CancellationTokenSource(_options.ReadyTimeout, _time);
        var ct = timeoutCts.Token;
        var progress = new Progress();
        try
        {
            await EnsureRunningAsync(target, progress, ct).ConfigureAwait(false);

            IPEndPoint endpoint;
            if (target.Host is null)
            {
                progress.Stage = "waiting for guest agent IP";
                endpoint = await WaitForAgentEndpointAsync(target, progress, ct).ConfigureAwait(false);
            }
            else
            {
                progress.Stage = "resolving host";
                var address = await ResolveHostAsync(target, target.Host, ct).ConfigureAwait(false);
                endpoint = new IPEndPoint(address, target.Port);
                progress.Stage = "waiting for RDP port";
                await WaitForPortAsync(endpoint, ct).ConfigureAwait(false);
            }

            LogReady(target.VmId, target.Node, endpoint);
            return endpoint;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            LogTimedOut(target.VmId, target.Node, _options.ReadyTimeout, progress.Stage);
            throw new VmActivationException(
                target,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"not ready after {_options.ReadyTimeout.TotalSeconds:0.#}s (timed out while {progress.Stage})"));
        }
        catch (Exception ex) when (ex is not VmActivationException and not OperationCanceledException)
        {
            throw new VmActivationException(target, $"activation failed while {progress.Stage}: {ex.Message}", ex);
        }
    }

    private async Task EnsureRunningAsync(VmTarget target, Progress progress, CancellationToken ct)
    {
        var status = await _control.GetStatusAsync(target.Node, target.VmId, ct).ConfigureAwait(false);
        if (status == RunningStatus)
            return;

        progress.Stage = "starting VM";
        LogStarting(target.VmId, target.Node, status);
        string exitStatus;
        Exception? startError = null;
        try
        {
            exitStatus = await _control.StartAndWaitAsync(target.Node, target.VmId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            startError = ex;
            exitStatus = ex.Message;
        }

        if (startError is null && exitStatus == "OK")
            return;

        // Someone else (another broker, an admin) may have started it concurrently.
        status = await _control.GetStatusAsync(target.Node, target.VmId, ct).ConfigureAwait(false);
        if (status == RunningStatus)
        {
            LogStartRaced(target.VmId, target.Node, exitStatus);
            return;
        }

        throw new VmActivationException(target, $"start failed ({exitStatus}); status is '{status}'", startError);
    }

    /// <summary>
    /// Each poll round asks the guest agent for its addresses and probes every usable one
    /// (in the order reported) on <see cref="VmTarget.Port"/>, returning the first that accepts
    /// a connection. Re-querying each round also follows an address that changes during boot.
    /// A round whose probes all fail is logged at Debug (the RDP service routinely starts after
    /// the agent); once that has lasted <see cref="AgentWarningInterval"/> it is logged as a
    /// Warning, at most once per interval.
    /// </summary>
    private async Task<IPEndPoint> WaitForAgentEndpointAsync(VmTarget target, Progress progress, CancellationToken ct)
    {
        long? lastWarning = null;
        long? unreachableSince = null;
        long? lastUnreachableWarning = null;
        while (true)
        {
            IReadOnlyList<string>? addresses = null;
            try
            {
                addresses = await _control.GetAgentIPv4AddressesAsync(target.Node, target.VmId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (IsPermanentAgentError(ex))
                    throw;

                // Expected while the guest boots (PVE answers 500 until the agent runs), so
                // warn once and then at most every AgentWarningInterval; otherwise Debug.
                if (lastWarning is null || _time.GetElapsedTime(lastWarning.Value) >= AgentWarningInterval)
                {
                    lastWarning = _time.GetTimestamp();
                    LogAgentNotReadyWarning(target.VmId, target.Node, ex.Message);
                }
                else
                {
                    LogAgentNotReady(target.VmId, target.Node, ex.Message);
                }
            }

            if (addresses is not null)
            {
                var usable = UsableAddresses(addresses);
                if (usable.Count == 0)
                {
                    LogAgentNoAddress(target.VmId, target.Node);
                }
                else
                {
                    progress.Stage = "waiting for RDP port";
                    foreach (var address in usable)
                    {
                        var endpoint = new IPEndPoint(address, target.Port);
                        if (await _probe.CanConnectAsync(endpoint, _options.ProbeTimeout, ct).ConfigureAwait(false))
                            return endpoint;
                    }

                    unreachableSince ??= _time.GetTimestamp();
                    var probed = string.Join(", ", usable);
                    var unreachableFor = _time.GetElapsedTime(unreachableSince.Value);
                    if (unreachableFor >= AgentWarningInterval &&
                        (lastUnreachableWarning is null ||
                         _time.GetElapsedTime(lastUnreachableWarning.Value) >= AgentWarningInterval))
                    {
                        lastUnreachableWarning = _time.GetTimestamp();
                        LogPortUnreachableWarning(target.VmId, target.Node, probed, target.Port, unreachableFor);
                    }
                    else
                    {
                        LogPortUnreachable(target.VmId, target.Node, probed, target.Port);
                    }
                }
            }

            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    private static List<IPAddress> UsableAddresses(IReadOnlyList<string> candidates)
    {
        var usable = new List<IPAddress>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (IPAddress.TryParse(candidate, out var ip) &&
                ip.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(ip) &&
                !usable.Contains(ip))
            {
                usable.Add(ip);
            }
        }
        return usable;
    }

    /// <summary>
    /// Agent errors that retrying cannot fix: authentication/permission failures and an
    /// unknown VM. PVE reports a stopped or not-yet-running agent as 500, which is retried.
    /// </summary>
    private static bool IsPermanentAgentError(Exception ex) =>
        ex is ProxmoxApiException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
        };

    private static async Task<IPAddress> ResolveHostAsync(VmTarget target, string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new VmActivationException(target, $"host '{host}' resolved to no addresses");
    }

    private async Task WaitForPortAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        while (!await _probe.CanConnectAsync(endpoint, _options.ProbeTimeout, ct).ConfigureAwait(false))
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Joining in-flight activation of VM {VmId} on {Node}")]
    private partial void LogJoining(int vmId, string node);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting VM {VmId} on {Node} (status was {Status})")]
    private partial void LogStarting(int vmId, string node, string status);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Start of VM {VmId} on {Node} reported '{ExitStatus}' but the VM is running; continuing")]
    private partial void LogStartRaced(int vmId, string node, string exitStatus);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guest agent on VM {VmId} ({Node}) not ready: {Reason}")]
    private partial void LogAgentNotReady(int vmId, string node, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Guest agent on VM {VmId} ({Node}) not ready, retrying: {Reason}")]
    private partial void LogAgentNotReadyWarning(int vmId, string node, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guest agent on VM {VmId} ({Node}) reported no usable IPv4 address yet")]
    private partial void LogAgentNoAddress(int vmId, string node);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "VM {VmId} ({Node}): no reported address accepted connections on port {Port} (probed {Addresses})")]
    private partial void LogPortUnreachable(int vmId, string node, string addresses, int port);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "VM {VmId} ({Node}): no reported address has accepted connections on port {Port} for {Elapsed} (probed {Addresses}); retrying")]
    private partial void LogPortUnreachableWarning(int vmId, string node, string addresses, int port, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM {VmId} on {Node} ready at {Endpoint}")]
    private partial void LogReady(int vmId, string node, IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "VM {VmId} on {Node} not ready within {Timeout} (stage: {Stage})")]
    private partial void LogTimedOut(int vmId, string node, TimeSpan timeout, string stage);

    /// <summary>Shared-activation key: callers share only when they want the same endpoint.</summary>
    private readonly record struct InflightKey(string Node, int VmId, string? Host, int Port);

    /// <summary>What the activation is doing, for timeout and failure messages.</summary>
    private sealed class Progress
    {
        public string Stage { get; set; } = "checking status";
    }
}
