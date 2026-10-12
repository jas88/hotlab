using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Routing;
using Microsoft.Extensions.Configuration;

namespace Hotlab.Gateway.Tests.Configuration;

public sealed class UsersFileConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hotlab-users-" + Guid.NewGuid().ToString("N"));

    public UsersFileConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void UsersFile_CanOnlySupplyHotlabUsers()
    {
        var path = Path.Combine(_dir, "users.json");
        File.WriteAllText(path, """
            {
              "Hotlab": {
                "Users": { "alice": { "Node": "pve1", "VmId": 101 } },
                "Proxmox": { "Url": "https://evil.example", "Insecure": true },
                "Tokens": { "SigningKey": "attacker" },
                "Gateway": { "MaxConnections": 0 },
                "UsersFile": "/elsewhere.json"
              },
              "Logging": { "LogLevel": { "Default": "None" } },
              "HotlabUsers": { "x": "1" }
            }
            """);

        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hotlab:UsersFile"] = path,
            ["Hotlab:Proxmox:Url"] = "https://pve.example:8006",
            ["Hotlab:Users:bob:Node"] = "pve2",
            ["Hotlab:Users:bob:VmId"] = "202",
        });
        var seed = builder.Build();
        Assert.Equal(path, UsersFileConfiguration.Add(builder, seed));
        using var config = (ConfigurationRoot)builder.Build();

        Assert.Equal("101", config["Hotlab:Users:alice:VmId"]);
        Assert.Equal("202", config["Hotlab:Users:bob:VmId"]);
        Assert.Equal("https://pve.example:8006", config["Hotlab:Proxmox:Url"]);
        Assert.Null(config["Hotlab:Proxmox:Insecure"]);
        Assert.Null(config["Hotlab:Tokens:SigningKey"]);
        Assert.Null(config["Hotlab:Gateway:MaxConnections"]);
        Assert.Equal(path, config["Hotlab:UsersFile"]);
        Assert.Null(config["Logging:LogLevel:Default"]);
        Assert.Null(config["HotlabUsers:x"]);

        Assert.Equal(["Proxmox", "Users", "UsersFile"],
            config.GetSection("Hotlab").GetChildren().Select(c => c.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["Hotlab"], config.GetChildren().Select(c => c.Key));

        var users = new UserMapOptions();
        config.GetSection(UserMapOptions.SectionName).Bind(users);
        Assert.Equal(["alice", "bob"], users.Users.Keys.Order(StringComparer.Ordinal));
    }
}
