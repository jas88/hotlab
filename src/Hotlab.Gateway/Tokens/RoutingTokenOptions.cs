namespace Hotlab.Gateway.Tokens;

/// <summary>
/// Signed routing token settings, bound from <c>Hotlab:Tokens</c>.
/// </summary>
/// <remarks>
/// The signing key must never be logged or committed; <see cref="ToString"/> redacts it. As
/// with the Proxmox token secret, in production the key is meant to come from systemd
/// credentials (<c>LoadCredentialEncrypted</c>) or an environment variable
/// (<c>Hotlab__Tokens__SigningKey</c>), not appsettings.json. Generate one with e.g.
/// <c>openssl rand -base64 32</c>. An empty key disables Hotlab tokens entirely.
/// </remarks>
public sealed class RoutingTokenOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Hotlab:Tokens";

    /// <summary>Minimum decoded HMAC-SHA256 key length in bytes.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>Base64 HMAC-SHA256 key (at least 32 bytes decoded). Empty disables tokens. Never log this.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Lifetime of a minted token when the caller does not specify one.</summary>
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How far past its expiry a token is still accepted, to absorb clock differences.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Decodes <see cref="SigningKey"/>. Returns null when no key is configured (tokens
    /// disabled).
    /// </summary>
    /// <exception cref="InvalidOperationException">A key is set but is not valid base64 or is too short.</exception>
    public byte[]? DecodeSigningKey()
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
            return null;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(SigningKey.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{SectionName}:SigningKey is not valid base64.");
        }

        if (key.Length < MinimumKeyBytes)
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey must decode to at least {MinimumKeyBytes} bytes (got {key.Length}).");
        return key;
    }

    /// <summary>Describes the settings with the key redacted.</summary>
    public override string ToString() =>
        $"Routing tokens {(string.IsNullOrWhiteSpace(SigningKey) ? "disabled (no key)" : "enabled (key redacted)")}, " +
        $"default lifetime {DefaultLifetime}, clock skew {ClockSkew}";
}
