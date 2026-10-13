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
        Assert.Empty(Directory.GetFiles(Out, "*.rdp")); // they hold signed routing tokens
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

    [Fact]
    public async Task Keeps_rdp_file_of_vm_that_survived()
    {
        await DeployAliceBobAsync();
        _backend.Clone("alice").Tags.Remove("hotlab");

        Assert.Equal(1, (await TeardownAsync(_backend, Out)).Code);

        Assert.True(File.Exists(Path.Combine(Out, "alice.rdp")));
        Assert.False(File.Exists(Path.Combine(Out, "bob.rdp")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Users_file_target_that_is_the_output_users_json_is_not_clobbered(bool keepMappings)
    {
        var aliased = Path.Combine(Out, DeploymentOutputs.UsersJsonFile);
        Directory.CreateDirectory(Out);
        File.WriteAllText(aliased, """{"Hotlab":{"Users":{"zed":{"Node":"pve2","VmId":150}}}}""");
        var request = Request(Out, "alice", "bob") with { UsersFileTarget = aliased };
        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);
        Assert.Equal(3, UserMapFile.ReadMappings(aliased).Count); // deploy merged rather than overwrote

        Assert.Equal(0, (await TeardownAsync(_backend, Out, keepMappings)).Code);

        var expected = keepMappings ? new[] { "alice", "bob", "zed" } : ["zed"];
        Assert.Equal(expected, UserMapFile.ReadMappings(aliased).Keys.Order());
    }

    [Fact]
    public async Task Unreadable_passwords_file_is_reported_not_thrown()
    {
        await DeployAliceBobAsync();
        File.WriteAllText(Path.Combine(Out, DeploymentOutputs.PasswordsFile), "user,guest_account,password,vmid,rdp_file\nbroken\n");

        var result = await TeardownAsync(_backend, Out);

        Assert.Equal(1, result.Code);
        Assert.Contains("malformed", result.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("destroy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_users_file_target_is_reported_not_thrown()
    {
        await DeployAliceBobAsync();
        File.WriteAllText(Target, "[1,2]");

        var result = await TeardownAsync(_backend, Out);

        Assert.Equal(1, result.Code);
        Assert.Contains("Could not update", result.Err, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Out, "*.rdp")); // local cleanup still happened
        Assert.All(Manifest(Out).Users, u => Assert.Equal(DeployStatus.Destroyed, u.Status));
    }

    [Theory]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":null}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[null]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":null,"status":"deployed"}]}""")]
    [InlineData("""{"name":null,"node":"pve1","users":[]}""")]
    // Missing properties must not keep their initializers: a missing status would read as "cloning".
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"alice","guestAccount":"alice","vmId":200,"vmName":"x"}]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"alice","status":"failed","vmId":200}]}""")]
    [InlineData("""{"name":"lab","node":"pve1","address":"gw","users":[]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","users":[]}""")]
    // Empty strings are no better.
    [InlineData("""{"name":"lab","node":"pve1","pool":"","address":"gw","users":[]}""")]
    [InlineData("""{"name":"lab","node":"","pool":"hotlab-lab","address":"gw","users":[]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"alice","guestAccount":"","status":"failed"}]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"alice","guestAccount":"alice","status":"bogus"}]}""")]
    // Users name files in the output directory.
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"../../victim","guestAccount":"a","status":"deployed"}]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"Alice","guestAccount":"a","status":"deployed"}]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","users":[{"user":"alice","guestAccount":"a","status":"failed"},{"user":"alice","guestAccount":"a","status":"failed"}]}""")]
    // An unusable users file target is caught before anything is destroyed.
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","usersFileTarget":"relative.json","users":[]}""")]
    [InlineData("""{"name":"lab","node":"pve1","pool":"hotlab-lab","address":"gw","usersFileTarget":"/tmp/a\u0000b","users":[]}""")]
    public async Task Structurally_invalid_manifest_is_reported(string json)
    {
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, DeploymentManifest.FileName), json);

        Assert.Throws<InvalidDataException>(() => DeploymentManifest.Load(Out));
        var teardown = await TeardownAsync(_backend, Out);
        var deploy = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, teardown.Code);
        Assert.Equal(1, deploy.Code);
        Assert.Contains("not a valid deployment manifest", deploy.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }
}
