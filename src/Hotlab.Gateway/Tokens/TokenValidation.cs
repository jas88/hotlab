namespace Hotlab.Gateway.Tokens;

/// <summary>Outcome class of <see cref="RoutingTokenService.Validate"/>.</summary>
public enum TokenStatus
{
    /// <summary>Not a Hotlab token (no <c>hotlab:</c> prefix); route by other means.</summary>
    NotHotlab,

    /// <summary>A Hotlab token with a good signature that has not expired.</summary>
    Valid,

    /// <summary>A Hotlab token that must be rejected; see <see cref="TokenValidation.Reason"/>.</summary>
    Invalid,
}

/// <summary>
/// Result of validating a routing token. Never carries the signature, so it is safe to log.
/// </summary>
/// <param name="Status">Outcome class.</param>
/// <param name="User">
/// The token's user. Authenticated for <see cref="TokenStatus.Valid"/>; for
/// <see cref="TokenStatus.Invalid"/> it is only the unverified claim (if one could be parsed)
/// and must not be used for routing.
/// </param>
/// <param name="Expiry">The token's expiry, if one could be parsed.</param>
/// <param name="Reason">Why the token is invalid; one of the <c>Reason*</c> constants.</param>
public sealed record TokenValidation(TokenStatus Status, string? User, DateTimeOffset? Expiry, string? Reason)
{
    /// <summary>The token is structurally broken.</summary>
    public const string ReasonMalformed = "malformed";

    /// <summary>The HMAC does not match.</summary>
    public const string ReasonBadSignature = "bad signature";

    /// <summary>The token expired (beyond the allowed clock skew).</summary>
    public const string ReasonExpired = "expired";

    /// <summary>No signing key is configured, so no Hotlab token can be trusted.</summary>
    public const string ReasonDisabled = "tokens disabled";

    /// <summary>The version field is not one this gateway understands.</summary>
    public const string ReasonUnknownVersion = "unknown version";

    /// <summary>Result for a token without the Hotlab prefix.</summary>
    public static TokenValidation NotHotlab { get; } = new(TokenStatus.NotHotlab, null, null, null);

    /// <summary>Result for an authenticated, unexpired token.</summary>
    public static TokenValidation Valid(string user, DateTimeOffset expiry) =>
        new(TokenStatus.Valid, user, expiry, null);

    /// <summary>Result for a token that must be rejected.</summary>
    public static TokenValidation Invalid(string reason, string? claimedUser = null, DateTimeOffset? expiry = null) =>
        new(TokenStatus.Invalid, claimedUser, expiry, reason);
}
