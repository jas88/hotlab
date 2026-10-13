using System.Security.Cryptography;
using System.Text;
using Hotlab.Gateway.Tokens;
using Microsoft.Extensions.Options;
using static Hotlab.Gateway.Tests.Tokens.TokenTestSupport;

namespace Hotlab.Gateway.Tests.Tokens;

public sealed class RoutingTokenServiceTests
{
    private readonly ManualTimeProvider _time = new(Epoch);

    [Fact]
    public void Mint_ThenValidate_RoundTrips()
    {
        var svc = Create(_time);
        var token = svc.Mint("  Alice ", TimeSpan.FromHours(1));

        var parts = token.Split(':');
        Assert.Equal(["hotlab", "v1", "alice", Epoch.AddHours(1).ToUnixTimeSeconds().ToString()], parts[..4]);
        Assert.Equal(43, parts[4].Length);

        var result = svc.Validate(token);
        Assert.Equal(TokenStatus.Valid, result.Status);
        Assert.Equal("alice", result.User);
        Assert.Equal(Epoch.AddHours(1), result.Expiry);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Signature_IsUnpaddedBase64UrlHmacSha256OfPayload()
    {
        var token = Create(_time).Mint("bob", TimeSpan.FromHours(8));
        var payload = token[..token.LastIndexOf(':')];
        var mac = HMACSHA256.HashData(Convert.FromBase64String(Key), Encoding.UTF8.GetBytes(payload));
        var expected = Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, SignatureOf(token));
    }

    [Fact]
    public void Mint_UsesDefaultLifetime()
    {
        var svc = new RoutingTokenService(Options.Create(new RoutingTokenOptions
        {
            SigningKey = Key,
            DefaultLifetime = TimeSpan.FromMinutes(90),
        }), _time);
        Assert.Equal(Epoch.AddMinutes(90), svc.Validate(svc.Mint("alice")).Expiry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a:b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("café")]
    public void Mint_RejectsBadUsers(string user) =>
        Assert.Throws<ArgumentException>(() => Create(_time).Mint(user));

    [Fact]
    public void Mint_RejectsOverlongUser() =>
        Assert.Throws<ArgumentException>(() => Create(_time).Mint(new string('a', RoutingTokenService.MaxUserLength + 1)));

    [Fact]
    public void Mint_RejectsNonPositiveLifetime() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(_time).Mint("alice", TimeSpan.Zero));

    [Fact]
    public void TamperedUser_IsBadSignature()
    {
        var svc = Create(_time);
        var token = svc.Mint("alice").Replace(":alice:", ":admin:", StringComparison.Ordinal);
        var result = svc.Validate(token);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        Assert.Equal(TokenValidation.ReasonBadSignature, result.Reason);
        Assert.Equal("admin", result.User); // unverified claim, reported for logging only
    }

    [Fact]
    public void TamperedExpiry_IsBadSignature()
    {
        var svc = Create(_time);
        var parts = svc.Mint("alice").Split(':');
        parts[3] = (long.Parse(parts[3]) + 86400).ToString();
        Assert.Equal(TokenValidation.ReasonBadSignature, svc.Validate(string.Join(':', parts)).Reason);
    }

    [Fact]
    public void TamperedSignature_EveryPosition_IsBadSignature()
    {
        var svc = Create(_time);
        var token = svc.Mint("alice");
        int sigStart = token.LastIndexOf(':') + 1;
        for (int i = sigStart; i < token.Length; i++)
        {
            var chars = token.ToCharArray();
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            Assert.Equal(TokenValidation.ReasonBadSignature, svc.Validate(new string(chars)).Reason);
        }
    }

    [Fact]
    public void TokenFromOtherKey_IsBadSignature()
    {
        var other = Create(_time, Convert.ToBase64String(new byte[32]));
        Assert.Equal(TokenValidation.ReasonBadSignature, Create(_time).Validate(other.Mint("alice")).Reason);
    }

