using Hotlab.Gateway.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Routing;

public sealed class ConfigUserTargetResolverTests
{
    private static (ConfigUserTargetResolver Resolver, IConfigurationRoot Config, ServiceProvider Sp) Create(
        Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<UserMapOptions>(config.GetSection(UserMapOptions.SectionName));
        var sp = services.BuildServiceProvider();
        var resolver = new ConfigUserTargetResolver(
            sp.GetRequiredService<IOptionsMonitor<UserMapOptions>>(),
            NullLogger<ConfigUserTargetResolver>.Instance);
        return (resolver, config, sp);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    [InlineData("Alice")]
    public void Resolve_IsCaseInsensitive(string username)
    {
        var (resolver, _, sp) = Create(new()
        {
            ["Hotlab:Users:Alice:Node"] = "pve1",
            ["Hotlab:Users:Alice:VmId"] = "101",
        });
        using (sp)
        {
            Assert.Equal(new VmTarget("pve1", 101), resolver.Resolve(username));
        }
    }

    [Fact]
    public void Resolve_HonoursPortAndHost()
    {
        var (resolver, _, sp) = Create(new()
        {
            ["Hotlab:Users:bob:Node"] = "pve2",
            ["Hotlab:Users:bob:VmId"] = "202",
            ["Hotlab:Users:bob:Port"] = "3390",
            ["Hotlab:Users:bob:Host"] = "10.0.0.5",
        });
        using (sp)
        {
            Assert.Equal(new VmTarget("pve2", 202, 3390, "10.0.0.5"), resolver.Resolve("bob"));
        }
    }

    [Fact]
    public void Resolve_UnknownUser_ReturnsNull()
    {
        var (resolver, _, sp) = Create(new()
        {
            ["Hotlab:Users:alice:Node"] = "pve1",
            ["Hotlab:Users:alice:VmId"] = "101",
        });
        using (sp)
        {
            Assert.Null(resolver.Resolve("mallory"));
        }
    }

    [Theory]
    [InlineData(null, "101", null)]
    [InlineData("pve1", "0", null)]
    [InlineData("pve1", "101", "70000")]
    public void Resolve_InvalidMapping_ReturnsNull(string? node, string vmId, string? port)
    {
        var (resolver, _, sp) = Create(new()
        {
            ["Hotlab:Users:carol:Node"] = node,
            ["Hotlab:Users:carol:VmId"] = vmId,
            ["Hotlab:Users:carol:Port"] = port,
        });
        using (sp)
        {
            Assert.Null(resolver.Resolve("carol"));
        }
    }

    [Fact]
    public void Resolve_SeesConfigurationReload()
    {
        var (resolver, config, sp) = Create(new()
        {
            ["Hotlab:Users:alice:Node"] = "pve1",
            ["Hotlab:Users:alice:VmId"] = "101",
        });
        using (sp)
        {
            Assert.Null(resolver.Resolve("dave"));

            config["Hotlab:Users:dave:Node"] = "pve3";
            config["Hotlab:Users:dave:VmId"] = "303";
            config["Hotlab:Users:alice:VmId"] = "111";
            config.Reload();

            Assert.Equal(new VmTarget("pve3", 303), resolver.Resolve("DAVE"));
            Assert.Equal(new VmTarget("pve1", 111), resolver.Resolve("alice"));
        }
    }
}
