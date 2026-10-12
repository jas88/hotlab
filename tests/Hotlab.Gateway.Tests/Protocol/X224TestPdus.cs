using System.Text;

namespace Hotlab.Gateway.Tests.Protocol;

/// <summary>Builds TPKT + X.224 Connection Request PDUs for tests.</summary>
internal static class X224TestPdus
{
    public static byte[] NegReq(uint protocols, byte flags = 0) =>
    [
        0x01, flags, 0x08, 0x00,
        (byte)protocols, (byte)(protocols >> 8), (byte)(protocols >> 16), (byte)(protocols >> 24),
    ];

    /// <summary>
    /// RDP_NEG_CORRELATION_INFO (MS-RDPBCGR 2.2.1.1.2): type 0x06, flags 0, length 0x0024
    /// (little-endian), a 16-byte correlationId (first byte not 0x00/0xF4, no 0x0D bytes),
    /// then 16 reserved zero bytes.
    /// </summary>
    public static byte[] CorrelationInfo()
    {
        var info = new byte[36];
        info[0] = 0x06;
        info[2] = 0x24;
        for (int i = 0; i < 16; i++)
            info[4 + i] = (byte)(0x10 + i);
        return info;
    }

    /// <summary>Wraps a variable part in a CR TPDU and TPKT header with consistent lengths.</summary>
    public static byte[] Build(byte[] variable, byte code = 0xE0)
    {
        int li = 6 + variable.Length;
        int total = 4 + 1 + li;
        var pdu = new List<byte>
        {
            0x03, 0x00, (byte)(total >> 8), (byte)total,
            (byte)li, code, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        pdu.AddRange(variable);
        return [.. pdu];
    }

    public static byte[] Build(string? cookieLine, byte[]? negReq)
    {
        var variable = new List<byte>();
        if (cookieLine is not null)
            variable.AddRange(Encoding.ASCII.GetBytes(cookieLine));
        if (negReq is not null)
            variable.AddRange(negReq);
        return Build([.. variable]);
    }

    /// <summary>
    /// A byte-exact mstsc-style CR: "Cookie: mstshash=eltons" with RDP_NEG_REQ requesting
    /// PROTOCOL_SSL | PROTOCOL_HYBRID (0x3). Total length 0x2C = 44 bytes.
    /// </summary>
    public static readonly byte[] Mstsc =
    [
        0x03, 0x00, 0x00, 0x2C,                         // TPKT, length 44
        0x27, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,       // LI=39, CR, refs, class 0
        0x43, 0x6F, 0x6F, 0x6B, 0x69, 0x65, 0x3A, 0x20, // "Cookie: "
        0x6D, 0x73, 0x74, 0x73, 0x68, 0x61, 0x73, 0x68, // "mstshash"
        0x3D, 0x65, 0x6C, 0x74, 0x6F, 0x6E, 0x73,       // "=eltons"
        0x0D, 0x0A,                                     // CRLF
        0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00, // RDP_NEG_REQ, SSL|HYBRID
    ];
}
