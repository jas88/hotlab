using Microsoft.Extensions.Configuration;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// <c>Hotlab.Gateway teardown --out &lt;dir&gt; [--keep-mappings]</c>: destroys a deployment's VMs
/// (only those in its pool and tagged hotlab), its pool, its gateway mappings and passwords.
/// </summary>
public static class TeardownCommand
{
    /// <summary>The verb that selects this command as the first program argument.</summary>
    public const string Name = "teardown";

    private const string Usage =
        "Usage: Hotlab.Gateway teardown --out <dir> [--keep-mappings]\n" +
        "  Destroys the VMs recorded in <dir>/deployment.json (only those still in the deployment's pool\n" +
        "  and tagged hotlab), removes the pool once empty, removes the users' mappings from the gateway\n" +
        "  users file (unless --keep-mappings) and deletes their passwords from passwords.csv.";

    /// <summary>Runs the command. Returns the process exit code (0 success, 1 failure/config error, 2 usage error).</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, IConfiguration configuration, TextWriter stdout, TextWriter stderr,
        Func<IConfiguration, IDeployBackend>? backendFactory = null, TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string? outDir = null;
        bool keepMappings = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Count && outDir is null:
                    outDir = args[++i];
                    break;
                case "--keep-mappings":
                    keepMappings = true;
                    break;
                case "-h" or "--help":
                    stderr.WriteLine(Usage);
                    return 2;
                default:
                    stderr.WriteLine($"Unexpected argument '{args[i]}'.");
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }
        if (string.IsNullOrWhiteSpace(outDir))
        {
            stderr.WriteLine("--out is required.");
            stderr.WriteLine(Usage);
            return 2;
        }

        IDeployBackend backend;
        try
        {
            backend = (backendFactory ?? DeployCommand.CreateBackend)(configuration);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }

        try
        {
            return await new Teardown(backend, stdout, stderr, timeProvider).RunAsync(outDir, keepMappings, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            (backend as IDisposable)?.Dispose();
        }
    }
}
