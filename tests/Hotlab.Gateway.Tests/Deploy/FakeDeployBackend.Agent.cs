using System.Collections.Concurrent;
using Hotlab.Proxmox;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>Guest agent side of the fake cluster.</summary>
internal sealed partial class FakeDeployBackend
{
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
        if (CrashPasswordFor.Contains(vm.Name))
            throw new InvalidOperationException("unexpected agent reply");
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
