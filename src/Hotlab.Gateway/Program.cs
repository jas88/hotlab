using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Relay;
using Hotlab.Gateway.Routing;
using Hotlab.Gateway.Tokens;
using Hotlab.Proxmox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Configuration sources (later wins): appsettings.json, appsettings.{Environment}.json,
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

var builder = CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<GatewayOptions>(config.GetSection(GatewayOptions.SectionName));
builder.Services.Configure<UserMapOptions>(config.GetSection(UserMapOptions.SectionName));
builder.Services.Configure<VmActivatorOptions>(config.GetSection("Hotlab:Activation"));
builder.Services.Configure<ProxmoxConnectionOptions>(config.GetSection(ProxmoxConnectionOptions.SectionName));
builder.Services.Configure<RoutingTokenOptions>(config.GetSection(RoutingTokenOptions.SectionName));

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
await host.RunAsync().ConfigureAwait(false);
return 0;

static HostApplicationBuilder CreateBuilder(string[] args) =>
    Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
