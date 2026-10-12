using Hotlab.Gateway.Deploy;
using Hotlab.Gateway.Tests.Tokens;
using static Hotlab.Gateway.Tests.Tokens.TokenTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>A unique temporary directory, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "hotlab-tests", Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed record RunResult(int Code, string Out, string Err)
{
    public string All => Out + Err;
}

internal static class DeployTestSupport
{
    public static readonly DeployTimings FastTimings = new()
    {
        AgentTimeout = TimeSpan.FromMilliseconds(300), // only expires for AgentDownFor VMs (the fake pings instantly)
        CommandTimeout = TimeSpan.FromMinutes(1), // never expected to expire: generous for slow CI
        ShutdownTimeout = TimeSpan.FromMinutes(1),
        PollInterval = TimeSpan.FromMilliseconds(1),
    };

    public static DeployRequest Request(string outDir, params string[] users) => new()
    {
        SourceVmId = FakeDeployBackend.SourceId,
        Node = FakeDeployBackend.Node,
        Address = "lab.example.org:3389",
        OutDir = outDir,
        Users = users,
        Name = "lab",
    };

    public static async Task<RunResult> DeployAsync(
        FakeDeployBackend backend, DeployRequest request, ManualTimeProvider? time = null, string? key = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        time ??= new ManualTimeProvider(Epoch);
        var deployer = new Deployer(backend, Create(time, key: key), FastTimings, stdout, stderr, time);
        int code = await deployer.RunAsync(request, CancellationToken.None);
        return new RunResult(code, stdout.ToString(), stderr.ToString());
    }

    public static async Task<RunResult> TeardownAsync(FakeDeployBackend backend, string outDir, bool keepMappings = false)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var teardown = new Teardown(backend, stdout, stderr, new ManualTimeProvider(Epoch));
        int code = await teardown.RunAsync(outDir, keepMappings, CancellationToken.None);
        return new RunResult(code, stdout.ToString(), stderr.ToString());
    }

    public static DeploymentManifest Manifest(string outDir) => DeploymentManifest.Load(outDir)!;

    public static UnixFileMode Mode(string path) =>
        OperatingSystem.IsWindows() ? UnixFileMode.None : File.GetUnixFileMode(path);
}
