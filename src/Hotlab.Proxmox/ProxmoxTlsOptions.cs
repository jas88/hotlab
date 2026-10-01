namespace Hotlab.Proxmox;

/// <summary>
/// TLS validation policy for a Proxmox VE host. Default is normal chain validation.
/// For hosts with self-signed certificates, prefer pinning the server certificate's
/// SHA-256 fingerprint over disabling validation entirely.
/// </summary>
public sealed class ProxmoxTlsOptions
{
    /// <summary>
    /// SHA-256 fingerprint of the server certificate (hex, case-insensitive,
    /// ':' separators allowed). When set, the connection is accepted only if the
    /// presented certificate matches, regardless of chain errors.
    /// </summary>
    public string? Sha256Fingerprint { get; init; }

    /// <summary>Accept any certificate. Only for isolated test environments.</summary>
    public bool DangerousAcceptAnyCertificate { get; init; }

    internal string? NormalizedFingerprint =>
        Sha256Fingerprint?.Replace(":", "", StringComparison.Ordinal)
                          .Replace(" ", "", StringComparison.Ordinal)
                          .ToUpperInvariant();
}
