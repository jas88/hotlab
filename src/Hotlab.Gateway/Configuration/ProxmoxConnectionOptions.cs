using Hotlab.Proxmox;

namespace Hotlab.Gateway.Configuration;

/// <summary>
/// Proxmox VE API connection settings, bound from <c>Hotlab:Proxmox</c>.
/// </summary>
/// <remarks>
/// The token secret must never be logged; <see cref="ToString"/> redacts it. In production
/// the secret is intended to come from systemd credentials (<c>LoadCredentialEncrypted</c>,
/// see docs/architecture.md "Holding Proxmox API credentials"); for development it can be
/// supplied via the environment, e.g. <c>Hotlab__Proxmox__TokenSecret</c>.
/// </remarks>
public sealed class ProxmoxConnectionOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Hotlab:Proxmox";

    /// <summary>Host base URL, e.g. <c>https://pve.example:8006</c>. Must be https.</summary>
    public string? Url { get; set; }

    /// <summary>Full API token id, e.g. <c>hotlab@pve!broker</c>.</summary>
    public string? TokenId { get; set; }

    /// <summary>API token secret. Never log this.</summary>
    public string? TokenSecret { get; set; }

    /// <summary>Optional SHA-256 fingerprint to pin the server certificate.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Accept any server certificate (isolated test hosts only). Mutually exclusive with <see cref="Fingerprint"/>.</summary>
    public bool Insecure { get; set; }

    /// <summary>
    /// Checks the settings: an absolute <c>https</c> URL, a token id and secret, and at most
    /// one of <see cref="Insecure"/> and <see cref="Fingerprint"/>. Returns an error message,
    /// or null when valid.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Url) || !Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            return $"{SectionName}:Url is missing or not an absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttps)
            return $"{SectionName}:Url must use https (got '{uri.Scheme}'); the API token is sent with every request.";
        if (string.IsNullOrWhiteSpace(TokenId))
            return $"{SectionName}:TokenId is required.";
        if (string.IsNullOrWhiteSpace(TokenSecret))
            return $"{SectionName}:TokenSecret is required.";
        if (Insecure && !string.IsNullOrWhiteSpace(Fingerprint))
            return $"{SectionName}:Insecure and {SectionName}:Fingerprint are mutually exclusive; " +
                   "set Fingerprint to pin the certificate, or Insecure (test hosts only), not both.";
        return null;
    }

    /// <summary>Builds a client, validating the settings first (see <see cref="Validate"/>).</summary>
    /// <exception cref="InvalidOperationException">A required setting is missing or invalid.</exception>
    public ProxmoxClient CreateClient()
    {
        if (Validate() is { } error)
            throw new InvalidOperationException(error);

        var uri = new Uri(Url!, UriKind.Absolute);
        ProxmoxTlsOptions? tls =
            Insecure ? new ProxmoxTlsOptions { DangerousAcceptAnyCertificate = true }
            : !string.IsNullOrWhiteSpace(Fingerprint) ? new ProxmoxTlsOptions { Sha256Fingerprint = Fingerprint }
            : null;

        return new ProxmoxClient(uri, TokenId!, TokenSecret!, tls);
    }

    /// <summary>Describes the connection with the token secret redacted.</summary>
    public override string ToString() =>
        $"Proxmox {Url} token {TokenId} (secret {(string.IsNullOrEmpty(TokenSecret) ? "unset" : "redacted")})";
}
