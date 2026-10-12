using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Hotlab.Gateway.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Activation;

/// <summary>
/// Brings a user's VM to a state where its RDP port accepts connections:
/// starts it if stopped, discovers its address (guest agent unless
/// <see cref="VmTarget.Host"/> is set), then waits for the port to open.
/// Concurrent requests for the same VM share a single in-flight activation.
/// </summary>
public sealed partial class VmActivator : IVmActivator
{
    private const string RunningStatus = "running";

    private readonly IVmControl _control;
    private readonly IPortProbe _probe;
    private readonly VmActivatorOptions _options;
    private readonly ILogger<VmActivator> _logger;
    private readonly ConcurrentDictionary<(string Node, int VmId), Task<IPEndPoint>> _inflight = new();

    /// <summary>Creates an activator.</summary>
    /// <param name="control">Hypervisor operations.</param>
    /// <param name="options">Timing options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="probe">TCP reachability probe; null for a real <see cref="TcpPortProbe"/>.</param>
    public VmActivator(
        IVmControl control,
        IOptions<VmActivatorOptions> options,
        ILogger<VmActivator> logger,
        IPortProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _control = control;
        _options = options.Value;
        _logger = logger;
        _probe = probe ?? new TcpPortProbe();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The shared activation runs under <see cref="VmActivatorOptions.ReadyTimeout"/>, not under
    /// <paramref name="ct"/>: cancelling one caller abandons only that caller's wait. Concurrent
    /// calls are keyed by (Node, VmId); the first caller's <see cref="VmTarget.Port"/> and
    /// <see cref="VmTarget.Host"/> are used for the shared activation.
    /// </remarks>
    /// <exception cref="VmActivationException">The VM could not be made ready in time.</exception>
    public async Task<IPEndPoint> EnsureReadyAsync(VmTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var key = (target.Node, target.VmId);

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
        (string Node, int VmId) key, VmTarget target, TaskCompletionSource<IPEndPoint> tcs)
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
            _inflight.TryRemove(new KeyValuePair<(string, int), Task<IPEndPoint>>(key, tcs.Task));
        }

        if (error is not null)
            tcs.SetException(error);
        else
            tcs.SetResult(result!);
    }

    private async Task<IPEndPoint> ActivateAsync(VmTarget target)
    {
        using var timeoutCts = new CancellationTokenSource(_options.ReadyTimeout);
        var ct = timeoutCts.Token;
        var stage = "checking status";
        try
        {
            await EnsureRunningAsync(target, ct).ConfigureAwait(false);

            stage = target.Host is null ? "waiting for guest agent IP" : "resolving host";
            var address = target.Host is null
                ? await WaitForAgentAddressAsync(target, ct).ConfigureAwait(false)
                : await ResolveHostAsync(target, target.Host, ct).ConfigureAwait(false);

            var endpoint = new IPEndPoint(address, target.Port);
            stage = "waiting for RDP port";
            await WaitForPortAsync(endpoint, ct).ConfigureAwait(false);

            LogReady(target.VmId, target.Node, endpoint);
            return endpoint;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            LogTimedOut(target.VmId, target.Node, _options.ReadyTimeout, stage);
            throw new VmActivationException(
                target,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"not ready after {_options.ReadyTimeout.TotalSeconds:0.#}s (timed out while {stage})"));
        }
        catch (Exception ex) when (ex is not VmActivationException and not OperationCanceledException)
        {
            throw new VmActivationException(target, $"activation failed while {stage}: {ex.Message}", ex);
        }
    }

    private async Task EnsureRunningAsync(VmTarget target, CancellationToken ct)
    {
        var status = await _control.GetStatusAsync(target.Node, target.VmId, ct).ConfigureAwait(false);
        if (status == RunningStatus)
            return;

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

    private async Task<IPAddress> WaitForAgentAddressAsync(VmTarget target, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var addresses = await _control.GetAgentIPv4AddressesAsync(target.Node, target.VmId, ct)
                    .ConfigureAwait(false);
                foreach (var candidate in addresses)
                {
                    if (IPAddress.TryParse(candidate, out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip))
                    {
                        return ip;
                    }
                }

                LogAgentNoAddress(target.VmId, target.Node);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAgentNotReady(target.VmId, target.Node, ex.Message);
            }

            await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
        }
    }

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
            await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guest agent on VM {VmId} ({Node}) reported no usable IPv4 address yet")]
    private partial void LogAgentNoAddress(int vmId, string node);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM {VmId} on {Node} ready at {Endpoint}")]
    private partial void LogReady(int vmId, string node, IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "VM {VmId} on {Node} not ready within {Timeout} (stage: {Stage})")]
    private partial void LogTimedOut(int vmId, string node, TimeSpan timeout, string stage);
}
