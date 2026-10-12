using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Relay;
using Hotlab.Gateway.Routing;
using Hotlab.Proxmox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Configuration sources (later wins): appsettings.json, appsettings.{Environment}.json,
// environment variables (e.g. Hotlab__Proxmox__TokenSecret), command line.
// Production note: the Proxmox token secret is meant to arrive via systemd credentials
// (LoadCredentialEncrypted), not appsettings; see docs/architecture.md.
// Content root is the binary directory so appsettings.json is found regardless of the
// working directory (systemd, dotnet run).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
var config = builder.Configuration;

builder.Services.Configure<GatewayOptions>(config.GetSection(GatewayOptions.SectionName));
builder.Services.Configure<UserMapOptions>(config.GetSection(UserMapOptions.SectionName));
builder.Services.Configure<VmActivatorOptions>(config.GetSection("Hotlab:Activation"));
builder.Services.Configure<ProxmoxConnectionOptions>(config.GetSection(ProxmoxConnectionOptions.SectionName));

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
builder.Services.AddSingleton<RdpConnectionHandler>();
builder.Services.AddHostedService<RdpListenerService>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;
