using System.Text.Json.Nodes;
using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Relay;
using Hotlab.Gateway.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Configuration;

public sealed class OptionsValidationTests
{
    [Fact]
    public void GatewayOptions_Defaults_AreValid_WithFiniteConnectionCap()
    {
        var options = new GatewayOptions();
        Assert.Null(options.Validate());
        Assert.Equal(GatewayOptions.DefaultMaxConnections, options.MaxConnections);
        Assert.True(options.MaxConnections > 0);
        Assert.Null(new GatewayOptions { MaxConnections = 0 }.Validate()); // explicit opt-in to unlimited
    }

    [Fact]
    public void ShippedAppSettings_HaveFiniteCap_AndNoSampleUsers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var hotlab = JsonNode.Parse(File.ReadAllText(path))!["Hotlab"]!;

        Assert.Equal(GatewayOptions.DefaultMaxConnections, (int)hotlab["Gateway"]!["MaxConnections"]!);
        Assert.Empty(hotlab["Users"]!.AsObject());
    }

    [Theory]
    [InlineData("Listen", "not-an-endpoint")]
    [InlineData("Listen", "")]
    [InlineData("MaxFirstPduBytes", "6")]
    [InlineData("MaxFirstPduBytes", "65536")]
    [InlineData("MaxConnections", "-1")]
    [InlineData("FirstPduTimeout", "00:00:00")]
    [InlineData("BackendConnectTimeout", "-00:00:01")]
    [InlineData("HalfCloseTimeout", "00:00:00")]
    public void GatewayOptions_Invalid_AreRejected(string key, string value)
    {
        var options = Bind<GatewayOptions>(GatewayOptions.SectionName, key, value);
        Assert.Contains(key, options.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayOptions_MinimumFirstPdu_IsAccepted() =>
        Assert.Null(new GatewayOptions { MaxFirstPduBytes = Gateway.Protocol.TpktReader.MinimumLength }.Validate());

    [Theory]
    [InlineData("http://pve.example:8006", "https")]
    [InlineData("pve.example:8006", "https")]
    [InlineData("not a url", "absolute")]
    public void Proxmox_RequiresAbsoluteHttpsUrl(string url, string expected)
    {
        var options = ValidProxmox();
        options.Url = url;

        Assert.Contains(expected, options.Validate(), StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidOperationException>(() => options.CreateClient());
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Proxmox_InsecureAndFingerprint_AreMutuallyExclusive()
    {
        var options = ValidProxmox();
        options.Insecure = true;
        options.Fingerprint = "AA:BB";

        Assert.Contains("mutually exclusive", options.Validate(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => options.CreateClient());
    }

    [Fact]
    public void Proxmox_Valid_CreatesClient()
    {
        var options = ValidProxmox();
        Assert.Null(options.Validate());
        using var client = options.CreateClient();
        options.Insecure = true;
        Assert.Null(options.Validate());
    }

    [Fact]
    public async Task Host_FailsAtStartup_OnInvalidSettings()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hotlab:Gateway:MaxFirstPduBytes"] = "3",
            ["Hotlab:Tokens:ClockSkew"] = "-00:00:01",
            ["Hotlab:Activation:PollInterval"] = "00:00:00",
            ["Hotlab:Proxmox:Url"] = "http://pve.example:8006",
        });
        var config = builder.Configuration;
        builder.Services.AddValidatedOptions<GatewayOptions>(config.GetSection(GatewayOptions.SectionName), o => o.Validate());
        builder.Services.AddValidatedOptions<RoutingTokenOptions>(config.GetSection(RoutingTokenOptions.SectionName), o => o.Validate());
        builder.Services.AddValidatedOptions<VmActivatorOptions>(config.GetSection(VmActivatorOptions.SectionName), o => o.Validate());
        builder.Services.AddValidatedOptions<ProxmoxConnectionOptions>(config.GetSection(ProxmoxConnectionOptions.SectionName), o => o.Validate());
        using var host = builder.Build();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        var messages = string.Join(" ", Flatten(ex).Select(e => e.Message));
        Assert.Contains("MaxFirstPduBytes", messages, StringComparison.Ordinal);
        Assert.Contains("ClockSkew", messages, StringComparison.Ordinal);
        Assert.Contains("PollInterval", messages, StringComparison.Ordinal);
        Assert.Contains("https", messages, StringComparison.Ordinal);
        Assert.Contains(Flatten(ex), e => e is OptionsValidationException);
    }

    [Fact]
    public async Task Host_StartsWithValidSettings()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddValidatedOptions<GatewayOptions>(
            builder.Configuration.GetSection(GatewayOptions.SectionName), o => o.Validate());
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
        Assert.Equal(GatewayOptions.DefaultMaxConnections,
            host.Services.GetRequiredService<IOptions<GatewayOptions>>().Value.MaxConnections);
    }

    private static ProxmoxConnectionOptions ValidProxmox() => new()
    {
        Url = "https://pve.example.invalid:8006",
        TokenId = "hotlab@pve!broker",
        TokenSecret = "secret",
    };

    private static T Bind<T>(string section, string key, string value) where T : new()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{section}:{key}"] = value }).Build();
        var options = new T();
        config.GetSection(section).Bind(options);
        return options;
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        yield return ex;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions.SelectMany(Flatten))
                yield return inner;
        }
        else if (ex.InnerException is not null)
        {
            foreach (var inner in Flatten(ex.InnerException))
                yield return inner;
        }
    }
}
