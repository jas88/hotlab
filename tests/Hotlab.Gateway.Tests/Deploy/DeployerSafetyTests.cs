using Hotlab.Gateway.Deploy;
using static Hotlab.Gateway.Tests.Deploy.DeployTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>Ownership checks: VMs that are not (or no longer) ours are never skipped as ours or destroyed.</summary>
public sealed class DeployerSafetyTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDeployBackend _backend = new();

    private string Out => _temp.Combine("out");

    public void Dispose() => _temp.Dispose();

    private void Replace(int vmId, string name, string? pool, params string[] tags) =>
        _backend.Vms[vmId] = new FakeVm(vmId, FakeDeployBackend.Node, name)
        {
            Pool = pool,
            Tags = new(tags, StringComparer.OrdinalIgnoreCase),
        };

    [Theory]
    [InlineData(null)] // VMID reused outside the pool
    [InlineData("hotlab-lab")] // in the pool and tagged, but not the VM we recorded
    public async Task Rerun_does_not_skip_or_touch_a_recorded_vmid_that_is_no_longer_ours(string? pool)
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        int old = Manifest(Out).Find("alice")!.VmId!.Value;
        Replace(old, "someone-elses", pool, "hotlab");

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(0, result.Code);
        Assert.DoesNotContain("already deployed", result.Out, StringComparison.Ordinal);
        Assert.Equal("someone-elses", _backend.Vms[old].Name);
        Assert.DoesNotContain(_backend.Calls, c => c == $"destroy {old}" || c == $"stop {old}");
        var row = Manifest(Out).Find("alice")!;
        Assert.NotEqual(old, row.VmId);
        Assert.Equal("hotlab-lab-alice", _backend.Vms[row.VmId!.Value].Name);
    }

    [Fact]
    public async Task Leftover_cleanup_error_does_not_retry_destroying_the_old_vm()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var manifest = Manifest(Out);
        int old = manifest.Find("alice")!.VmId!.Value;
        manifest.Find("alice")!.Status = DeployStatus.Provisioning; // interrupted
        manifest.Save(Out);
        _backend.DestroyExit = id => id == old ? "VM is locked (clone)" : "OK";

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        Assert.Single(_backend.Calls, $"destroy {old}"); // only the leftover attempt, no failure "cleanup"
        Assert.Contains("locked", Manifest(Out).Find("alice")!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_clone_cleanup_requires_the_expected_name()
    {
        // The clone was accepted but failed, and the VMID now holds another VM in our pool.
        _backend.CloneExit = _ => "clone failed: storage error";
        _backend.AfterCloneAccepted = id => Replace(id, "hotlab-lab-intruder", "hotlab-lab");

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        Assert.Contains(_backend.Vms.Values, v => v.Name == "hotlab-lab-intruder");
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("destroy", StringComparison.Ordinal));
        Assert.Contains("not destroying it", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerun_destroys_untagged_clone_left_by_an_interrupted_clone()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var manifest = Manifest(Out);
        int old = manifest.Find("alice")!.VmId!.Value;
        manifest.Find("alice")!.Status = DeployStatus.Cloning; // cancelled while cloning, before tagging
        manifest.Save(Out);
        _backend.Vms[old].Tags.Remove("hotlab");

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(0, result.Code);
        Assert.Contains($"destroy {old}", _backend.Calls);
        Assert.Contains("hotlab", _backend.Clone("alice").Tags); // a single, fresh clone
    }

    [Fact]
    public async Task Interrupted_clone_with_another_name_is_left_alone()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var manifest = Manifest(Out);
        int old = manifest.Find("alice")!.VmId!.Value;
        manifest.Find("alice")!.Status = DeployStatus.Cloning;
        manifest.Save(Out);
        Replace(old, "hotlab-lab-intruder", "hotlab-lab");

        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        Assert.True(_backend.Vms.ContainsKey(old));
        Assert.DoesNotContain($"destroy {old}", _backend.Calls);
    }

    [Fact]
    public async Task Teardown_rechecks_ownership_after_stopping()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice") with { LeaveRunning = true })).Code);
        int id = _backend.Clone("alice").VmId;
        _backend.AfterStop = stopped => Replace(stopped, "reused", pool: null);

        var result = await TeardownAsync(_backend, Out);

        Assert.Equal(1, result.Code);
        Assert.Contains($"stop {id}", _backend.Calls);
        Assert.DoesNotContain($"destroy {id}", _backend.Calls);
        Assert.Equal("reused", _backend.Vms[id].Name);
        Assert.Contains("no longer", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_exception_fails_only_that_user_and_removes_its_clone()
    {
        _backend.CrashPasswordFor.Add("hotlab-lab-bob");

        var result = await DeployAsync(_backend, Request(Out, "alice", "bob", "carol"));

        Assert.Equal(1, result.Code);
        var manifest = Manifest(Out);
        Assert.Equal(DeployStatus.Failed, manifest.Find("bob")!.Status);
        Assert.Contains("unexpected agent reply", manifest.Find("bob")!.Error, StringComparison.Ordinal);
        Assert.Equal(DeployStatus.Deployed, manifest.Find("carol")!.Status);
        Assert.DoesNotContain(_backend.Vms.Values, v => v.Name == "hotlab-lab-bob");
    }

    [Fact]
    public async Task Tags_are_matched_case_insensitively()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var vm = _backend.Clone("alice");
        vm.Tags = new HashSet<string>(StringComparer.Ordinal) { "HotLab" }; // as Proxmox may report it

        Assert.Equal(0, (await TeardownAsync(_backend, Out)).Code);
        Assert.False(_backend.Vms.ContainsKey(vm.VmId));
    }
}
