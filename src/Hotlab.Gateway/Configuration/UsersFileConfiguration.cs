using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Primitives;

namespace Hotlab.Gateway.Configuration;

/// <summary>
/// Optional JSON file holding the <c>Hotlab:Users</c> map, maintained by
/// <c>deploy --users-file-target</c>. Loaded with reload-on-change so new deployments take
/// effect without restarting the gateway. Only keys under <see cref="UsersSection"/> are
/// taken from the file: anything else in it (Proxmox, token key, listener settings) is
/// ignored, so a writable user map cannot reconfigure the gateway.
/// </summary>
public static class UsersFileConfiguration
{
    /// <summary>Configuration key naming the file.</summary>
    public const string ConfigKey = "Hotlab:UsersFile";

    /// <summary>The only configuration section the users file may supply.</summary>
    public const string UsersSection = "Hotlab:Users";

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
    /// existing sources so its <c>Hotlab:Users</c> entries win. Keys outside
    /// <see cref="UsersSection"/> are filtered out. Returns the path, or null.
    /// </summary>
    public static string? Add(IConfigurationBuilder builder, IConfiguration current, Func<string, string?>? getEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var path = ResolvePath(current, getEnvironment);
        if (path is null)
            return null;
        var json = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = true };
        json.ResolveFileProvider();
        builder.Add(new SectionFilterSource(json, UsersSection));
        return path;
    }

    /// <summary>Exposes only the keys of an inner source that lie under one section.</summary>
    private sealed class SectionFilterSource(IConfigurationSource inner, string section) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new SectionFilterProvider(inner.Build(builder), section);
    }

    private sealed class SectionFilterProvider(IConfigurationProvider inner, string section)
        : IConfigurationProvider, IDisposable
    {
        private static readonly StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

        public bool TryGet(string key, out string? value)
        {
            value = null;
            return IsInside(key) && inner.TryGet(key, out value);
        }

        public void Set(string key, string? value)
        {
            if (IsInside(key))
                inner.Set(key, value);
        }

        public IChangeToken GetReloadToken() => inner.GetReloadToken();

        public void Load() => inner.Load();

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var own = inner.GetChildKeys([], parentPath)
                .Where(child => IsInsideOrAncestor(parentPath is null ? child : parentPath + ConfigurationPath.KeyDelimiter + child));
            return earlierKeys.Concat(own).OrderBy(k => k, ConfigurationKeyComparer.Instance).ToList();
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();

        /// <summary>The section itself or anything below it.</summary>
        private bool IsInside(string key) =>
            key.Equals(section, Comparison) ||
            (key.Length > section.Length && key.StartsWith(section, Comparison) &&
             key[section.Length] == ':');

        /// <summary>Inside the section, or a path leading to it (e.g. <c>Hotlab</c>).</summary>
        private bool IsInsideOrAncestor(string key) =>
            IsInside(key) ||
            (section.Length > key.Length && section.StartsWith(key, Comparison) && section[key.Length] == ':');
    }
}
