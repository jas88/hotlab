namespace Hotlab.Gateway.Routing;

/// <summary>How a raw <c>mstshash</c> cookie was (or was not) resolved to a target.</summary>
public enum CookieMatch
{
    /// <summary>No usable username in the cookie.</summary>
    NoUser,

    /// <summary>The normalized username matched a configured user exactly.</summary>
    Exact,

    /// <summary>The cookie looked truncated and its prefix matched exactly one configured user.</summary>
    UniquePrefix,

    /// <summary>The cookie looked truncated and its prefix matched several configured users.</summary>
    AmbiguousPrefix,

    /// <summary>No configured user matched.</summary>
    NotFound,
}

/// <summary>Result of <see cref="IUserTargetResolver.ResolveCookie"/>.</summary>
/// <param name="Match">How the cookie was matched.</param>
/// <param name="User">
/// The resolved configured username for <see cref="CookieMatch.Exact"/> and
/// <see cref="CookieMatch.UniquePrefix"/>; otherwise the normalized cookie value, if any.
/// </param>
/// <param name="Target">The target VM, non-null only for a successful match.</param>
public sealed record CookieResolution(CookieMatch Match, string? User, VmTarget? Target)
{
    /// <summary>
    /// Length at which Windows mstsc truncates the mstshash identifier (DOMAIN\ included).
    /// A cookie of exactly this length may be a prefix of the real username.
    /// </summary>
    public const int MstscTruncationLength = 9;
}
