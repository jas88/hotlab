using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Deploy;
using Microsoft.Extensions.Configuration;
using static Hotlab.Gateway.Tests.Tokens.TokenTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>CLI entry points and the gateway's users-file configuration source.</summary>
public sealed class DeployCommandTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDeployBackend _backend = new();

    public void Dispose() => _temp.Dispose();

    private static IConfiguration Config(string? key = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hotlab:Tokens:SigningKey"] = key ?? Key,
            ["Hotlab:Deploy:PollInterval"] = "00:00:00",
            ["Hotlab:Deploy:AgentTimeout"] = "00:00:05",
        }).Build();

    private async Task<RunResult> RunAsync(string verb, IConfiguration config, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = verb == DeployCommand.Name
            ? await DeployCommand.RunAsync(args, config, stdout, stderr, _ => _backend)
            : await TeardownCommand.RunAsync(args, config, stdout, stderr, _ => _backend);
        return new RunResult(code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Deploy_then_teardown_end_to_end()
    {
        var outDir = _temp.Combine("lab1");
        var deploy = await RunAsync(DeployCommand.Name, Config(),
            "--source", "9000", "--node", "pve1", "--address", "gw.example", "--out", outDir, "alice", "bob");

        Assert.Equal(0, deploy.Code);
        Assert.Equal("lab1", DeploymentManifest.Load(outDir)!.Name); // derived from --out
        Assert.True(File.Exists(Path.Combine(outDir, "bob.rdp")));

        var teardown = await RunAsync(TeardownCommand.Name, Config(), "--out", outDir);
        Assert.Equal(0, teardown.Code);
        Assert.Single(_backend.Vms); // only the source is left
    }

    [Fact]
    public async Task Deploy_usage_error_returns_two_with_usage()
    {
        var result = await RunAsync(DeployCommand.Name, Config(), "--help");

        Assert.Equal(2, result.Code);
        Assert.Contains("Usage: Hotlab.Gateway deploy", result.Err, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task Deploy_bad_signing_key_is_config_error()
    {
        var result = await RunAsync(DeployCommand.Name, Config("not base64!"),
            "--source", "9000", "--node", "pve1", "--address", "gw", "--out", _temp.Combine("o"), "alice");

        Assert.Equal(1, result.Code);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task Default_backend_requires_proxmox_settings()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = await TeardownCommand.RunAsync(["--out", _temp.Path], Config(), stdout, stderr);

        Assert.Equal(1, code);
        Assert.Contains("Hotlab:Proxmox:Url", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("--out")]
    [InlineData("--out", "a", "--bogus")]
    [InlineData("--help")]
    public async Task Teardown_usage_errors_return_two(params string[] args)
    {
        var result = await RunAsync(TeardownCommand.Name, Config(), args);

        Assert.Equal(2, result.Code);
        Assert.Contains("Usage: Hotlab.Gateway teardown", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void UsersFile_path_comes_from_config_then_environment()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Hotlab:UsersFile"] = "/etc/hotlab/users.json" }).Build();
        var empty = new ConfigurationBuilder().Build();

        Assert.Equal(Path.GetFullPath("/etc/hotlab/users.json"), UsersFileConfiguration.ResolvePath(config, _ => "/other"));
        Assert.Equal(Path.GetFullPath("/env/users.json"), UsersFileConfiguration.ResolvePath(empty, _ => "/env/users.json"));
        Assert.Null(UsersFileConfiguration.ResolvePath(empty, _ => null));
    }

    [Fact]
    public async Task UsersFile_source_reloads_after_atomic_merge()
    {
        var path = _temp.Combine("users.json");
        var builder = new ConfigurationBuilder();
        var seed = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Hotlab:UsersFile"] = path }).Build();
        Assert.Equal(path, UsersFileConfiguration.Add(builder, seed));
        var config = builder.Build(); // optional: the file does not exist yet

        Assert.Null(config["Hotlab:Users:alice:VmId"]);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = config.GetReloadToken().RegisterChangeCallback(_ => reloaded.TrySetResult(), null);

        UserMapFile.Merge(path, [new UserMapping("alice", "pve1", 200)]);

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // The reload may fire before the provider has re-read the file; poll briefly.
        for (int i = 0; i < 50 && config["Hotlab:Users:alice:VmId"] is null; i++)
            await Task.Delay(100);
        Assert.Equal("200", config["Hotlab:Users:alice:VmId"]);
    }
}
