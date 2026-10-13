using Hotlab.Gateway.Protocol;
using static Hotlab.Gateway.Tests.Protocol.X224TestPdus;

namespace Hotlab.Gateway.Tests.Protocol;

public class X224ConnectionRequestTests
{
    [Fact]
    public void Mstsc_StyleRequest_ParsesCookieAndProtocols()
    {
        Assert.True(X224ConnectionRequest.TryParse(Mstsc, out var req));
        Assert.NotNull(req);
        Assert.Equal("eltons", req.Cookie);
        Assert.Null(req.RoutingToken);
        Assert.Equal(0x3u, req.RequestedProtocols);
    }

    [Fact]
    public void Builder_MatchesHandEncodedMstscPdu()
    {
        Assert.Equal(Mstsc, Build("Cookie: mstshash=eltons\r\n", NegReq(0x3)));
    }

    [Fact]
    public void FreeRdp_StyleRequest_WithDomainUser()
    {
        // FreeRDP sends the full /u: value and typically requests SSL|HYBRID|HYBRID_EX (0xB).
        var pdu = Build("Cookie: mstshash=LAB\\Alice.Smith\r\n", NegReq(0xB));
        Assert.True(X224ConnectionRequest.TryParse(pdu, out var req));
        Assert.Equal("LAB\\Alice.Smith", req!.Cookie);
        Assert.Equal(0xBu, req.RequestedProtocols);
        Assert.Equal("alice.smith", X224ConnectionRequest.NormalizeUser(req.Cookie));
    }

    [Fact]
    public void NoCookie_OnlyNegReq()
    {
        Assert.True(X224ConnectionRequest.TryParse(Build(null, NegReq(0x1)), out var req));
        Assert.Null(req!.Cookie);
        Assert.Null(req.RoutingToken);
        Assert.Equal(0x1u, req.RequestedProtocols);
    }

    [Fact]
    public void Bare_CrWithNoVariablePart()
    {
        Assert.True(X224ConnectionRequest.TryParse(Build(null, null), out var req));
        Assert.Equal(new X224ConnectionRequest(null, null, null), req);
    }

    [Fact]
    public void CookieWithoutNegReq_LegacyClient()
    {
        Assert.True(X224ConnectionRequest.TryParse(Build("Cookie: mstshash=bob\r\n", null), out var req));
        Assert.Equal("bob", req!.Cookie);
        Assert.Null(req.RequestedProtocols);
    }

    [Fact]
    public void RoutingToken_IsReturnedRaw()
    {
        var pdu = Build("Cookie: msts=3640205228.15629.0000\r\n", NegReq(0x3));
        Assert.True(X224ConnectionRequest.TryParse(pdu, out var req));
        Assert.Null(req!.Cookie);
        Assert.Equal("Cookie: msts=3640205228.15629.0000", req.RoutingToken);
        Assert.Equal(0x3u, req.RequestedProtocols);
    }

    [Theory]
    [InlineData("tsv://MS Terminal Services Plugin.1.Coll")]
    [InlineData("hotlab:lab=sql101;t=abc")]
    public void LoadBalanceInfoToken_WithNegReq(string token)
    {
        Assert.True(X224ConnectionRequest.TryParse(Build(token + "\r\n", NegReq(0x3)), out var req));
        Assert.Null(req!.Cookie);
        Assert.Equal(token, req.RoutingToken);
        Assert.Equal(0x3u, req.RequestedProtocols);
    }

    [Theory]
    [InlineData("tsv://MS Terminal Services Plugin.1.Coll")]
    [InlineData("hotlab:lab=sql101;t=abc")]
    public void LoadBalanceInfoToken_WithoutNegReq(string token)
    {
        Assert.True(X224ConnectionRequest.TryParse(Build(token + "\r\n", null), out var req));
        Assert.Null(req!.Cookie);
        Assert.Equal(token, req.RoutingToken);
        Assert.Null(req.RequestedProtocols);
    }

    [Theory]
    [InlineData("hotlab:x\r\nCookie: mstshash=bob\r\n")]
    [InlineData("Cookie: mstshash=bob\r\nhotlab:x\r\n")]
    public void TokenAndCookie_InEitherOrder_AreBothReturned(string lines)
    {
        Assert.True(X224ConnectionRequest.TryParse(Build(lines, NegReq(0x3)), out var req));
        Assert.Equal("bob", req!.Cookie);
        Assert.Equal("hotlab:x", req.RoutingToken);
        Assert.Equal(0x3u, req.RequestedProtocols);
    }

