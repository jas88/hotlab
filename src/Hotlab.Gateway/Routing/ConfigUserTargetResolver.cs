using System.Text;
using Hotlab.Gateway.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Routing;

/// <summary>
/// Resolves users from the <see cref="UserMapOptions"/> configuration. Reads through an
/// <see cref="IOptionsMonitor{TOptions}"/>, so edits to the configuration source take
/// effect without a restart. Lookups are case-insensitive.
/// </summary>
public sealed class ConfigUserTargetResolver : IUserTargetResolver
{
    private readonly IOptionsMonitor<UserMapOptions> _options;
    private readonly ILogger<ConfigUserTargetResolver> _logger;

    /// <summary>Creates a resolver over the monitored user map.</summary>
    public ConfigUserTargetResolver(
        IOptionsMonitor<UserMapOptions> options, ILogger<ConfigUserTargetResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public VmTarget? Resolve(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        // Usernames are ASCII; refusing anything else keeps case folding from aliasing
        // (e.g. U+212A KELVIN SIGN lowercases to 'k').
        if (!Ascii.IsValid(username))
            return null;
        var users = _options.CurrentValue.Users;

        if (!users.TryGetValue(username, out var entry))
        {
            // Defensive: the dictionary is case-insensitive when bound normally, but a
            // replaced instance (e.g. via PostConfigure) might not be.
            entry = users.FirstOrDefault(
                kv => string.Equals(kv.Key, username, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return entry is null ? null : ToTarget(username, entry);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exact match on the normalized username first. Failing that, if the raw cookie is
    /// exactly <see cref="CookieResolution.MstscTruncationLength"/> characters (mstsc's
    /// truncation, DOMAIN\ included), a configured username that starts with the normalized
    /// value is accepted if it is the only one; several candidates are ambiguous and rejected.
    /// </remarks>
    public CookieResolution ResolveCookie(string? rawCookie)
    {
        var user = X224ConnectionRequest.NormalizeUser(rawCookie);
        if (user is null)
            return new CookieResolution(CookieMatch.NoUser, null, null);

        var exact = Resolve(user);
        if (exact is not null)
            return new CookieResolution(CookieMatch.Exact, user, exact);

        if (rawCookie!.Trim().Length != CookieResolution.MstscTruncationLength)
            return new CookieResolution(CookieMatch.NotFound, user, null);

        var candidates = _options.CurrentValue.Users
            .Where(kv => Ascii.IsValid(kv.Key) && kv.Key.StartsWith(user, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        switch (candidates.Count)
        {
            case 0:
                return new CookieResolution(CookieMatch.NotFound, user, null);
            case > 1:
                _logger.LogWarning(
                    "Truncated cookie {Cookie} matches several configured users; refusing to guess",
                    rawCookie);
                return new CookieResolution(CookieMatch.AmbiguousPrefix, user, null);
        }

        var (name, entry) = candidates[0];
        var target = ToTarget(name, entry);
        return target is null
            ? new CookieResolution(CookieMatch.NotFound, user, null)
            : new CookieResolution(CookieMatch.UniquePrefix, name.ToLowerInvariant(), target);
    }

    private VmTarget? ToTarget(string username, UserTargetOptions entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Node) || entry.VmId <= 0 ||
            entry.Port is <= 0 or > 65535)
        {
            _logger.LogWarning(
                "Ignoring invalid mapping for user {User}: Node={Node} VmId={VmId} Port={Port}",
                username, entry.Node, entry.VmId, entry.Port);
            return null;
        }

        return new VmTarget(
            entry.Node,
            entry.VmId,
            entry.Port ?? 3389,
            string.IsNullOrWhiteSpace(entry.Host) ? null : entry.Host);
    }
}
