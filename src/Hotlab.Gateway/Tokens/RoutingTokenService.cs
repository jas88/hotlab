using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tokens;

/// <summary>
/// Mints and validates signed Hotlab routing tokens, carried in an .rdp file's
/// <c>loadbalanceinfo:s:</c> value and sent by mstsc as the X.224 routing token.
/// </summary>
/// <remarks>
/// Format: <c>hotlab:v1:&lt;user&gt;:&lt;expiryUnixSeconds&gt;:&lt;sig&gt;</c>, where
/// <c>sig</c> is base64url (no padding) of HMAC-SHA256(key, <c>"hotlab:v1:&lt;user&gt;:&lt;expiry&gt;"</c>).
/// The whole X.224 Connection Request must fit a one-byte length indicator (at most 254
/// TPDU bytes), so the token is kept compact and usernames are capped at
/// <see cref="MaxUserLength"/>. Signatures are compared in constant time.
/// </remarks>
public sealed class RoutingTokenService
{
    /// <summary>Prefix identifying a Hotlab token.</summary>
    public const string Prefix = "hotlab:";

    /// <summary>Current token version.</summary>
    public const string Version = "v1";

    /// <summary>Longest username that can be minted into a token.</summary>
    public const int MaxUserLength = 64;

    /// <summary>Longest prefix of a non-Hotlab routing token that may be logged.</summary>
    public const int MaxLoggedTokenChars = 40;

    private const int SignatureChars = 43; // base64url of 32 bytes, unpadded
    private const long MaxUnixSeconds = 253402300799; // 9999-12-31T23:59:59Z

    private readonly byte[]? _key;
    private readonly RoutingTokenOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <param name="options">Token options.</param>
    /// <param name="timeProvider">Clock; null for <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="InvalidOperationException">A signing key is configured but invalid.</exception>
    public RoutingTokenService(IOptions<RoutingTokenOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _key = _options.DecodeSigningKey();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when a signing key is configured.</summary>
    public bool Enabled => _key is not null;

    /// <summary>
    /// Mints a token for <paramref name="user"/> (trimmed and lowercased) that expires after
    /// <paramref name="lifetime"/>, or <see cref="RoutingTokenOptions.DefaultLifetime"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Tokens are disabled (no signing key).</exception>
    /// <exception cref="ArgumentException">The user is empty, too long, or contains ':' or
    /// non-printable/non-ASCII characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lifetime is not positive.</exception>
    public string Mint(string user, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_key is null)
            throw new InvalidOperationException(
                $"Routing tokens are disabled: {RoutingTokenOptions.SectionName}:SigningKey is not set.");

        var normalized = NormalizeUser(user);
        var life = lifetime ?? _options.DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero, nameof(lifetime));

        long expiry = (_time.GetUtcNow() + life).ToUnixTimeSeconds();
        var payload = string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Version}:{normalized}:{expiry}");
        return $"{payload}:{Sign(_key, payload)}";
    }

    /// <summary>
    /// Validates a raw routing token. Anything not starting with <see cref="Prefix"/> is
    /// <see cref="TokenStatus.NotHotlab"/>; Hotlab tokens are either valid or invalid with a
    /// reason. Never throws for any input.
    /// </summary>
    public TokenValidation Validate(string routingToken)
    {
        ArgumentNullException.ThrowIfNull(routingToken);
        if (!routingToken.StartsWith(Prefix, StringComparison.Ordinal))
            return TokenValidation.NotHotlab;

        // Embedders of the mstscax ActiveX control must pad LoadBalanceInfo to an even byte
        // length "by adding a space" (IMsRdpClientAdvancedSettings::LoadBalanceInfo docs), so
        // trailing spaces are not part of the token. Tokens never end in a space otherwise.
        routingToken = routingToken.TrimEnd(' ');
        if (_key is null)
            return TokenValidation.Invalid(TokenValidation.ReasonDisabled);

        var parts = routingToken.Split(':');
        if (parts.Length < 2 || parts[1].Length == 0)
            return TokenValidation.Invalid(TokenValidation.ReasonMalformed);
        if (parts[1] != Version)
            return TokenValidation.Invalid(TokenValidation.ReasonUnknownVersion);
        if (parts.Length != 5)
            return TokenValidation.Invalid(TokenValidation.ReasonMalformed);

        var (user, expiryText, signature) = (parts[2], parts[3], parts[4]);
        if (!IsValidUser(user)
            || !long.TryParse(expiryText, NumberStyles.None, CultureInfo.InvariantCulture, out long expirySeconds)
            || expirySeconds > MaxUnixSeconds
            || !IsBase64UrlSignature(signature))
            return TokenValidation.Invalid(TokenValidation.ReasonMalformed);

        var expiry = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        var payload = routingToken[..^(signature.Length + 1)];
        var expected = Encoding.ASCII.GetBytes(Sign(_key, payload));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature)))
            return TokenValidation.Invalid(TokenValidation.ReasonBadSignature, user, expiry);

        if (_time.GetUtcNow() > expiry + _options.ClockSkew)
            return TokenValidation.Invalid(TokenValidation.ReasonExpired, user, expiry);

        return TokenValidation.Valid(user, expiry);
    }

    /// <summary>
    /// A loggable form of a raw routing token: Hotlab tokens are fully redacted (log the
    /// <see cref="TokenValidation"/> fields instead), anything else is cut to its first
    /// <see cref="MaxLoggedTokenChars"/> characters.
    /// </summary>
    public static string RedactForLog(string routingToken)
    {
        ArgumentNullException.ThrowIfNull(routingToken);
        if (routingToken.StartsWith(Prefix, StringComparison.Ordinal))
            return Prefix + "<redacted>";
        return routingToken.Length <= MaxLoggedTokenChars
            ? routingToken
            : string.Concat(routingToken.AsSpan(0, MaxLoggedTokenChars), "...");
    }

    private static string NormalizeUser(string user)
    {
        // Validate before lowercasing: ToLowerInvariant maps some non-ASCII characters to
        // ASCII (U+212A KELVIN SIGN becomes 'k'), which would let them through the check.
        var trimmed = user.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("User must not be empty.", nameof(user));
        if (trimmed.Length > MaxUserLength)
            throw new ArgumentException($"User must be at most {MaxUserLength} characters.", nameof(user));
        if (!IsValidUser(trimmed))
            throw new ArgumentException(
                "User must be printable ASCII and must not contain ':'.", nameof(user));
        return trimmed.ToLowerInvariant();
    }

    private static bool IsValidUser(string user)
    {
        if (user.Length == 0)
            return false;
        foreach (char c in user)
        {
            if (c is < (char)0x20 or > (char)0x7E or ':')
                return false;
        }
        return true;
    }

    private static bool IsBase64UrlSignature(string signature)
    {
        if (signature.Length != SignatureChars)
            return false;
        foreach (char c in signature)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return false;
        }
        return true;
    }

    private static string Sign(byte[] key, string payload) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
}
