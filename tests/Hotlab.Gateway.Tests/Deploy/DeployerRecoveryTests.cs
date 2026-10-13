using System.Net;
using Hotlab.Gateway.Deploy;
using static Hotlab.Gateway.Tests.Deploy.DeployTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>Recovering from partial failures: unclear clone outcomes, failed cleanups, stale mappings, address changes.</summary>
public sealed class DeployerRecoveryTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDeployBackend _backend = new();

    private string Out => _temp.Combine("out");

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Clone_error_after_proxmox_accepted_still_cleans_up_by_pool_and_name(HttpStatusCode status)
    {
        _backend.CloneErrorAfterAccept = status;

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        var row = Manifest(Out).Find("alice")!;
        Assert.Equal(DeployStatus.Failed, row.Status);
        Assert.NotNull(row.VmId); // possibly accepted: the id stays recorded
        Assert.Contains($"destroy {row.VmId}", _backend.Calls);
        Assert.DoesNotContain(_backend.Vms.Values, v => v.Name == "hotlab-lab-alice");
        Assert.False(row.UntaggedClone);

        _backend.CloneErrorAfterAccept = null;
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
    }

    [Fact]
    public async Task Failed_cleanup_of_untagged_clone_is_retried_by_the_next_run()
    {
        _backend.CloneExit = _ => "clone failed: storage error";
        _backend.DestroyExit = _ => "VM is locked (clone)";

        Assert.Equal(1, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var row = Manifest(Out).Find("alice")!;
        int partial = row.VmId!.Value;
        Assert.True(_backend.Vms.ContainsKey(partial));
        Assert.DoesNotContain("hotlab", _backend.Vms[partial].Tags); // never tagged
        Assert.Equal(DeployStatus.Failed, row.Status);
        Assert.True(row.UntaggedClone);

        _backend.CloneExit = null;
        _backend.DestroyExit = null;
        var retry = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(0, retry.Code);
        Assert.Equal(2, _backend.Calls.Count(c => c == $"destroy {partial}")); // failed, then retried
        Assert.Contains($"destroying leftover VM {partial}", retry.Out, StringComparison.Ordinal);
        Assert.False(Manifest(Out).Find("alice")!.UntaggedClone);
        Assert.Contains("hotlab", _backend.Clone("alice").Tags); // a single, fresh (tagged) clone
    }

    [Fact]
    public async Task Teardown_removes_untagged_clone_whose_cleanup_failed()
    {
        _backend.CloneExit = _ => "clone failed: storage error";
        _backend.DestroyExit = _ => "VM is locked (clone)";
        Assert.Equal(1, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        int partial = Manifest(Out).Find("alice")!.VmId!.Value;
        _backend.DestroyExit = null;

        Assert.Equal(0, (await TeardownAsync(_backend, Out)).Code);
        Assert.False(_backend.Vms.ContainsKey(partial));
        Assert.DoesNotContain("hotlab-lab", _backend.Pools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the gateway users file is the output users.json itself
    public async Task Mappings_of_users_whose_redeploy_failed_are_pruned(bool aliased)
    {
        var target = aliased ? Path.Combine(Out, DeploymentOutputs.UsersJsonFile) : _temp.Combine("gateway", "users.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, """{"Hotlab":{"Users":{"zed":{"Node":"pve2","VmId":150}}}}""");
        var request = Request(Out, "alice", "bob") with { UsersFileTarget = target };
        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);
        _backend.Vms.TryRemove(_backend.Clone("bob").VmId, out _);
        _backend.FailPasswordFor.Add("hotlab-lab-bob");

        var result = await DeployAsync(_backend, request);

        Assert.Equal(1, result.Code);
        var map = UserMapFile.ReadMappings(target);
        Assert.Equal(["alice", "zed"], map.Keys.Order());
        Assert.Equal(_backend.Clone("alice").VmId, map["alice"]);
        Assert.Contains("removed 1", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Address_change_stays_pending_until_every_rdp_file_is_regenerated()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice", "bob"))).Code);
        var bobRdp = Path.Combine(Out, "bob.rdp");
        File.Delete(bobRdp);
        Directory.CreateDirectory(bobRdp); // makes regenerating bob's file fail
        var moved = Request(Out, "alice", "bob") with { Address = "new-gw.example.org:3389" };

        var failed = await DeployAsync(_backend, moved with { Force = true });

        Assert.Equal(1, failed.Code);
        var manifest = Manifest(Out);
        Assert.Equal("lab.example.org:3389", manifest.Address);
        Assert.Equal("new-gw.example.org:3389", manifest.PendingAddress);

        // The retry still sees the change (no --force needed to finish what was forced).
        Directory.Delete(bobRdp);
        var retry = await DeployAsync(_backend, moved);

        Assert.Equal(0, retry.Code);
        manifest = Manifest(Out);
        Assert.Equal("new-gw.example.org:3389", manifest.Address);
        Assert.Null(manifest.PendingAddress);
        foreach (var user in new[] { "alice", "bob" })
        {
            Assert.StartsWith("full address:s:new-gw.example.org:3389\r\n",
                File.ReadAllText(Path.Combine(Out, $"{user}.rdp")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Pending_address_change_to_a_third_address_needs_force()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        var manifest = Manifest(Out);
        manifest.PendingAddress = "new-gw.example.org:3389";
        manifest.Save(Out);

        var refused = await DeployAsync(_backend, Request(Out, "alice") with { Address = "third.example.org" });
        Assert.Equal(2, refused.Code);
        Assert.Contains("pending", refused.Err, StringComparison.Ordinal);

        // Going back to the recorded address regenerates everything for it.
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);
        Assert.Null(Manifest(Out).PendingAddress);
        Assert.StartsWith("full address:s:lab.example.org:3389\r\n",
            File.ReadAllText(Path.Combine(Out, "alice.rdp")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbolic_link_output_directory_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        var real = _temp.Combine("real");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Out, real);

        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        Assert.Contains("symbolic link", result.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(real));
    }
}
