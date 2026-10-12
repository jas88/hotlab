using System.Net;
using System.Net.Sockets;
using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Activation;

public class VmActivatorTests
{
    private static readonly VmTarget Target = new("pve1", 123);

    private static VmActivator Create(
        FakeVmControl control, IPortProbe? probe = null, TimeSpan? readyTimeout = null) =>
        new(
            control,
            Options.Create(new VmActivatorOptions
            {
                ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(10),
                PollInterval = TimeSpan.FromMilliseconds(5),
                ProbeTimeout = TimeSpan.FromMilliseconds(200),
            }),
            NullLogger<VmActivator>.Instance,
            probe ?? new FakePortProbe());

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
        var control = new FakeVmControl();
        var probe = new FakePortProbe(_ => false);
        var activator = Create(control, probe, readyTimeout: TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<VmActivationException>(
            () => activator.EnsureReadyAsync(Target, CancellationToken.None));

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met");
            await Task.Delay(5);
        }
    }
}