    [Theory]
    [InlineData("Cookie: mstshash=a\r\nCookie: mstshash=b\r\n")]
    [InlineData("hotlab:x\r\ntsv://y\r\n")]
    [InlineData("hotlab:x\r\nCookie: mstshash=b\r\ntsv://y\r\n")]
    public void RepeatedOrExtraIdentityLines_AreRejected(string lines)
    {
        Assert.False(X224ConnectionRequest.TryParse(Build(lines, NegReq(0x3)), out _));
    }

    [Fact]
    public void LoadBalanceInfoToken_MissingCrlf_IsRejected()
    {
        Assert.False(X224ConnectionRequest.TryParse(Build("hotlab:lab=sql101", NegReq(0x3)), out _));
        Assert.False(X224ConnectionRequest.TryParse(Build("hotlab:lab=sql101", null), out _));
    }

    [Fact]
    public void LoadBalanceInfoToken_WithControlChar_IsRejected()
    {
        Assert.False(X224ConnectionRequest.TryParse(Build("hotlab:\tlab\r\n", null), out _));
    }

    [Fact]
    public void NonPrintableNonNegReqVariablePart_IsRejected()
    {
        Assert.False(X224ConnectionRequest.TryParse(Build([0x00, 0x41, 0x0D, 0x0A]), out _));
    }

    [Fact]
    public void NegReq_WithCorrelationInfo_IsAccepted()
    {
        var variable = new List<byte>(System.Text.Encoding.ASCII.GetBytes("Cookie: mstshash=x\r\n"));
        variable.AddRange(NegReq(0x3, flags: 0x08));
        variable.AddRange(CorrelationInfo());
        Assert.True(X224ConnectionRequest.TryParse(Build([.. variable]), out var req));
        Assert.Equal("x", req!.Cookie);
    }

    [Theory]
    [InlineData(0, 0x00)] // type must be TYPE_RDP_CORRELATION_INFO (0x06)
    [InlineData(0, 0x01)]
    [InlineData(2, 0x00)] // length must be 0x0024
    [InlineData(2, 0x23)]
    [InlineData(3, 0x01)] // length high byte
    public void NegReq_WithMalformedCorrelationInfo_IsRejected(int offset, byte value)
    {
        var info = CorrelationInfo();
        info[offset] = value;
        var variable = new List<byte>(NegReq(0x3, flags: 0x08));
        variable.AddRange(info);
        Assert.False(X224ConnectionRequest.TryParse(Build([.. variable]), out _));
    }

    [Fact]
    public void CorrelationFlag_WithoutCorrelationInfo_IsRejected()
    {
        Assert.False(X224ConnectionRequest.TryParse(Build(null, NegReq(0x3, flags: 0x08)), out _));
    }

    [Fact]
    public void TrailingGarbageAfterNegReq_IsRejected()
    {
        var variable = new List<byte>(NegReq(0x3)) { 0xFF };
        Assert.False(X224ConnectionRequest.TryParse(Build([.. variable]), out _));
    }

    [Fact]
    public void BadNegReqHeader_IsRejected()
    {
        var neg = NegReq(0x3);
        neg[2] = 0x09; // length field must be 8
        Assert.False(X224ConnectionRequest.TryParse(Build(null, neg), out _));
        neg = NegReq(0x3);
        neg[0] = 0x02; // RDP_NEG_RSP type, not a request
        Assert.False(X224ConnectionRequest.TryParse(Build(null, neg), out _));
    }

    [Fact]
    public void MissingCrlf_IsRejected()
    {
        Assert.False(X224ConnectionRequest.TryParse(Build("Cookie: mstshash=eltons", NegReq(0x3)), out _));
        Assert.False(X224ConnectionRequest.TryParse(Build("Cookie: mstshash=eltons\r", null), out _));
    }

    [Fact]
    public void ControlCharactersInCookie_AreRejected()
    {
        Assert.False(X224ConnectionRequest.TryParse(Build("Cookie: mstshash=a\0b\r\n", null), out _));
        byte[] latin1 = [.. "Cookie: mstshash="u8, 0xE9, 0x0D, 0x0A]; // raw non-ASCII byte
        Assert.False(X224ConnectionRequest.TryParse(Build(latin1), out _));
    }

