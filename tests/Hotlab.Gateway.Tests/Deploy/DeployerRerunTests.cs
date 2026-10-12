using Hotlab.Gateway.Deploy;
using static Hotlab.Gateway.Tests.Deploy.DeployTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>Re-runs, per-user failures and the gateway users file.</summary>
public sealed class DeployerRerunTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDeployBackend _backend = new();

    private string Out => _temp.Combine("out");
    private string Target => _temp.Combine("gateway", "users.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Rerun_skips_deployed_users_and_adds_new_ones()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice", "bob"))).Code);
        var alicePassword = DeploymentOutputs.ReadPasswords(Out)["alice"].Password;
        var aliceRdp = File.ReadAllText(Path.Combine(Out, "alice.rdp"));
        int clones = _backend.Calls.Count(c => c.StartsWith("clone", StringComparison.Ordinal));

        var result = await DeployAsync(_backend, Request(Out, "alice", "bob", "carol"));

        Assert.Equal(0, result.Code);
        Assert.Equal(clones + 1, _backend.Calls.Count(c => c.StartsWith("clone", StringComparison.Ordinal)));
        Assert.Contains("alice: already deployed", result.Out, StringComparison.Ordinal);
        var passwords = DeploymentOutputs.ReadPasswords(Out);
        Assert.Equal(3, passwords.Count);
        Assert.Equal(alicePassword, passwords["alice"].Password);
        Assert.Equal(aliceRdp, File.ReadAllText(Path.Combine(Out, "alice.rdp")));
        Assert.Equal(3, Manifest(Out).Users.Count);
    }

    [Fact]
    public async Task Rerun_with_subset_keeps_other_rows()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice", "bob"))).Code);
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "carol"))).Code);

        Assert.Equal(3, DeploymentOutputs.ReadPasswords(Out).Count);
        Assert.Equal(3, Manifest(Out).Users.Count(u => u.Status == DeployStatus.Deployed));
        Assert.Contains("\"bob\"", File.ReadAllText(Path.Combine(Out, "users.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerun_redeploys_user_whose_vm_vanished()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var old = _backend.Clone("alice").VmId;
        _backend.Vms.TryRemove(old, out _);

        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);

        var row = Manifest(Out).Find("alice")!;
        Assert.Equal(2, _backend.Calls.Count(c => c.StartsWith("clone", StringComparison.Ordinal)));
        Assert.True(_backend.Vms.ContainsKey(row.VmId!.Value));
        Assert.Equal(row.VmId, DeploymentOutputs.ReadPasswords(Out)["alice"].VmId);
    }

    [Fact]
    public async Task Rerun_destroys_own_leftover_of_unfinished_user_but_not_untagged_one()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice", "bob"))).Code);
        var manifest = Manifest(Out);
        int aliceOld = manifest.Find("alice")!.VmId!.Value, bobOld = manifest.Find("bob")!.VmId!.Value;
        manifest.Find("alice")!.Status = DeployStatus.Provisioning; // as if interrupted
        manifest.Find("bob")!.Status = DeployStatus.Failed;
        manifest.Save(Out);
        _backend.Vms[bobOld].Tags.Remove("hotlab"); // no longer recognisably ours

        var result = await DeployAsync(_backend, Request(Out, "alice", "bob"));

        Assert.Equal(0, result.Code);
        Assert.Contains($"destroy {aliceOld}", _backend.Calls);
        Assert.DoesNotContain($"destroy {bobOld}", _backend.Calls);
        Assert.True(_backend.Vms.ContainsKey(bobOld));
        Assert.Contains("leaving it alone", result.Err, StringComparison.Ordinal);
        Assert.NotEqual(bobOld, Manifest(Out).Find("bob")!.VmId);
    }

    [Fact]
    public async Task Rerun_with_different_source_is_refused()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);

        var result = await DeployAsync(_backend, Request(Out, "bob") with { SourceVmId = 9001 });

        Assert.Equal(2, result.Code);
        Assert.Contains("belongs to deployment 'lab'", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_failure_is_recorded_partial_clone_destroyed_and_others_continue()
    {
        _backend.CloneExit = name => name.EndsWith("-bob", StringComparison.Ordinal) ? "clone failed: out of space" : "OK";

        var result = await DeployAsync(_backend, Request(Out, "alice", "bob", "carol"));

        Assert.Equal(1, result.Code);
        var bob = Manifest(Out).Find("bob")!;
        Assert.Equal(DeployStatus.Failed, bob.Status);
        Assert.Contains("out of space", bob.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Vms.Values, v => v.Name == "hotlab-lab-bob");
        Assert.Contains($"destroy {bob.VmId}", _backend.Calls);
        Assert.Equal(["alice", "carol"], DeploymentOutputs.ReadPasswords(Out).Keys.Order());
        Assert.False(File.Exists(Path.Combine(Out, "bob.rdp")));
        Assert.Contains("bob: FAILED", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provisioning_failure_destroys_clone_and_rerun_retries()
    {
        _backend.FailPasswordFor.Add("hotlab-lab-bob");
        Assert.Equal(1, (await DeployAsync(_backend, Request(Out, "alice", "bob"))).Code);
        var failedVm = Manifest(Out).Find("bob")!.VmId!.Value;
        Assert.False(_backend.Vms.ContainsKey(failedVm));
        Assert.False(DeploymentOutputs.ReadPasswords(Out).ContainsKey("bob"));

        _backend.FailPasswordFor.Clear();
        var retry = await DeployAsync(_backend, Request(Out, "alice", "bob"));

        Assert.Equal(0, retry.Code);
        Assert.Equal(DeployStatus.Deployed, Manifest(Out).Find("bob")!.Status);
        Assert.True(DeploymentOutputs.ReadPasswords(Out).ContainsKey("bob"));
    }

    [Fact]
    public async Task Agent_timeout_fails_user()
    {
        _backend.AgentDownFor.Add("hotlab-lab-alice");

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        Assert.Contains("guest agent did not respond", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_cleanup_never_destroys_vm_outside_the_pool()
    {
        // Someone else grabs the VMID between nextid and clone: the clone fails, and the
        // foreign VM (not in our pool) must survive.
        _backend.Vms[200] = new FakeVm(200, FakeDeployBackend.Node, "someone-elses");
        _backend.ForcedNextId = 200;

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        Assert.True(_backend.Vms.ContainsKey(200));
        Assert.DoesNotContain("destroy 200", _backend.Calls);
        Assert.Null(Manifest(Out).Find("alice")!.VmId); // the rejected id is not recorded as ours
    }

    [Fact]
    public async Task Merges_into_users_file_target_preserving_other_content()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, """{"Logging":{"LogLevel":{"Default":"Debug"}},"Hotlab":{"Users":{"zed":{"Node":"pve2","VmId":150}}}}""");

        var result = await DeployAsync(_backend, Request(Out, "alice") with { UsersFileTarget = Target });

        Assert.Equal(0, result.Code);
        var map = UserMapFile.ReadMappings(Target);
        Assert.Equal(150, map["zed"]);
        Assert.Equal(_backend.Clone("alice").VmId, map["alice"]);
        Assert.Contains("Debug", File.ReadAllText(Target), StringComparison.Ordinal);
        Assert.Equal(Target, Manifest(Out).UsersFileTarget);
    }

    [Fact]
    public async Task Refuses_to_remap_user_mapped_elsewhere_unless_forced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, """{"Hotlab":{"Users":{"Alice":{"Node":"pve2","VmId":150}}}}""");

        var refused = await DeployAsync(_backend, Request(Out, "alice") with { UsersFileTarget = Target });
        Assert.Equal(1, refused.Code);
        Assert.Contains("--force", refused.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("clone", StringComparison.Ordinal));
        Assert.Equal(150, UserMapFile.ReadMappings(Target)["alice"]);

        var forced = await DeployAsync(_backend, Request(Out, "alice") with { UsersFileTarget = Target, Force = true });
        Assert.Equal(0, forced.Code);
        Assert.Equal(_backend.Clone("alice").VmId, UserMapFile.ReadMappings(Target)["alice"]);
    }

    [Fact]
    public async Task Rerun_does_not_treat_own_mappings_as_conflicts()
    {
        var request = Request(Out, "alice") with { UsersFileTarget = Target };
        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);

        Assert.Equal(0, (await DeployAsync(_backend, request with { Users = ["alice", "bob"] })).Code);
        Assert.Equal(2, UserMapFile.ReadMappings(Target).Count);
    }

    [Fact]
    public async Task Address_change_is_refused_unless_forced_and_then_regenerates_rdp_files()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var moved = Request(Out, "alice", "bob") with { Address = "new-gw.example.org:3389" };

        var refused = await DeployAsync(_backend, moved);
        Assert.Equal(2, refused.Code);
        Assert.Contains("--force", refused.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Calls, c => c.Contains("-bob", StringComparison.Ordinal));
        Assert.Equal("lab.example.org:3389", Manifest(Out).Address);

        var forced = await DeployAsync(_backend, moved with { Force = true });
        Assert.Equal(0, forced.Code);
        Assert.Equal("new-gw.example.org:3389", Manifest(Out).Address);
        foreach (var user in new[] { "alice", "bob" })
        {
            Assert.StartsWith("full address:s:new-gw.example.org:3389\r\n",
                File.ReadAllText(Path.Combine(Out, $"{user}.rdp")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Users_file_target_change_is_refused()
    {
        var request = Request(Out, "alice") with { UsersFileTarget = Target };
        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);

        var other = _temp.Combine("other", "users.json");
        var result = await DeployAsync(_backend, request with { UsersFileTarget = other, Force = true });

        Assert.Equal(2, result.Code);
        Assert.Contains("--users-file-target", result.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(other));
        Assert.Equal(Target, Manifest(Out).UsersFileTarget);
        // The same target, spelt differently, is not a change.
        var same = Path.Combine(_temp.Path, "gateway", "..", "gateway", "users.json");
        Assert.Equal(0, (await DeployAsync(_backend, request with { UsersFileTarget = same })).Code);
    }

    [Fact]
    public async Task Malformed_mapping_is_overwritten_with_a_warning_not_a_conflict()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, """{"Hotlab":{"Users":{"alice":{"Node":"pve2"},"bob":"junk"}}}""");

        var result = await DeployAsync(_backend, Request(Out, "alice", "bob") with { UsersFileTarget = Target });

        Assert.Equal(0, result.Code);
        Assert.Contains("malformed mapping for alice", result.Err, StringComparison.Ordinal);
        Assert.Contains("malformed mapping for bob", result.Err, StringComparison.Ordinal);
        Assert.Equal(_backend.Clone("alice").VmId, UserMapFile.ReadMappings(Target)["alice"]);
        Assert.Equal(_backend.Clone("bob").VmId, UserMapFile.ReadMappings(Target)["bob"]);
    }
}
