using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// <c>Hotlab.Gateway deploy ...</c>: one clone of a source VM per user (see <see cref="DeployArgs.Usage"/>).
/// Uses the gateway's normal configuration (appsettings + environment) for the Proxmox
/// connection, the token signing key and <c>Hotlab:Deploy</c> timings.
/// </summary>
public static class DeployCommand
{
    /// <summary>The verb that selects this command as the first program argument.</summary>
    public const string Name = "deploy";

    /// <summary>Runs the command. Returns the process exit code (0 success, 1 failure/config error, 2 usage error).</summary>
    /// <param name="args">Arguments after the verb.</param>
    /// <param name="configuration">Gateway configuration.</param>
    /// <param name="stdout">Progress and summary.</param>
    /// <param name="stderr">Diagnostics.</param>
    /// <param name="backendFactory">Creates the Proxmox backend; null to build one from <c>Hotlab:Proxmox</c>.</param>
    /// <param name="timeProvider">Clock; null for the system clock.</param>
    /// <param name="ct">Cancellation (Ctrl+C).</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, IConfiguration configuration, TextWriter stdout, TextWriter stderr,
        Func<IConfiguration, IDeployBackend>? backendFactory = null, TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var request = DeployArgs.Parse(args, out var error);
        if (request is null)
        {
            if (error.Length > 0)
                stderr.WriteLine(error);
            stderr.WriteLine(DeployArgs.Usage);
            return 2;
        }

        var tokenOptions = new RoutingTokenOptions();
        configuration.GetSection(RoutingTokenOptions.SectionName).Bind(tokenOptions);
        var timings = new DeployTimings();
        configuration.GetSection(DeployTimings.SectionName).Bind(timings);

        RoutingTokenService tokens;
        IDeployBackend backend;
        try
        {
            tokens = new RoutingTokenService(Options.Create(tokenOptions), timeProvider);
            backend = (backendFactory ?? CreateBackend)(configuration);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }

        try
        {
            var deployer = new Deployer(backend, tokens, timings, stdout, stderr, timeProvider);
            return await deployer.RunAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            (backend as IDisposable)?.Dispose();
        }
    }

    /// <summary>Builds the real backend from <c>Hotlab:Proxmox</c>.</summary>
    /// <exception cref="InvalidOperationException">The connection settings are incomplete.</exception>
    public static IDeployBackend CreateBackend(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ProxmoxConnectionOptions();
        configuration.GetSection(ProxmoxConnectionOptions.SectionName).Bind(options);
        return new ProxmoxDeployBackend(options.CreateClient());
    }
}
