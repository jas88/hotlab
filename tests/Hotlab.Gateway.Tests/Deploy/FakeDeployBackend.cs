using System.Collections.Concurrent;
using Hotlab.Gateway.Deploy;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>In-memory Proxmox cluster for deployment tests.</summary>
internal sealed partial class FakeDeployBackend : IDeployBackend
{
    public const int SourceId = 9000;
    public const string Node = "pve1";

    private readonly Lock _lock = new();
    private readonly Dictionary<int, AgentExecStatus> _exec = [];
    private readonly HashSet<int> _polled = [];
    private readonly Dictionary<string, (int VmId, string Exit)> _tasks = [];
    private readonly TaskCompletionSource _startsOverlapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId = 200;
    private int _pid;
    private int _activeClones;
    private int _running;
    private int _activeStarts;

    public FakeDeployBackend(bool sourceIsTemplate = true)
    {
        Vms[SourceId] = new FakeVm(SourceId, Node, "tpl")
        {
            Template = sourceIsTemplate,
            Tags = { "linux" },
            Accounts = { ["daemon"] = 1, ["student"] = 1001 },
        };
    }

    public ConcurrentDictionary<int, FakeVm> Vms { get; } = new();
    public HashSet<string> Pools { get; } = [];
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<(int VmId, string Account, string Password)> PasswordSets { get; } = new();
    public ConcurrentQueue<IReadOnlyList<string>> ExecCommands { get; } = new();

    /// <summary>Clone exit status by new VM name; default "OK". The VM is still created (a partial clone).</summary>
    public Func<string, string>? CloneExit { get; set; }

    /// <summary>Throws from set-user-password for these VM names.</summary>
    public HashSet<string> FailPasswordFor { get; } = [];

    /// <summary>Throws a non-Proxmox exception from set-user-password for these VM names.</summary>
    public HashSet<string> CrashPasswordFor { get; } = [];

    /// <summary>Destroy exit status by VMID; default "OK" (the VM is only removed on "OK").</summary>
    public Func<int, string>? DestroyExit { get; set; }

    /// <summary>Runs after a VM is hard-stopped (e.g. to simulate the VMID being reused).</summary>
    public Action<int>? AfterStop { get; set; }

    /// <summary>Guest agent never answers for these VM names.</summary>
    public HashSet<string> AgentDownFor { get; } = [];

    public string OsId { get; set; } = "debian";

    /// <summary>When set, nextid returns this (simulates a VMID race).</summary>
    public int? ForcedNextId { get; set; }
    public string ShutdownExit { get; set; } = "OK";
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    public int MaxConcurrentClones { get; private set; }
    public int MaxRunning { get; private set; }

    /// <summary>
    /// When set, each VM start waits (up to 30 s) until this many starts are in flight at once,
    /// so a run that provisions sequentially never reaches it.
    /// </summary>
    public int? StartRendezvous { get; set; }

    /// <summary>Most VM starts in flight at once.</summary>
    public int MaxConcurrentStarts { get; private set; }

    /// <summary>Runs after a clone task is accepted, before it is waited for (e.g. to simulate a race).</summary>
    public Action<int>? AfterCloneAccepted { get; set; }

    public FakeVm Clone(string user) => Vms.Values.Single(v => v.Name.EndsWith("-" + user, StringComparison.Ordinal));

    public Task<int> GetNextVmIdAsync(CancellationToken ct)
    {
        if (ForcedNextId is int forced)
            return Task.FromResult(forced);
        lock (_lock)
        {
            while (Vms.ContainsKey(_nextId))
                _nextId++;
            return Task.FromResult(_nextId);
        }
    }

    public Task<bool> IsTemplateAsync(string node, int vmId, CancellationToken ct) => Task.FromResult(Get(vmId).Template);

    public Task<bool> PoolExistsAsync(string pool, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(Pools.Contains(pool));
    }

    public Task CreatePoolAsync(string pool, string comment, CancellationToken ct)
    {
        Calls.Enqueue($"create-pool {pool}");
        lock (_lock)
            Pools.Add(pool);
        return Task.CompletedTask;
    }

