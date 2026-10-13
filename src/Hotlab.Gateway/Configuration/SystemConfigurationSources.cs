using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.KeyPerFile;

namespace Hotlab.Gateway.Configuration;

/// <summary>
/// Configuration sources for an installed gateway: a system configuration directory
/// (<c>/etc/hotlab/appsettings.json</c>) and systemd credentials (<c>$CREDENTIALS_DIRECTORY</c>).
/// Both are inserted after the binary-directory <c>appsettings*.json</c> files and before the
/// environment variables and command line, so the effective order (later wins) is:
/// binary appsettings, config directory, credentials, environment, command line.
/// When neither is present (development, tests) the configuration is unchanged.
/// </summary>
public static class SystemConfigurationSources
{
    /// <summary>Environment variable naming the configuration directory.</summary>
    public const string ConfigDirectoryVariable = "HOTLAB_CONFIG_DIR";

    /// <summary>Used when <see cref="ConfigDirectoryVariable"/> is unset and this directory exists.</summary>
    public const string DefaultConfigDirectory = "/etc/hotlab";

    /// <summary>The settings file read from the configuration directory.</summary>
    public const string SettingsFileName = "appsettings.json";

    /// <summary>Set by systemd when the unit loads credentials (LoadCredential=, LoadCredentialEncrypted=).</summary>
    public const string CredentialsDirectoryVariable = "CREDENTIALS_DIRECTORY";

    /// <summary>
    /// Credential (file) names use this in place of <c>:</c>, matching the environment
    /// variable convention: <c>Hotlab__Proxmox__TokenSecret</c> becomes <c>Hotlab:Proxmox:TokenSecret</c>.
    /// </summary>
    public const string CredentialSectionDelimiter = "__";

    /// <summary>
    /// The configuration directory: <see cref="ConfigDirectoryVariable"/> if set, else
    /// <paramref name="defaultDirectory"/> if it exists, else null. An explicitly set directory
    /// must exist and be readable: a typo there would otherwise silently drop every setting.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ConfigDirectoryVariable"/> names a directory that does not exist or cannot be read.
    /// </exception>
    public static string? ResolveConfigDirectory(
        Func<string, string?>? getEnvironment = null, string defaultDirectory = DefaultConfigDirectory)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        var dir = getEnvironment(ConfigDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var full = Path.GetFullPath(dir.Trim());
            EnsureReadableDirectory(full);
            return full;
        }
        return Directory.Exists(defaultDirectory) ? Path.GetFullPath(defaultDirectory) : null;
    }

    private static void EnsureReadableDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"{ConfigDirectoryVariable} is set to '{path}', but that directory does not exist. " +
                $"Create it, or unset {ConfigDirectoryVariable} to use {DefaultConfigDirectory} when present.");
        }
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"{ConfigDirectoryVariable} is set to '{path}', but that directory cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The systemd credentials directory, or null when not running with credentials.</summary>
    public static string? ResolveCredentialsDirectory(Func<string, string?>? getEnvironment = null)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        var dir = getEnvironment(CredentialsDirectoryVariable);
        return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir.Trim());
    }

    /// <summary>
    /// Inserts the configuration-directory settings file (optional, reloaded on change) and the
    /// credentials directory (optional) before the first unprefixed environment-variable
    /// source, or appends them when there is none. Returns what was added.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ConfigDirectoryVariable"/> names a directory that does not exist or cannot be read.
    /// </exception>
    public static (string? ConfigDirectory, string? CredentialsDirectory) Add(
        IConfigurationBuilder builder,
        Func<string, string?>? getEnvironment = null,
        string defaultDirectory = DefaultConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configDir = ResolveConfigDirectory(getEnvironment, defaultDirectory);
        var credentialsDir = ResolveCredentialsDirectory(getEnvironment);

        var added = new List<IConfigurationSource>();
        if (configDir is not null)
        {
            var json = new JsonConfigurationSource
            {
                Path = Path.Combine(configDir, SettingsFileName),
                Optional = true,
                ReloadOnChange = true,
            };
            json.ResolveFileProvider();
            added.Add(json);
        }
        if (credentialsDir is not null)
        {
            var credentials = new KeyPerFileConfigurationSource { Optional = true, SectionDelimiter = CredentialSectionDelimiter };
            // Credentials are immutable for the life of the service; no change tracking needed.
            credentials.FileProvider = Directory.Exists(credentialsDir)
                ? new Microsoft.Extensions.FileProviders.PhysicalFileProvider(credentialsDir)
                : null;
            added.Add(credentials);
        }

        var index = FindEnvironmentSource(builder.Sources);
        foreach (var source in added)
        {
            if (index < 0)
                builder.Sources.Add(source);
            else
                builder.Sources.Insert(index++, source);
        }
        return (configDir, credentialsDir);
    }

    private static int FindEnvironmentSource(IList<IConfigurationSource> sources)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
                return i;
        }
        return -1;
    }
}
