using System.Collections.Concurrent;
using Hotlab.Gateway.Deploy;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>In-memory Proxmox cluster for deployment tests.</summary>
internal sealed class FakeDeployBackend : IDeployBackend
{
    public const int SourceId = 9000;
    public const string Node = "pve1";

    private readonly Lock _lock = new();
    private readonly Dictionary<int, AgentExecStatus> _exec = [];
    private readonly HashSet<int> _polled = [];
    private int _nextId = 200;
    private int _pid;
    private int _activeClones;
    private int _running;

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

    /// <summary>Guest agent never answers for these VM names.</summary>
    public HashSet<string> AgentDownFor { get; } = [];

    public string OsId { get; set; } = "debian";

    /// <summary>When set, nextid returns this (simulates a VMID race).</summary>
    public int? ForcedNextId { get; set; }
    public string ShutdownExit { get; set; } = "OK";
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    public int MaxConcurrentClones { get; private set; }
    public int MaxRunning { get; private set; }

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

    public async Task<string> CloneAsync(
        string node, int sourceVmId, int newVmId, string name, bool fullClone, string pool, CancellationToken ct)
    {
        int active = Interlocked.Increment(ref _activeClones);
        lock (_lock)
            MaxConcurrentClones = Math.Max(MaxConcurrentClones, active);
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
                return "unable to create VM: config file already exists";
            }
            return CloneExit?.Invoke(name) ?? "OK";
        }
        finally
        {
            Interlocked.Decrement(ref _activeClones);
        }
    }

    public Task<IReadOnlySet<string>> GetTagsAsync(string node, int vmId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(Get(vmId).Tags.ToHashSet());

    public Task SetTagsAsync(string node, int vmId, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        Get(vmId).Tags = [.. tags];
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
        lock (_lock)
            MaxRunning = Math.Max(MaxRunning, running);
        await Task.Delay(StartDelay, ct);
        return "OK";
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
        return Task.FromResult("OK");
    }

    public Task<string> DestroyAsync(string node, int vmId, CancellationToken ct)
    {
        Calls.Enqueue($"destroy {vmId}");
        if (vmId == SourceId)
            throw new InvalidOperationException("Test bug: destroying the source");
        Vms.TryRemove(vmId, out _);
        return Task.FromResult("OK");
    }

    public Task<bool> AgentPingAsync(string node, int vmId, CancellationToken ct)
    {
        var vm = Get(vmId);
        return Task.FromResult(vm.Status == "running" && !AgentDownFor.Contains(vm.Name));
    }

    public Task<AgentOsInfo> AgentGetOsInfoAsync(string node, int vmId, CancellationToken ct) =>
        Task.FromResult(new AgentOsInfo(OsId, OsId));

    public Task<int> AgentExecAsync(string node, int vmId, IReadOnlyList<string> command, CancellationToken ct)
    {
        ExecCommands.Enqueue(command);
        var vm = Get(vmId);
        AgentExecStatus result = command[0] switch
        {
            "id" when vm.Accounts.TryGetValue(command[^1], out int uid) => new(true, 0, null, $"{uid}\n", ""),
            "id" => new(true, 1, null, "", $"id: '{command[^1]}': no such user\n"),
            "useradd" => AddAccount(vm, command[^1]),
            _ => new(true, 127, null, "", "not found"),
        };
        lock (_lock)
        {
            int pid = ++_pid;
            _exec[pid] = result;
            return Task.FromResult(pid);
        }
    }

    public Task<AgentExecStatus> AgentExecStatusAsync(string node, int vmId, int pid, CancellationToken ct)
    {
        lock (_lock)
        {
            // First poll: still running; later polls: the result (exercises polling).
            if (_polled.Add(pid))
                return Task.FromResult(new AgentExecStatus(false, null, null, "", ""));
            return Task.FromResult(_exec[pid]);
        }
    }

    public Task AgentSetUserPasswordAsync(string node, int vmId, string user, string password, CancellationToken ct)
    {
        var vm = Get(vmId);
        if (FailPasswordFor.Contains(vm.Name))
            throw new ProxmoxApiException(System.Net.HttpStatusCode.InternalServerError, "POST", "set-user-password", "agent error");
        if (!vm.Accounts.ContainsKey(user))
            throw new ProxmoxApiException(System.Net.HttpStatusCode.InternalServerError, "POST", "set-user-password",
                "chpasswd: user does not exist");
        PasswordSets.Enqueue((vmId, user, password));
        return Task.CompletedTask;
    }

    private static AgentExecStatus AddAccount(FakeVm vm, string user)
    {
        vm.Accounts[user] = 1001 + vm.Accounts.Count;
        return new AgentExecStatus(true, 0, null, "", "");
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

internal sealed class FakeVm(int vmId, string node, string name)
{
    public int VmId { get; } = vmId;
    public string Node { get; } = node;
    public string Name { get; } = name;
    public bool Template { get; init; }
    public string? Pool { get; set; }
    public string Status { get; set; } = "stopped";
    public HashSet<string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Accounts { get; init; } = new();
}
