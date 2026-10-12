using Microsoft.Extensions.Configuration;

namespace Hotlab.Gateway.Configuration;

/// <summary>
/// Optional JSON file holding the <c>Hotlab:Users</c> map, maintained by
/// <c>deploy --users-file-target</c>. Loaded with reload-on-change so new deployments take
/// effect without restarting the gateway.
/// </summary>
public static class UsersFileConfiguration
{
    /// <summary>Configuration key naming the file.</summary>
    public const string ConfigKey = "Hotlab:UsersFile";

    /// <summary>Environment variable naming the file (used when <see cref="ConfigKey"/> is unset).</summary>
    public const string EnvironmentVariable = "HOTLAB_USERS_FILE";

    /// <summary>
    /// Returns the configured users file as a full path (relative paths resolve against the
    /// current directory), or null when none is configured.
    /// </summary>
    public static string? ResolvePath(IConfiguration configuration, Func<string, string?>? getEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        getEnvironment ??= Environment.GetEnvironmentVariable;
        var path = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(path))
            path = getEnvironment(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
    }

    /// <summary>
    /// Adds the users file (if configured) as an optional, reloading JSON source, after the
    /// existing sources so its <c>Hotlab:Users</c> entries win. Returns the path, or null.
    /// </summary>
    public static string? Add(IConfigurationBuilder builder, IConfiguration current, Func<string, string?>? getEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var path = ResolvePath(current, getEnvironment);
        if (path is null)
            return null;
        builder.AddJsonFile(path, optional: true, reloadOnChange: true);
        return path;
    }
}
