namespace Hotlab.Gateway.Tokens;

/// <summary>
/// Renders the .rdp files Hotlab hands out: the gateway address, a prefilled login name and
/// the signed routing token in <c>loadbalanceinfo</c>. Shared by <c>mint-rdp</c> and <c>deploy</c>.
/// </summary>
/// <remarks>
/// Per MS-RDPBCGR 3.2.5.3.1, mstsc sends either the routing token or the <c>mstshash</c>
/// cookie, never both; with a token present, <c>username</c> only prefills the login prompt.
/// Passwords are deliberately absent: mstsc's <c>password 51:b:</c> is DPAPI-encrypted per
/// machine and user, so it cannot be written for someone else.
/// </remarks>
public static class RdpFileRenderer
{
    /// <summary>Renders the file body (CRLF line endings, as mstsc writes them).</summary>
    /// <param name="address">Gateway <c>host[:port]</c>.</param>
    /// <param name="username">Login name to prefill.</param>
    /// <param name="routingToken">Signed routing token.</param>
    /// <exception cref="ArgumentException">A value contains a line break.</exception>
    public static string Render(string address, string username, string routingToken)
    {
        RejectLineBreaks(address, nameof(address));
        RejectLineBreaks(username, nameof(username));
        RejectLineBreaks(routingToken, nameof(routingToken));
        return $"full address:s:{address}\r\n" +
            $"username:s:{username}\r\n" +
            $"loadbalanceinfo:s:{routingToken}\r\n";
    }

    private static void RejectLineBreaks(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("Value must not contain line breaks.", name);
    }
}