    [Fact]
    public void Expired_BeyondSkew_IsRejected()
    {
        var svc = Create(_time, skew: TimeSpan.FromMinutes(2));
        var token = svc.Mint("alice", TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        var result = svc.Validate(token);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        Assert.Equal(TokenValidation.ReasonExpired, result.Reason);
        Assert.Equal(Epoch.AddHours(1), result.Expiry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(120)]
    public void ExpiredWithinSkew_IsAccepted(int secondsPastExpiry)
    {
        var svc = Create(_time, skew: TimeSpan.FromMinutes(2));
        var token = svc.Mint("alice", TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(secondsPastExpiry));
        Assert.Equal(TokenStatus.Valid, svc.Validate(token).Status);
    }

    [Fact]
    public void Disabled_RejectsHotlabTokens_AndCannotMint()
    {
        var token = Create(_time).Mint("alice");
        foreach (var key in new[] { "", "   ", null })
        {
            var disabled = new RoutingTokenService(Options.Create(new RoutingTokenOptions { SigningKey = key }), _time);
            Assert.False(disabled.Enabled);
            Assert.Equal(TokenValidation.ReasonDisabled, disabled.Validate(token).Reason);
            Assert.Equal(TokenStatus.NotHotlab, disabled.Validate("tsv://x").Status);
            Assert.Throws<InvalidOperationException>(() => disabled.Mint("alice"));
        }
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")] // 31 bytes
    public void InvalidConfiguredKey_FailsFast(string key) =>
        Assert.Throws<InvalidOperationException>(() => Create(_time, key));

    [Theory]
    [InlineData("hotlab:")]
    [InlineData("hotlab::alice:1:sig")]
    [InlineData("hotlab:v1")]
    [InlineData("hotlab:v1:alice")]
    [InlineData("hotlab:v1:alice:1792000000")]
    [InlineData("hotlab:v1::1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice::AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice:-5:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice:+5:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice:1e9:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice:99999999999999999999:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice:253402300800:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:v1:alice:1792000000:")]
    [InlineData("hotlab:v1:alice:1792000000:AAAA")]
    [InlineData("hotlab:v1:alice:1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("hotlab:v1:alice:1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("hotlab:v1:alice:1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:extra")]
    [InlineData("hotlab:v1:al\tice:1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_IsRejected(string token)
    {
        var result = Create(_time).Validate(token);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        Assert.Equal(TokenValidation.ReasonMalformed, result.Reason);
    }

    [Theory]
    [InlineData("hotlab:v2:alice:1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:V1:alice:1792000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hotlab:lab=sql101;t=abc")]
    public void UnknownVersion_IsRejected(string token) =>
        Assert.Equal(TokenValidation.ReasonUnknownVersion, Create(_time).Validate(token).Reason);

    [Theory]
    [InlineData("")]
    [InlineData("tsv://MS Terminal Services Plugin.1.Coll")]
    [InlineData("Cookie: msts=3640205228.15629.0000")]
    [InlineData("HOTLAB:v1:alice:1:x")]
    [InlineData("hotlab")]
    public void NonHotlab_PassesThrough(string token)
    {
        var result = Create(_time).Validate(token);
        Assert.Equal(TokenValidation.NotHotlab, result);
    }

    [Fact]
    public void TrailingSpacePadding_IsTolerated()
    {
        var svc = Create(_time);
        Assert.Equal(TokenStatus.Valid, svc.Validate(svc.Mint("alice") + " ").Status);
    }

    [Theory]
    [InlineData("tsv://MS Terminal Services Plugin.1.Coll", "tsv://MS Terminal Services Plugin.1.Coll")]
    [InlineData("tsv://MS Terminal Services Plugin.1.A-Very-Long-Collection-Name",
        "tsv://MS Terminal Services Plugin.1.A-Ve...")]
    [InlineData("hotlab:v1:alice:1:secret", "hotlab:<redacted>")]
    public void RedactForLog_TruncatesOrRedacts(string token, string expected) =>
        Assert.Equal(expected, RoutingTokenService.RedactForLog(token));

    [Fact]
    public void OptionsToString_DoesNotLeakKey()
    {
        var text = new RoutingTokenOptions { SigningKey = Key }.ToString();
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.Contains("redacted", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u212Aate")] // KELVIN SIGN: lowercases to ASCII 'k'
    [InlineData("caf\u00E9")]
    public void Mint_RejectsNonAsciiUser_EvenIfItLowercasesToAscii(string user)
    {
        Assert.Throws<ArgumentException>(() => Create(_time).Mint(user, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Options_Validate_ChecksLifetimeSkewAndKey()
    {
        Assert.Null(new RoutingTokenOptions().Validate());
        Assert.Null(new RoutingTokenOptions { SigningKey = Key, ClockSkew = TimeSpan.Zero }.Validate());
        Assert.Contains("ClockSkew",
            new RoutingTokenOptions { ClockSkew = TimeSpan.FromSeconds(-1) }.Validate(), StringComparison.Ordinal);
        Assert.Contains("DefaultLifetime",
            new RoutingTokenOptions { DefaultLifetime = TimeSpan.Zero }.Validate(), StringComparison.Ordinal);
        Assert.Contains("DefaultLifetime",
            new RoutingTokenOptions { DefaultLifetime = TimeSpan.FromHours(-1) }.Validate(), StringComparison.Ordinal);
        Assert.Contains("SigningKey",
            new RoutingTokenOptions { SigningKey = "not base64!" }.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_Validate_RejectsUnboundedLifetimeAndSkew()
    {
        Assert.Null(new RoutingTokenOptions { DefaultLifetime = RoutingTokenOptions.MaxLifetime }.Validate());
        Assert.Null(new RoutingTokenOptions { ClockSkew = RoutingTokenOptions.MaxClockSkew }.Validate());
        Assert.Contains("DefaultLifetime",
            new RoutingTokenOptions { DefaultLifetime = TimeSpan.MaxValue }.Validate(), StringComparison.Ordinal);
        Assert.Contains("DefaultLifetime",
            new RoutingTokenOptions { DefaultLifetime = RoutingTokenOptions.MaxLifetime + TimeSpan.FromSeconds(1) }.Validate(),
            StringComparison.Ordinal);
        Assert.Contains("ClockSkew",
            new RoutingTokenOptions { ClockSkew = TimeSpan.MaxValue }.Validate(), StringComparison.Ordinal);
        Assert.Contains("ClockSkew",
            new RoutingTokenOptions { ClockSkew = RoutingTokenOptions.MaxClockSkew + TimeSpan.FromSeconds(1) }.Validate(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MintRdpCommand_SharesMaxLifetime()
    {
        Assert.Equal(RoutingTokenOptions.MaxLifetime, MintRdpCommand.MaxLifetime);
    }

    [Fact]
    public void Mint_WithUnvalidatedHugeDefaultLifetime_ThrowsArgumentOutOfRange_NotOverflow()
    {
        var service = new RoutingTokenService(Options.Create(new RoutingTokenOptions
        {
            SigningKey = Key,
            DefaultLifetime = TimeSpan.MaxValue,
        }), _time);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Mint("alice"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Mint("alice", TimeSpan.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => service.Mint("alice", RoutingTokenOptions.MaxLifetime + TimeSpan.FromSeconds(1)));
        Assert.Equal(TokenStatus.Valid, service.Validate(service.Mint("alice", RoutingTokenOptions.MaxLifetime)).Status);
    }

    [Fact]
    public void Validate_WithUnvalidatedHugeClockSkew_DoesNotOverflow()
    {
        var token = Create(_time).Mint("alice", TimeSpan.FromHours(1));
        var service = Create(_time, skew: TimeSpan.MaxValue);

        Assert.Equal(TokenStatus.Valid, service.Validate(token).Status);

        // The skew is clamped to MaxClockSkew, so a long-expired token is still rejected.
        _time.Advance(TimeSpan.FromHours(1) + RoutingTokenOptions.MaxClockSkew + TimeSpan.FromSeconds(1));
        var result = service.Validate(token);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        Assert.Equal(TokenValidation.ReasonExpired, result.Reason);
    }

    [Fact]
    public void Validate_TokenExpiringAtMaxDate_WithSkew_DoesNotOverflow()
    {
        // Year-9999 expiry plus skew used to overflow DateTimeOffset arithmetic.
        _time.Now = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var service = Create(_time, skew: RoutingTokenOptions.MaxClockSkew);
        var token = service.Mint("alice", TimeSpan.FromHours(23));
        Assert.Equal(TokenStatus.Valid, service.Validate(token).Status);
    }
}
