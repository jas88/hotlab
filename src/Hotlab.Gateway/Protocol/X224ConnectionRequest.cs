using System.Buffers.Binary;
using System.Text;

namespace Hotlab.Gateway.Protocol;

/// <summary>
/// The client's first RDP PDU: a TPKT-framed X.224 Connection Request (MS-RDPBCGR
/// 2.2.1.1). Hotlab only parses it to extract the routing identity; the original bytes
/// are forwarded to the guest unchanged.
/// </summary>
public sealed record X224ConnectionRequest(
    string? Cookie,
    string? RoutingToken,
    uint? RequestedProtocols)
{
    private const int TpktHeaderLength = 4;
    private const int FixedTpduLength = 7; // LI, code, dst-ref(2), src-ref(2), class
    private const byte ConnectionRequestCode = 0xE0;
    private const byte NegotiationRequestType = 0x01;
    private const int NegotiationRequestLength = 8;
    private const byte CorrelationInfoPresent = 0x08;
    private const int CorrelationInfoLength = 36;

    private static ReadOnlySpan<byte> MstsHashPrefix => "Cookie: mstshash="u8;
    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    /// <summary>
    /// Parses a complete TPKT PDU (header included). Returns false for anything that is
    /// not a well-formed X.224 Connection Request.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> pdu, out X224ConnectionRequest? request)
    {
        request = null;

        // TPKT header (RFC 1006): version 3, reserved 0, big-endian total length.
        if (pdu.Length < TpktHeaderLength + FixedTpduLength)
            return false;
        if (pdu[0] != 3 || pdu[1] != 0)
            return false;
        int tpktLength = BinaryPrimitives.ReadUInt16BigEndian(pdu[2..]);
        if (tpktLength != pdu.Length)
            return false;

        // X.224 CR TPDU: LI excludes itself, so the TPDU spans 1 + LI bytes and must
        // exactly fill the TPKT payload.
        var tpdu = pdu[TpktHeaderLength..];
        int li = tpdu[0];
        if (li + 1 != tpdu.Length || li < FixedTpduLength - 1)
            return false;
        if (tpdu[1] != ConnectionRequestCode)
            return false;

        var variable = tpdu[FixedTpduLength..];
        string? cookie = null;
        string? routingToken = null;

        // Optional cookie OR routing token, each a CRLF-terminated line (MS-RDPBCGR 2.2.1.1:
        // "routingToken (variable): An optional and variable-length routing token ...
        // terminated by a 0x0D0A two-byte sequence", format per [MSFT-SDLBTS]; "cookie" MUST
        // be "Cookie: mstshash=IDENTIFIER"; the two are mutually exclusive). In practice mstsc
        // sends the .rdp file's loadbalanceinfo:s: value verbatim as the routing token, e.g.
        // "tsv://MS Terminal Services Plugin.1.Coll" or "Cookie: msts=...", so any
        // printable line that is not an mstshash cookie is reported raw as RoutingToken.
        // RDP_NEG_REQ starts with type 0x01 (non-printable), so a printable first byte
        // unambiguously means a line is present.
        if (!variable.IsEmpty && variable[0] is >= 0x20 and <= 0x7E)
        {
            int crlf = variable.IndexOf(Crlf);
            if (crlf < 0)
                return false;
            var line = variable[..crlf];
            if (!IsPrintableAscii(line))
                return false;

            if (line.StartsWith(MstsHashPrefix))
                cookie = Encoding.ASCII.GetString(line[MstsHashPrefix.Length..]);
            else
                routingToken = Encoding.ASCII.GetString(line);

            variable = variable[(crlf + Crlf.Length)..];
        }

        // Optional RDP_NEG_REQ (MS-RDPBCGR 2.2.1.1.1), optionally followed by
        // RDP_NEG_CORRELATION_INFO (2.2.1.1.2) when flagged.
        uint? requestedProtocols = null;
        if (!variable.IsEmpty)
        {
            if (variable.Length < NegotiationRequestLength
                || variable[0] != NegotiationRequestType
                || BinaryPrimitives.ReadUInt16LittleEndian(variable[2..]) != NegotiationRequestLength)
                return false;

            byte flags = variable[1];
            requestedProtocols = BinaryPrimitives.ReadUInt32LittleEndian(variable[4..]);
            variable = variable[NegotiationRequestLength..];

            int expectedTrailer = (flags & CorrelationInfoPresent) != 0 ? CorrelationInfoLength : 0;
            if (variable.Length != expectedTrailer)
                return false;
        }

        request = new X224ConnectionRequest(cookie, routingToken, requestedProtocols);
        return true;
    }

    /// <summary>
    /// Reduces an mstshash cookie value to a bare lowercase username, stripping any
    /// DOMAIN\ prefix or @realm suffix. Null if nothing usable remains.
    /// </summary>
    /// <remarks>
    /// Truncation: Windows mstsc is widely reported to truncate the mstshash identifier to
    /// its first 9 characters, DOMAIN\ included (e.g. "domain\johnsmith" is sent as
    /// "domain\jo"); this is not in MS-RDPBCGR, which just says "IDENTIFIER is an ANSI
    /// character string" — sources: F5 DevCentral "rdp cookie question" / "iRule for RDS
    /// session" threads and the Zeek community thread "Length of Username (Cookie)".
    /// FreeRDP (nego.c) only caps it at its CookieMaxLength setting (default 255). The routing layer must therefore treat the
    /// result as a possibly-truncated prefix, not an exact username.
    /// </remarks>
    public static string? NormalizeUser(string? cookie)
    {
        if (cookie is null)
            return null;

        var user = cookie.AsSpan().Trim();

        int backslash = user.LastIndexOf('\\');
        if (backslash >= 0)
            user = user[(backslash + 1)..];

        int at = user.IndexOf('@');
        if (at >= 0)
            user = user[..at];

        user = user.Trim();
        return user.IsEmpty ? null : user.ToString().ToLowerInvariant();
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is < 0x20 or > 0x7E)
                return false;
        }
        return true;
    }
}