    public Task DeletePoolAsync(string pool, CancellationToken ct)
    {
        Calls.Enqueue($"delete-pool {pool}");
        lock (_lock)
        {
            if (Vms.Values.Any(v => v.Pool == pool))
                throw new InvalidOperationException("pool not empty");
            Pools.Remove(pool);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProxmoxPoolMember>> GetPoolMembersAsync(string pool, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProxmoxPoolMember>>(Vms.Values.Where(v => v.Pool == pool)
            .Select(v => new ProxmoxPoolMember($"qemu/{v.VmId}", "qemu", v.VmId, v.Node)).ToList());

    public async Task<string> StartCloneAsync(
        string node, int sourceVmId, int newVmId, string name, bool fullClone, string pool, CancellationToken ct)
    {
        int active = Interlocked.Increment(ref _activeClones);
        lock (_lock)
            MaxConcurrentClones = Math.Max(MaxConcurrentClones, active);
        bool accepted = false;
        try
        {
            Calls.Enqueue($"clone {sourceVmId}->{newVmId} {name} full={fullClone} pool={pool}");
            await Task.Delay(5, ct);
            var source = Get(sourceVmId);
            if (!Vms.TryAdd(newVmId, new FakeVm(newVmId, node, name)
            {
                Pool = pool,
                Tags = new(source.Tags, StringComparer.OrdinalIgnoreCase),
                Accounts = new(source.Accounts),
            }))
            {
                throw new ProxmoxApiException(System.Net.HttpStatusCode.InternalServerError, "POST", "clone",
                    $"unable to create VM {newVmId}: config file already exists");
            }
            var upid = $"UPID:{node}:clone:{newVmId}";
            lock (_lock)
                _tasks[upid] = (newVmId, CloneExit?.Invoke(name) ?? "OK");
            accepted = true;
            AfterCloneAccepted?.Invoke(newVmId);
            return upid;
        }
        finally
        {
            if (!accepted)
                Interlocked.Decrement(ref _activeClones);
        }
    }

    public async Task<string> WaitForTaskAsync(string node, string taskId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1, ct);
            lock (_lock)
                return _tasks[taskId].Exit;
        }
        finally
        {
            Interlocked.Decrement(ref _activeClones);
        }
    }

    public Task<IReadOnlySet<string>> GetTagsAsync(string node, int vmId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(Get(vmId).Tags.ToHashSet(StringComparer.OrdinalIgnoreCase));

    public Task<string?> GetNameAsync(string node, int vmId, CancellationToken ct) => Task.FromResult<string?>(Get(vmId).Name);

    public Task SetTagsAsync(string node, int vmId, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        Get(vmId).Tags = new(tags, StringComparer.OrdinalIgnoreCase);
        return Task.CompletedTask;
    }

    public Task<string?> FindVmNodeAsync(int vmId, CancellationToken ct) =>
        Task.FromResult(Vms.TryGetValue(vmId, out var vm) ? vm.Node : null);

    public Task<string> GetStatusAsync(string node, int vmId, CancellationToken ct) => Task.FromResult(Get(vmId).Status);

    public async Task<string> StartAsync(string node, int vmId, CancellationToken ct)
    {
        Calls.Enqueue($"start {vmId}");
        var vm = Get(vmId);
        vm.Status = "running";
        int running = Interlocked.Increment(ref _running);
        int starts = Interlocked.Increment(ref _activeStarts);
        lock (_lock)
        {
            MaxRunning = Math.Max(MaxRunning, running);
            MaxConcurrentStarts = Math.Max(MaxConcurrentStarts, starts);
        }
        try
        {
            if (StartRendezvous is int wanted)
            {
                if (starts >= wanted)
                    _startsOverlapped.TrySetResult();
                try
                {
                    await _startsOverlapped.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                catch (TimeoutException)
                {
                }
            }
            await Task.Delay(StartDelay, ct);
            return "OK";
        }
        finally
        {
            Interlocked.Decrement(ref _activeStarts);
        }
    }

    public Task<string> ShutdownAsync(string node, int vmId, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Enqueue($"shutdown {vmId}");
        if (ShutdownExit == "OK")
            PowerOff(Get(vmId));
        return Task.FromResult(ShutdownExit);
    }

    public Task<string> StopAsync(string node, int vmId, CancellationToken ct)
    {
        Calls.Enqueue($"stop {vmId}");
        PowerOff(Get(vmId));
        AfterStop?.Invoke(vmId);
        return Task.FromResult("OK");
    }

    public Task<string> DestroyAsync(string node, int vmId, CancellationToken ct)
    {
        Calls.Enqueue($"destroy {vmId}");
        if (vmId == SourceId)
            throw new InvalidOperationException("Test bug: destroying the source");
        var exit = DestroyExit?.Invoke(vmId) ?? "OK";
        if (exit == "OK")
            Vms.TryRemove(vmId, out _);
        return Task.FromResult(exit);
    }

    private void PowerOff(FakeVm vm)
    {
        if (vm.Status == "running")
            Interlocked.Decrement(ref _running);
        vm.Status = "stopped";
    }

    private FakeVm Get(int vmId) =>
        Vms.TryGetValue(vmId, out var vm)
            ? vm
            : throw new ProxmoxApiException(System.Net.HttpStatusCode.InternalServerError, "GET", $"qemu/{vmId}",
                "Configuration file does not exist");
}
