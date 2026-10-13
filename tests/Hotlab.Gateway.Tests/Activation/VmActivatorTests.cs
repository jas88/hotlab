using System.Net;
using System.Net.Sockets;
using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Routing;
using Hotlab.Gateway.Tests.Tokens;
using Hotlab.Proxmox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Activation;

public class VmActivatorTests
{
    private static readonly VmTarget Target = new("pve1", 123);

    /// <summary>Ready timeout fired by hand via <see cref="ManualTimeoutProvider"/>; never elapses on its own.</summary>
    private static readonly TimeSpan ManualTimeout = TimeSpan.FromHours(1);

    private static VmActivator Create(
        FakeVmControl control, IPortProbe? probe = null, TimeSpan? readyTimeout = null,
        ILogger<VmActivator>? logger = null, TimeProvider? time = null) =>
        new(
            control,
            Options.Create(new VmActivatorOptions
            {
                ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(30),
                PollInterval = TimeSpan.FromMilliseconds(5),
                ProbeTimeout = TimeSpan.FromMilliseconds(200),
            }),
            logger ?? NullLogger<VmActivator>.Instance,
            probe ?? new FakePortProbe(),
            time);

    [Fact]
    public async Task Already_running_skips_start_and_returns_agent_address()
    {
        var control = new FakeVmControl { Status = "running", AgentAddresses = ["10.1.2.3"] };
        var probe = new FakePortProbe();

        var endpoint = await Create(control, probe).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.1.2.3"), 3389), endpoint);
        Assert.Equal(0, control.StartCalls);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Stopped_vm_is_started()
    {
        var control = new FakeVmControl { Status = "stopped" };

        await Create(control).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(1, control.StartCalls);
        Assert.Equal("running", control.Status);
    }

    [Fact]
    public async Task Host_override_skips_guest_agent_and_uses_target_port()
    {
        var control = new FakeVmControl { Status = "running" };
        var probe = new FakePortProbe();

        var endpoint = await Create(control, probe)
            .EnsureReadyAsync(Target with { Host = "192.0.2.10", Port = 13389 }, CancellationToken.None);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 13389), endpoint);
        Assert.Equal(0, control.AgentCalls);
        Assert.Equal(endpoint, Assert.Single(probe.Endpoints));
    }

    [Fact]
    public async Task Agent_errors_and_unusable_addresses_are_retried_until_an_ipv4_appears()
    {
        var control = new FakeVmControl
        {
            OnAgent = call => call switch
            {
                1 or 2 => throw new InvalidOperationException("QEMU guest agent is not running"),
                3 => [],
                4 => ["127.0.0.1", "fe80::1"],
                _ => ["10.9.8.7"],
            },
        };

        var endpoint = await Create(control).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.9.8.7"), endpoint.Address);
        Assert.Equal(5, control.AgentCalls);
    }

    [Fact]
    public async Task Port_closed_is_polled_until_open()
    {
        var control = new FakeVmControl();
        var probe = new FakePortProbe(call => call >= 3);

        await Create(control, probe).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public async Task Port_never_opening_throws_VmActivationException_after_timeout()
    {
        // The ready timeout fires only when the test says so, after several real polls,
        // so neither the stage reached nor the poll count depends on scheduler latency.
        var time = new ManualTimeoutProvider(ManualTimeout);
        var control = new FakeVmControl();
        var probe = new FakePortProbe(_ => false);
        var activator = Create(control, probe, readyTimeout: ManualTimeout, time: time);

        var activation = activator.EnsureReadyAsync(Target, CancellationToken.None);
        await WaitUntilAsync(() => probe.Calls >= 3);
        Assert.False(activation.IsCompleted);
        time.FireTimeouts();
        var ex = await Assert.ThrowsAsync<VmActivationException>(() => activation);

        Assert.Equal(Target, ex.Target);
        Assert.Contains("RDP port", ex.Message);
        Assert.Contains("123", ex.Message);
        Assert.True(probe.Calls > 1);
    }

    [Fact]
    public async Task Start_failure_while_vm_became_running_is_treated_as_success()
    {
        var control = new FakeVmControl { Status = "stopped" };
        control.OnStart = _ =>
        {
            control.Status = "running"; // someone else won the race
            throw new InvalidOperationException("VM 123 already running");
        };

        var endpoint = await Create(control).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal(1, control.StartCalls);
        Assert.Equal(2, control.StatusCalls);
    }

    [Fact]
    public async Task Start_task_not_ok_and_vm_still_stopped_throws()
    {
        var control = new FakeVmControl { Status = "stopped", OnStart = _ => Task.FromResult("ERROR: no space") };

        var ex = await Assert.ThrowsAsync<VmActivationException>(
            () => Create(control).EnsureReadyAsync(Target, CancellationToken.None));

        Assert.Contains("no space", ex.Message);
        Assert.Contains("stopped", ex.Message);
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_start()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new FakeVmControl { Status = "stopped" };
        control.OnStart = async _ =>
        {
            await gate.Task;
            control.Status = "running";
            return "OK";
        };
        var activator = Create(control);

        var callers = Enumerable.Range(0, 10)
            .Select(_ => activator.EnsureReadyAsync(Target, CancellationToken.None))
            .ToArray();
        await WaitUntilAsync(() => control.StartCalls == 1);
        gate.SetResult();
        var endpoints = await Task.WhenAll(callers);

        Assert.Equal(1, control.StartCalls);
        Assert.Equal(1, control.StatusCalls);
        Assert.All(endpoints, e => Assert.Equal(endpoints[0], e));
    }

    [Fact]
    public async Task Different_vms_are_activated_independently()
    {
        var control = new FakeVmControl { Status = "running" };
        var activator = Create(control);

        await Task.WhenAll(
            activator.EnsureReadyAsync(new VmTarget("pve1", 1), CancellationToken.None),
            activator.EnsureReadyAsync(new VmTarget("pve1", 2), CancellationToken.None));

        Assert.Equal(2, control.StatusCalls);
    }

    [Fact]
    public async Task Cancelling_one_caller_does_not_cancel_the_shared_activation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new FakeVmControl { Status = "stopped" };
        control.OnStart = async _ =>
        {
            await gate.Task;
            control.Status = "running";
            return "OK";
        };
        var activator = Create(control);
        using var cts = new CancellationTokenSource();

        var cancelled = activator.EnsureReadyAsync(Target, cts.Token);
        await WaitUntilAsync(() => control.StartCalls == 1);
        var survivor = activator.EnsureReadyAsync(Target, CancellationToken.None);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(survivor.IsCompleted);

        gate.SetResult();
        var endpoint = await survivor;

        Assert.Equal(IPAddress.Parse("10.0.0.5"), endpoint.Address);
        Assert.Equal(1, control.StartCalls);
    }

    [Fact]
    public async Task Call_after_completion_rechecks_status()
    {
        var control = new FakeVmControl { Status = "running" };
        var activator = Create(control);

        await activator.EnsureReadyAsync(Target, CancellationToken.None);
        control.Status = "stopped"; // VM shut down between sessions
        await activator.EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(2, control.StatusCalls);
        Assert.Equal(1, control.StartCalls);
    }

    [Fact]
    public async Task Failed_activation_is_not_cached()
    {
        var control = new FakeVmControl { Status = "stopped", OnStart = _ => Task.FromResult("ERROR") };
        var activator = Create(control);

        await Assert.ThrowsAsync<VmActivationException>(
            () => activator.EnsureReadyAsync(Target, CancellationToken.None));
        control.OnStart = null;
        var endpoint = await activator.EnsureReadyAsync(Target, CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal(2, control.StartCalls);
    }

    [Fact]
    public async Task Real_tcp_probe_against_loopback_listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var control = new FakeVmControl { Status = "running" };
        var activator = new VmActivator(
            control,
            Options.Create(new VmActivatorOptions { PollInterval = TimeSpan.FromMilliseconds(5) }),
            NullLogger<VmActivator>.Instance);

        var endpoint = await activator.EnsureReadyAsync(
            Target with { Host = "127.0.0.1", Port = port }, CancellationToken.None);

        Assert.Equal(new IPEndPoint(IPAddress.Loopback, port), endpoint);
        Assert.Equal(0, control.AgentCalls);
    }

    [Fact]
    public async Task TcpPortProbe_reports_open_and_closed_ports()
    {
        var probe = new TcpPortProbe();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = (IPEndPoint)listener.LocalEndpoint;

        Assert.True(await probe.CanConnectAsync(open, TimeSpan.FromSeconds(2), CancellationToken.None));

        listener.Stop();
        Assert.False(await probe.CanConnectAsync(open, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task Every_usable_agent_address_is_probed_and_the_first_reachable_wins()
    {
        var control = new FakeVmControl { AgentAddresses = ["127.0.0.1", "10.0.0.1", "10.0.0.2", "10.0.0.3"] };
        var reachable = IPAddress.Parse("10.0.0.2");
        var probe = new EndpointProbe(ep => ep.Address.Equals(reachable));

        var endpoint = await Create(control, probe).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(new IPEndPoint(reachable, 3389), endpoint);
        Assert.Equal(
            [IPAddress.Parse("10.0.0.1"), reachable],
            probe.Probed.Select(e => e.Address).ToArray());
        Assert.Equal(1, control.AgentCalls);
    }

    [Fact]
    public async Task Concurrent_callers_with_different_host_or_port_get_their_own_endpoint()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new FakeVmControl { Status = "stopped" };
        control.OnStart = async _ =>
        {
            await gate.Task;
            control.Status = "running";
            return "OK";
        };
        var activator = Create(control);

        var a = activator.EnsureReadyAsync(Target with { Host = "192.0.2.10" }, CancellationToken.None);
        await WaitUntilAsync(() => control.StartCalls == 1);
        var b = activator.EnsureReadyAsync(Target with { Host = "192.0.2.11" }, CancellationToken.None);
        var c = activator.EnsureReadyAsync(Target with { Host = "192.0.2.10", Port = 3390 }, CancellationToken.None);
        var shared = activator.EnsureReadyAsync(Target with { Host = "192.0.2.10" }, CancellationToken.None);
        await WaitUntilAsync(() => control.StartCalls == 3);
        gate.SetResult();

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 3389), await a);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.11"), 3389), await b);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 3390), await c);
        Assert.Equal(await a, await shared);
        Assert.Equal(3, control.StartCalls); // a and shared joined one activation
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Permanent_agent_errors_fail_fast(HttpStatusCode status)
    {
        var control = new FakeVmControl
        {
            OnAgent = _ => throw new ProxmoxApiException(status, "GET", "/nodes/pve1/qemu/123/agent/network-get-interfaces", "denied"),
        };
        var activator = Create(control, readyTimeout: TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<VmActivationException>(
            () => activator.EnsureReadyAsync(Target, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsType<ProxmoxApiException>(ex.InnerException);
        Assert.Contains("guest agent", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, control.AgentCalls);
    }

    [Fact]
    public async Task Agent_not_running_500_is_retried_and_warned_about_once()
    {
        var control = new FakeVmControl
        {
            OnAgent = call => call <= 5
                ? throw new ProxmoxApiException(HttpStatusCode.InternalServerError, "GET", "/agent", "QEMU guest agent is not running")
                : ["10.0.0.9"],
        };
        var log = new CapturingLogger<VmActivator>();
        var time = new ManualTimeoutProvider(ManualTimeout); // clock frozen: no repeat warning however slow

        var endpoint = await Create(control, logger: log, time: time).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.0.0.9"), endpoint.Address);
        Assert.Equal(6, control.AgentCalls);
        Assert.Single(log.Entries, e => e.StartsWith("Warning:", StringComparison.Ordinal) && e.Contains("not running", StringComparison.Ordinal));
        Assert.Equal(4, log.Entries.Count(e => e.StartsWith("Debug:", StringComparison.Ordinal) && e.Contains("not running", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unreachable_agent_addresses_are_logged_each_round_and_warned_once_persistent()
    {
        var control = new FakeVmControl { AgentAddresses = ["10.0.0.1", "10.0.0.2"] };
        var time = new ManualTimeoutProvider(ManualTimeout);
        var rounds = 0;
        var probe = new EndpointProbe(ep =>
        {
            if (!ep.Address.Equals(IPAddress.Parse("10.0.0.2")))
                return false;
            rounds++;
            // 16s pass during round 3, so it warns; the clock is then frozen again, so rounds 4-5 do not.
            if (rounds == 3)
                time.AdvanceTimestamp(TimeSpan.FromSeconds(16));
            return rounds == 6;
        });
        var log = new CapturingLogger<VmActivator>();

        var endpoint = await Create(control, probe, logger: log, time: time).EnsureReadyAsync(Target, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.0.0.2"), endpoint.Address);
        var unreachable = log.Entries.Where(e => e.Contains("accepted connections on port 3389", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, unreachable.Count);
        Assert.All(unreachable, e => Assert.Contains("10.0.0.1, 10.0.0.2", e, StringComparison.Ordinal));
        var warning = Assert.Single(unreachable, e => e.StartsWith("Warning:", StringComparison.Ordinal));
        Assert.Same(unreachable[2], warning);
        Assert.Equal(4, unreachable.Count(e => e.StartsWith("Debug:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Timeout_during_start_reports_starting_stage()
    {
        var time = new ManualTimeoutProvider(ManualTimeout);
        var control = new FakeVmControl { Status = "stopped" };
        control.OnStart = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "OK";
        };

        var activation = Create(control, readyTimeout: ManualTimeout, time: time)
            .EnsureReadyAsync(Target, CancellationToken.None);
        await WaitUntilAsync(() => control.StartCalls == 1);
        time.FireTimeouts();
        var ex = await Assert.ThrowsAsync<VmActivationException>(() => activation);

        Assert.Contains("starting VM", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_Validate_rejects_non_positive_intervals()
    {
        Assert.Null(new VmActivatorOptions().Validate());
        Assert.Contains("PollInterval", new VmActivatorOptions { PollInterval = TimeSpan.Zero }.Validate(), StringComparison.Ordinal);
        Assert.Contains("ProbeTimeout", new VmActivatorOptions { ProbeTimeout = TimeSpan.FromSeconds(-1) }.Validate(), StringComparison.Ordinal);
        Assert.Contains("ReadyTimeout", new VmActivatorOptions { ReadyTimeout = TimeSpan.Zero }.Validate(), StringComparison.Ordinal);
        Assert.Contains("ReadyTimeout", new VmActivatorOptions { ReadyTimeout = TimeSpan.FromDays(100) }.Validate(), StringComparison.Ordinal);
    }

    /// <summary>Probe answering per endpoint, recording the order probed.</summary>
    private sealed class EndpointProbe(Func<IPEndPoint, bool> isOpen) : IPortProbe
    {
        public List<IPEndPoint> Probed { get; } = [];

        public Task<bool> CanConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
        {
            lock (Probed)
                Probed.Add(endpoint);
            return Task.FromResult(isOpen(endpoint));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Real-time timers (so poll delays still elapse) except those due after
    /// <paramref name="manualDue"/>, which fire only on <see cref="FireTimeouts"/>; the
    /// timestamp clock is frozen.
    /// </summary>
    private sealed class ManualTimeoutProvider(TimeSpan manualDue) : TimeProvider
    {
        private readonly List<ManualTimer> _manual = [];

        private long _timestamp;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        /// <summary>Moves the (otherwise frozen) monotonic clock forward.</summary>
        public void AdvanceTimestamp(TimeSpan by) =>
            Interlocked.Add(ref _timestamp, (long)(by.TotalSeconds * TimestampFrequency));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != manualDue)
                return System.CreateTimer(callback, state, dueTime, period);
            var timer = new ManualTimer(callback, state);
            lock (_manual)
                _manual.Add(timer);
            return timer;
        }

        public void FireTimeouts()
        {
            ManualTimer[] timers;
            lock (_manual)
                timers = [.. _manual];
            Assert.NotEmpty(timers);
            foreach (var timer in timers)
                timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private volatile bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
