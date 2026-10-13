using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Deploy;
using Hotlab.Gateway.Relay;
using Hotlab.Gateway.Routing;
using Hotlab.Gateway.Tokens;
using Hotlab.Proxmox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Configuration sources (later wins): appsettings.json, appsettings.{Environment}.json,
// $HOTLAB_CONFIG_DIR/appsettings.json (must exist if set; default /etc/hotlab, if present),
// systemd credentials in $CREDENTIALS_DIRECTORY (file name = key, "__" for ":"),
// environment variables (e.g. Hotlab__Proxmox__TokenSecret), command line.
// Production note: the Proxmox token secret and the routing-token signing key
// (Hotlab__Tokens__SigningKey) are meant to arrive via systemd credentials
// (LoadCredentialEncrypted) or the environment, not appsettings; see docs/architecture.md.
// Content root is the binary directory so appsettings.json is found regardless of the
// working directory (systemd, dotnet run).
if (args.Length > 0 && args[0] == MintRdpCommand.Name)
{
    // Same appsettings + environment configuration as the listener; the verb's own
    // arguments are not fed to the command-line configuration provider.
    var mintConfig = CreateBuilder([]).Configuration;
    return MintRdpCommand.Run(args[1..], mintConfig, Console.Out, Console.Error);
}

if (args.Length > 0 && args[0] is DeployCommand.Name or TeardownCommand.Name)
{
    var verbConfig = CreateBuilder([]).Configuration;
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // Let the command record its progress before exiting.
        cts.Cancel();
    };
    return args[0] == DeployCommand.Name
        ? await DeployCommand.RunAsync(args[1..], verbConfig, Console.Out, Console.Error, ct: cts.Token).ConfigureAwait(false)
        : await TeardownCommand.RunAsync(args[1..], verbConfig, Console.Out, Console.Error, ct: cts.Token).ConfigureAwait(false);
}

var builder = CreateBuilder(args);
var config = builder.Configuration;
// Deployment-maintained user map (deploy --users-file-target), reloaded on change.
var usersFile = UsersFileConfiguration.Add(config, config);

// Settings are validated when the host starts (ValidateOnStart), so a bad value stops the
// gateway with a clear message instead of failing on the first connection.
builder.Services.AddValidatedOptions<GatewayOptions>(config.GetSection(GatewayOptions.SectionName), o => o.Validate());
builder.Services.Configure<UserMapOptions>(config.GetSection(UserMapOptions.SectionName));
builder.Services.AddValidatedOptions<VmActivatorOptions>(config.GetSection(VmActivatorOptions.SectionName), o => o.Validate());
builder.Services.AddValidatedOptions<ProxmoxConnectionOptions>(config.GetSection(ProxmoxConnectionOptions.SectionName), o => o.Validate());
builder.Services.AddValidatedOptions<RoutingTokenOptions>(config.GetSection(RoutingTokenOptions.SectionName), o => o.Validate());

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ProxmoxConnectionOptions>>().Value;
    sp.GetRequiredService<ILogger<ProxmoxClient>>().LogInformation("Using {Proxmox}", options);
    return options.CreateClient();
});
builder.Services.AddSingleton<IVmControl>(sp => new ProxmoxVmControl(sp.GetRequiredService<ProxmoxClient>()));
builder.Services.AddSingleton<IPortProbe, TcpPortProbe>();
builder.Services.AddSingleton<IVmActivator, VmActivator>();
builder.Services.AddSingleton<IUserTargetResolver, ConfigUserTargetResolver>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<RoutingTokenOptions>>();
    sp.GetRequiredService<ILogger<RoutingTokenService>>().LogInformation("{Tokens}", options.Value);
    return new RoutingTokenService(options, sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<RdpConnectionHandler>();
builder.Services.AddHostedService<RdpListenerService>();

using var host = builder.Build();
if (usersFile is not null)
{
    host.Services.GetRequiredService<ILogger<UserMapOptions>>()
        .LogInformation("Loading user map from {UsersFile} (reload on change)", usersFile);
}
await host.RunAsync().ConfigureAwait(false);
return 0;

static HostApplicationBuilder CreateBuilder(string[] args)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    SystemConfigurationSources.Add(builder.Configuration);
    return builder;
}
