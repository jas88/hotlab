namespace Hotlab.Gateway.Routing;

/// <summary>Maps a normalized username to the VM their RDP session should reach.</summary>
public interface IUserTargetResolver
{
    /// <summary>Returns the user's target, or null if the user has no VM.</summary>
    VmTarget? Resolve(string username);

    /// <summary>
    /// Resolves a raw <c>mstshash</c> cookie value. The default implementation only does an
    /// exact match on <see cref="Protocol.X224ConnectionRequest.NormalizeUser"/>; resolvers
    /// that can enumerate users also accept a unique prefix for mstsc-truncated cookies
    /// (see <see cref="CookieResolution.MstscTruncationLength"/>).
    /// </summary>
    CookieResolution ResolveCookie(string? rawCookie)
    {
        var user = Protocol.X224ConnectionRequest.NormalizeUser(rawCookie);
        if (user is null)
            return new CookieResolution(CookieMatch.NoUser, null, null);
        var target = Resolve(user);
        return target is null
            ? new CookieResolution(CookieMatch.NotFound, user, null)
            : new CookieResolution(CookieMatch.Exact, user, target);
    }
}
