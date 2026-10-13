using Hotlab.Gateway.Deploy;
using Hotlab.Gateway.Tokens;
using Hotlab.Gateway.Tests.Tokens;
using static Hotlab.Gateway.Tests.Deploy.DeployTestSupport;
using static Hotlab.Gateway.Tests.Tokens.TokenTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

public sealed class DeployerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDeployBackend _backend = new();

    private string Out => _temp.Combine("out");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Deploys_one_linked_clone_per_user_into_tagged_pool()
    {
        var result = await DeployAsync(_backend, Request(Out, "alice", "bob"));

        Assert.Equal(0, result.Code);
        Assert.Contains("create-pool hotlab-lab", _backend.Calls);
        Assert.Contains(_backend.Calls, c => c.StartsWith("clone 9000->200 hotlab-lab-alice full=False pool=hotlab-lab", StringComparison.Ordinal));
        Assert.Contains(_backend.Calls, c => c.StartsWith("clone 9000->201 hotlab-lab-bob full=False", StringComparison.Ordinal));
        foreach (var user in new[] { "alice", "bob" })
        {
            var vm = _backend.Clone(user);
            Assert.Equal("hotlab-lab", vm.Pool);
            Assert.Contains("hotlab", vm.Tags);
            Assert.Contains("linux", vm.Tags); // source tags kept
            Assert.True(vm.Accounts.ContainsKey(user));
            Assert.Equal("stopped", vm.Status);
        }
        Assert.Contains(_backend.ExecCommands, c => c.SequenceEqual(["useradd", "-m", "-s", "/bin/bash", "alice"]));
        Assert.Equal(2, _backend.PasswordSets.Count);
    }

    [Fact]
    public async Task Non_template_source_gets_full_clones()
    {
        var backend = new FakeDeployBackend(sourceIsTemplate: false);
        var result = await DeployAsync(backend, Request(Out, "alice"));

        Assert.Equal(0, result.Code);
        Assert.Contains(backend.Calls, c => c.Contains("full=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writes_rdp_passwords_users_json_and_manifest()
    {
        var time = new ManualTimeProvider(Epoch);
        var request = Request(Out, "alice", "bob") with { Lifetime = TimeSpan.FromDays(120) };
        Assert.Equal(0, (await DeployAsync(_backend, request, time)).Code);

        var lines = File.ReadAllText(Path.Combine(Out, "alice.rdp")).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("full address:s:lab.example.org:3389", lines[0]);
        Assert.Equal("username:s:alice", lines[1]);
        var validation = Create(time).Validate(lines[2]["loadbalanceinfo:s:".Length..]);
        Assert.Equal(TokenStatus.Valid, validation.Status);
        Assert.Equal("alice", validation.User);
        Assert.Equal(Epoch.AddDays(120), validation.Expiry);

        var passwords = DeploymentOutputs.ReadPasswords(Out);
        var alice = passwords["alice"];
        Assert.Equal(_backend.Clone("alice").VmId, alice.VmId);
        Assert.Equal("alice.rdp", alice.RdpFile);
        Assert.Equal(16, alice.Password.Length);
        Assert.All(alice.Password, c => Assert.Contains(c, DeployNames.PasswordAlphabet));
        Assert.Contains(_backend.PasswordSets, p => p.Account == "alice" && p.Password == alice.Password);
        Assert.NotEqual(alice.Password, passwords["bob"].Password);
        Assert.StartsWith("user,guest_account,password,vmid,rdp_file\n", File.ReadAllText(Path.Combine(Out, "passwords.csv")), StringComparison.Ordinal);

        var usersJson = File.ReadAllText(Path.Combine(Out, "users.json"));
        Assert.Contains("\"Hotlab\"", usersJson, StringComparison.Ordinal);
        Assert.Contains($"\"VmId\": {alice.VmId}", usersJson, StringComparison.Ordinal);

        var manifest = Manifest(Out);
        Assert.Equal("lab", manifest.Name);
        Assert.Equal("hotlab-lab", manifest.Pool);
        Assert.All(manifest.Users, u => Assert.Equal(DeployStatus.Deployed, u.Status));
        Assert.Equal("hotlab-lab-bob", manifest.Find("bob")!.VmName);
        Assert.DoesNotContain(alice.Password, File.ReadAllText(Path.Combine(Out, DeploymentManifest.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_files_are_private()
    {
        if (OperatingSystem.IsWindows())
            return;
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);

        Assert.Equal(AtomicFile.PrivateDirectory, Mode(Out));
        foreach (var file in new[] { "passwords.csv", "alice.rdp", "users.json", "deployment.json" })
            Assert.Equal(AtomicFile.PrivateFile, Mode(Path.Combine(Out, file)));
        Assert.Empty(Directory.GetFiles(Out, "*.tmp"));
    }

    [Fact]
    public async Task Passwords_never_appear_in_output()
    {
        var result = await DeployAsync(_backend, Request(Out, "alice", "bob"));

        Assert.All(_backend.PasswordSets, p => Assert.DoesNotContain(p.Password, result.All, StringComparison.Ordinal));
        Assert.All(_backend.ExecCommands, c => Assert.DoesNotContain(c, a => _backend.PasswordSets.Any(p => a.Contains(p.Password, StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Clones_run_sequentially_and_provisioning_in_bounded_parallel()
    {
        _backend.StartRendezvous = 2; // each start waits until two are in flight
        var request = Request(Out, "u1", "u2", "u3", "u4", "u5", "u6") with { Parallel = 2 };

        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);

        Assert.Equal(1, _backend.MaxConcurrentClones);
        Assert.Equal(2, _backend.MaxConcurrentStarts); // parallel, but bounded
        Assert.InRange(_backend.MaxRunning, 1, 2);
        Assert.Equal(6, _backend.PasswordSets.Count);
    }

    [Fact]
    public async Task Existing_regular_account_is_reused_not_recreated()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "student"))).Code);

        Assert.DoesNotContain(_backend.ExecCommands, c => c[0] == "useradd");
        Assert.Contains(_backend.PasswordSets, p => p.Account == "student");
    }

    [Fact]
    public async Task Existing_system_account_is_refused()
    {
        var result = await DeployAsync(_backend, Request(Out, "daemon"));

        Assert.Equal(1, result.Code);
        Assert.Contains("system account", result.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.PasswordSets);
        Assert.Equal(DeployStatus.Failed, Manifest(Out).Find("daemon")!.Status);
    }

    [Fact]
    public async Task Guest_user_sets_password_on_shared_account_without_exec()
    {
        var request = Request(Out, "alice", "bob") with { GuestUser = "student" };

        Assert.Equal(0, (await DeployAsync(_backend, request)).Code);

        Assert.Empty(_backend.ExecCommands);
        Assert.All(_backend.PasswordSets, p => Assert.Equal("student", p.Account));
        Assert.Contains("username:s:student\r\n", File.ReadAllText(Path.Combine(Out, "bob.rdp")), StringComparison.Ordinal);
        Assert.Equal("student", DeploymentOutputs.ReadPasswords(Out)["bob"].GuestAccount);
        // The token still routes as the lab user.
        Assert.Contains("loadbalanceinfo:s:hotlab:v1:bob:", File.ReadAllText(Path.Combine(Out, "bob.rdp")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_guest_without_guest_user_fails_clearly()
    {
        _backend.OsId = "mswindows";
        var result = await DeployAsync(_backend, Request(Out, "alice"));

        Assert.Equal(1, result.Code);
        Assert.Contains("Windows", result.Err, StringComparison.Ordinal);
        Assert.Contains("--guest-user", result.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.ExecCommands);
        Assert.DoesNotContain(_backend.Vms.Values, v => v.Name == "hotlab-lab-alice"); // partial clone removed
    }

    [Fact]
    public async Task Windows_guest_with_guest_user_works()
    {
        _backend.OsId = "mswindows";
        var result = await DeployAsync(_backend, Request(Out, "alice") with { GuestUser = "student" });

        Assert.Equal(0, result.Code);
        Assert.Single(_backend.PasswordSets);
    }

    [Fact]
    public async Task Failed_clean_shutdown_falls_back_to_stop()
    {
        _backend.ShutdownExit = "VM quit/powerdown failed";
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice"))).Code);

        var vm = _backend.Clone("alice");
        Assert.Contains($"shutdown {vm.VmId}", _backend.Calls);
        Assert.Contains($"stop {vm.VmId}", _backend.Calls);
        Assert.Equal("stopped", vm.Status);
    }

    [Fact]
    public async Task Leave_running_skips_shutdown()
    {
        Assert.Equal(0, (await DeployAsync(_backend, Request(Out, "alice") with { LeaveRunning = true })).Code);

        Assert.Equal("running", _backend.Clone("alice").Status);
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_signing_key_fails_before_touching_proxmox()
    {
        var result = await DeployAsync(_backend, Request(Out, "alice"), key: "");

        Assert.Equal(1, result.Code);
        Assert.Contains("SigningKey", result.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }
}
