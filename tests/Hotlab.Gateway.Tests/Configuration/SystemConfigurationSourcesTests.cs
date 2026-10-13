using Hotlab.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.Hosting;

namespace Hotlab.Gateway.Tests.Configuration;

public sealed class SystemConfigurationSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hotlab-syscfg-" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir;
    private readonly string _credentialsDir;
    private readonly string _missingDir;

    public SystemConfigurationSourcesTests()
    {
        _configDir = Path.Combine(_root, "etc-hotlab");
        _credentialsDir = Path.Combine(_root, "credentials");
        _missingDir = Path.Combine(_root, "missing");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_credentialsDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Func<string, string?> Env(string? configDir = null, string? credentialsDir = null) => name => name switch
    {
        SystemConfigurationSources.ConfigDirectoryVariable => configDir,
        SystemConfigurationSources.CredentialsDirectoryVariable => credentialsDir,
        _ => null,
    };

    private static ConfigurationBuilder BaseBuilder(Dictionary<string, string?> binaryDefaults)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(binaryDefaults); // stands in for the binary-dir appsettings.json
        // The real (unprefixed) environment source: the insertion point. The keys asserted on
        // below are not expected in a test environment.
        builder.Add(new EnvironmentVariablesConfigurationSource());
        return builder;
    }

    [Fact]
    public void ConfigDirectory_DefaultUsedOnlyWhenItExists()
    {
        Assert.Equal(Path.GetFullPath(_configDir), SystemConfigurationSources.ResolveConfigDirectory(Env(), _configDir));
        Assert.Null(SystemConfigurationSources.ResolveConfigDirectory(Env(), _missingDir));
        Assert.Equal(Path.GetFullPath(_configDir),
            SystemConfigurationSources.ResolveConfigDirectory(Env(configDir: _configDir), _missingDir));
    }

    [Fact]
    public void ExplicitConfigDirectory_Missing_FailsNamingVariableAndPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SystemConfigurationSources.ResolveConfigDirectory(Env(configDir: _missingDir), _configDir));
        Assert.Contains(SystemConfigurationSources.ConfigDirectoryVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(_missingDir), ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);

        var builder = new ConfigurationBuilder();
        Assert.Throws<InvalidOperationException>(
            () => SystemConfigurationSources.Add(builder, Env(configDir: _missingDir), _configDir));
        Assert.Empty(builder.Sources);
    }

    [Fact]
    public void ExplicitConfigDirectory_ThatIsAFile_Fails()
    {
        var file = Path.Combine(_root, "not-a-dir");
        File.WriteAllText(file, "{}");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SystemConfigurationSources.ResolveConfigDirectory(Env(configDir: file), _missingDir));
        Assert.Contains(SystemConfigurationSources.ConfigDirectoryVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitConfigDirectory_Unreadable_FailsNamingVariableAndPath()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return; // permission bits are not enforced there

        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SystemConfigurationSources.ResolveConfigDirectory(Env(configDir: locked), _missingDir));
            Assert.Contains(SystemConfigurationSources.ConfigDirectoryVariable, ex.Message, StringComparison.Ordinal);
            Assert.Contains(locked, ex.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ExplicitConfigDirectory_WithoutSettingsFile_IsAccepted()
    {
        var builder = new ConfigurationBuilder();

        var (config, _) = SystemConfigurationSources.Add(builder, Env(configDir: _configDir), _missingDir);

        Assert.Equal(Path.GetFullPath(_configDir), config);
        using var root = (ConfigurationRoot)builder.Build();
        Assert.Empty(root.AsEnumerable());
    }

    [Fact]
    public void NothingAdded_WhenNoDirectoryAndNoCredentials()
    {
        var builder = BaseBuilder(new() { ["Hotlab:Proxmox:Url"] = "https://a" });
        var before = builder.Sources.ToList();

        var (config, credentials) = SystemConfigurationSources.Add(builder, Env(), _missingDir);

        Assert.Null(config);
        Assert.Null(credentials);
        Assert.Equal(before, builder.Sources);
    }

    [Fact]
    public void ConfigDirectoryAndCredentials_LayerBetweenDefaultsAndEnvironment()
    {
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), """
            { "Hotlab": { "Proxmox": { "Url": "https://pve.site:8006", "TokenId": "hotlab@pve!broker" } } }
            """);
        // systemd-creds output typically keeps the trailing newline of `echo secret | ...`.
        File.WriteAllText(Path.Combine(_credentialsDir, "Hotlab__Proxmox__TokenSecret"), "proxmox-secret\n");
        File.WriteAllText(Path.Combine(_credentialsDir, "Hotlab__Tokens__SigningKey"), "signing-key");
        File.WriteAllText(Path.Combine(_credentialsDir, "Hotlab__Proxmox__TokenId"), "hotlab@pve!fromcred");

        var builder = BaseBuilder(new()
        {
            ["Hotlab:Proxmox:Url"] = "https://default.invalid",
            ["Hotlab:Proxmox:TokenId"] = "default",
            ["Hotlab:Gateway:Listen"] = "0.0.0.0:3389",
        });
        var environment = builder.Sources[^1];
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Hotlab:Tokens:SigningKey"] = "from-env" });

        var (config, credentials) = SystemConfigurationSources.Add(
            builder, Env(configDir: _configDir, credentialsDir: _credentialsDir), _missingDir);

        Assert.Equal(Path.GetFullPath(_configDir), config);
        Assert.Equal(Path.GetFullPath(_credentialsDir), credentials);
        Assert.IsType<JsonConfigurationSource>(builder.Sources[1]);
        Assert.IsType<KeyPerFileConfigurationSource>(builder.Sources[2]);
        Assert.Same(environment, builder.Sources[3]);

        using var root = (ConfigurationRoot)builder.Build();
        Assert.Equal("https://pve.site:8006", root["Hotlab:Proxmox:Url"]);   // config dir beats defaults
        Assert.Equal("hotlab@pve!fromcred", root["Hotlab:Proxmox:TokenId"]); // credentials beat config dir
        Assert.Equal("proxmox-secret", root["Hotlab:Proxmox:TokenSecret"]);  // "__" -> ":", newline trimmed
        Assert.Equal("from-env", root["Hotlab:Tokens:SigningKey"]);          // later sources still win
        Assert.Equal("0.0.0.0:3389", root["Hotlab:Gateway:Listen"]);         // untouched defaults remain
    }

    [Fact]
    public void Credentials_MapDoubleUnderscoreToSections()
    {
        File.WriteAllText(Path.Combine(_credentialsDir, "Hotlab__Tokens__SigningKey"), "k");
        var builder = new ConfigurationBuilder();

        SystemConfigurationSources.Add(builder, Env(credentialsDir: _credentialsDir), _missingDir);

        using var root = (ConfigurationRoot)builder.Build();
        Assert.Equal("k", root["Hotlab:Tokens:SigningKey"]);
        Assert.Equal("k", root.GetSection("Hotlab").GetSection("Tokens")["SigningKey"]);
    }

    [Fact]
    public void MissingDefaultDirectoryAndCredentials_AreOptional()
    {
        var builder = new ConfigurationBuilder();

        var (config, credentials) = SystemConfigurationSources.Add(
            builder, Env(credentialsDir: Path.Combine(_root, "no-creds")), _missingDir);

        Assert.Null(config);
        Assert.NotNull(credentials);
        Assert.Single(builder.Sources);
        using var root = (ConfigurationRoot)builder.Build();
        Assert.Empty(root.AsEnumerable());
    }

    [Fact]
    public void HostBuilder_SourcesGoAfterAppSettingsAndBeforeEnvironment()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });

        SystemConfigurationSources.Add(builder.Configuration, Env(configDir: _configDir, credentialsDir: _credentialsDir), _missingDir);

        var sources = ((IConfigurationBuilder)builder.Configuration).Sources;
        int Index(Func<IConfigurationSource, bool> match) => sources.Select((src, i) => (src, i)).First(t => match(t.src)).i;
        int appSettings = Index(src => src is JsonConfigurationSource { Path: "appsettings.json" });
        int configDir = Index(src => src is JsonConfigurationSource j && j.Path == "appsettings.json" && j != sources[appSettings]);
        int credentials = Index(src => src is KeyPerFileConfigurationSource);
        int environment = Index(src => src is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        Assert.True(appSettings < configDir && configDir < credentials && credentials < environment,
            $"order: appsettings {appSettings}, config dir {configDir}, credentials {credentials}, environment {environment}");
    }
}