    [Theory]
    [InlineData(0xD0)] // CC
    [InlineData(0xF0)] // DT
    [InlineData(0xE1)] // CR with non-zero CDT
    [InlineData(0x80)] // DR
    public void WrongTpduCode_IsRejected(byte code)
    {
        Assert.False(X224ConnectionRequest.TryParse(Build(NegReq(0x3), code), out _));
    }

    [Fact]
    public void LiMismatch_IsRejected()
    {
        var pdu = (byte[])Mstsc.Clone();
        pdu[4]--;
        Assert.False(X224ConnectionRequest.TryParse(pdu, out _));
        pdu[4] += 2;
        Assert.False(X224ConnectionRequest.TryParse(pdu, out _));
    }

    [Fact]
    public void TpktLengthMismatch_IsRejected()
    {
        var pdu = (byte[])Mstsc.Clone();
        pdu[3]++;
        Assert.False(X224ConnectionRequest.TryParse(pdu, out _));
        // Extra trailing byte beyond declared TPKT length.
        Assert.False(X224ConnectionRequest.TryParse([.. Mstsc, 0x00], out _));
    }

    [Fact]
    public void BadTpktHeader_IsRejected()
    {
        var pdu = (byte[])Mstsc.Clone();
        pdu[0] = 0x02;
        Assert.False(X224ConnectionRequest.TryParse(pdu, out _));
        pdu = (byte[])Mstsc.Clone();
        pdu[1] = 0x01;
        Assert.False(X224ConnectionRequest.TryParse(pdu, out _));
    }

    [Fact]
    public void TooShortLi_IsRejected()
    {
        // LI=5 (one short of the fixed CR part), lengths otherwise consistent.
        byte[] pdu = [0x03, 0x00, 0x00, 0x0A, 0x05, 0xE0, 0x00, 0x00, 0x00, 0x00];
        Assert.False(X224ConnectionRequest.TryParse(pdu, out _));
    }

    [Fact]
    public void EveryTruncation_IsRejectedWithoutThrowing()
    {
        for (int len = 0; len < Mstsc.Length; len++)
        {
            Assert.False(X224ConnectionRequest.TryParse(Mstsc.AsSpan(0, len), out var req), $"length {len}");
            Assert.Null(req);
        }
    }

    [Fact]
    public void RandomGarbage_NeverThrows()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 5000; i++)
        {
            var buf = new byte[rng.Next(0, 300)];
            rng.NextBytes(buf);
            if (buf.Length >= 4 && rng.Next(2) == 0)
            {
                // Make the header plausible so the deeper parsing paths get exercised.
                buf[0] = 3; buf[1] = 0; buf[2] = (byte)(buf.Length >> 8); buf[3] = (byte)buf.Length;
                if (buf.Length >= 6) { buf[4] = (byte)(buf.Length - 5); buf[5] = 0xE0; }
            }
            _ = X224ConnectionRequest.TryParse(buf, out _);
        }
    }

    [Fact]
    public void EveryByteMutation_NeverThrows()
    {
        for (int i = 0; i < Mstsc.Length; i++)
        {
            for (int v = 0; v < 256; v++)
            {
                var pdu = (byte[])Mstsc.Clone();
                pdu[i] = (byte)v;
                _ = X224ConnectionRequest.TryParse(pdu, out _);
            }
        }
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("  Alice  ", "alice")]
    [InlineData("LAB\\Alice", "alice")]
    [InlineData("lab.example.org\\BOB", "bob")]
    [InlineData("bob@LAB.EXAMPLE.ORG", "bob")]
    [InlineData("LAB\\bob@realm", "bob")]
    [InlineData("domain\\jo", "jo")] // mstsc-truncated "domain\johnsmith"
    [InlineData("LAB\\", null)]
    [InlineData("@realm", null)]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("\u212Aate", null)] // KELVIN SIGN would lowercase to ASCII 'k'
    [InlineData("LAB\\caf\u00E9", null)]
    public void NormalizeUser_Cases(string? input, string? expected)
    {
        Assert.Equal(expected, X224ConnectionRequest.NormalizeUser(input));
    }
}
