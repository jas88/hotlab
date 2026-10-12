using Hotlab.Gateway.Deploy;
using static Hotlab.Gateway.Tests.Deploy.DeployTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

public sealed class TeardownTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDeployBackend _backend = new();

    private string Out => _temp.Combine("out");
    private string Target => _temp.Combine("users.json");

    public void Dispose() => _temp.Dispose();

    private async Task DeployAliceBobAsync()
    {
        File.WriteAllText(Target, """{"Hotlab":{"Users":{"zed":{"Node":"pve2","VmId":150}}}}""");
        var request = Request(Out, "alice", "bob") with { UsersFileTarget = Target };
        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);
    }

    [Fact]
    public async Task Destroys_vms_pool_mappings_and_passwords()
    {
        await DeployAliceBobAsync();
        var ids = Manifest(Out).Users.Select(u => u.VmId!.Value).ToList();

        var result = await TeardownAsync(_backend, Out);

        Assert.Equal(0, result.Code);
        Assert.All(ids, id => Assert.False(_backend.Vms.ContainsKey(id)));
        Assert.True(_backend.Vms.ContainsKey(FakeDeployBackend.SourceId));
        Assert.DoesNotContain("hotlab-lab", _backend.Pools);
        Assert.Equal(["zed"], UserMapFile.ReadMappings(Target).Keys);
        Assert.False(File.Exists(Path.Combine(Out, DeploymentOutputs.PasswordsFile)));
        Assert.All(Manifest(Out).Users, u => Assert.Equal(DeployStatus.Destroyed, u.Status));
        Assert.Contains("2 VM(s) removed, 0 remaining", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Is_idempotent()
    {
        await DeployAliceBobAsync();
        Assert.Equal(0, (await TeardownAsync(_backend, Out)).Code);

        var again = await TeardownAsync(_backend, Out);

        Assert.Equal(0, again.Code);
        Assert.Equal(2, _backend.Calls.Count(c => c.StartsWith("destroy", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Never_destroys_vm_without_hotlab_tag_or_outside_pool()
    {
        await DeployAliceBobAsync();
        var alice = _backend.Clone("alice");
        var bob = _backend.Clone("bob");
        alice.Tags.Remove("hotlab");
        bob.Pool = "someone-else";

        var result = await TeardownAsync(_backend, Out);

        Assert.Equal(1, result.Code);
        Assert.True(_backend.Vms.ContainsKey(alice.VmId));
        Assert.True(_backend.Vms.ContainsKey(bob.VmId));
        Assert.Contains("hotlab-lab", _backend.Pools); // alice is still a member
        Assert.Contains("not destroying", result.Err, StringComparison.Ordinal);
        // Their mappings and passwords stay, since the VMs still exist.
        Assert.Equal(2, DeploymentOutputs.ReadPasswords(Out).Count);
        Assert.Equal(3, UserMapFile.ReadMappings(Target).Count);
    }

    [Fact]
    public async Task Vm_already_gone_counts_as_destroyed()
    {
        await DeployAliceBobAsync();
        _backend.Vms.TryRemove(_backend.Clone("alice").VmId, out _);

        Assert.Equal(0, (await TeardownAsync(_backend, Out)).Code);
        Assert.Equal(DeployStatus.Destroyed, Manifest(Out).Find("alice")!.Status);
    }

    [Fact]
    public async Task Keep_mappings_leaves_users_file()
    {
        await DeployAliceBobAsync();

        Assert.Equal(0, (await TeardownAsync(_backend, Out, keepMappings: true)).Code);

        Assert.Equal(3, UserMapFile.ReadMappings(Target).Count);
    }

    [Fact]
    public async Task Leaves_mapping_of_user_remapped_elsewhere()
    {
        await DeployAliceBobAsync();
        UserMapFile.Merge(Target, [new UserMapping("alice", "pve9", 777)]);

        Assert.Equal(0, (await TeardownAsync(_backend, Out)).Code);

        Assert.Equal(777, UserMapFile.ReadMappings(Target)["alice"]);
        Assert.False(UserMapFile.ReadMappings(Target).ContainsKey("bob"));
    }

    [Fact]
    public async Task Missing_manifest_is_usage_error()
    {
        Directory.CreateDirectory(Out);
        var result = await TeardownAsync(_backend, Out);

        Assert.Equal(2, result.Code);
        Assert.Contains("deployment.json", result.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }
}
