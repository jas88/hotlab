using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hotlab.Gateway.Deploy;

/// <summary>Naming, validation and password rules for lab deployments.</summary>
public static partial class DeployNames
{
    /// <summary>Tag put on every VM a deployment creates; teardown only destroys tagged VMs.</summary>
    public const string Tag = "hotlab";

    /// <summary>Generated password length.</summary>
    public const int PasswordLength = 16;

    /// <summary>Password alphabet: letters and digits without look-alikes (0/O/o, 1/l/I/i).</summary>
    public const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>Longest deployment name (keeps pool ids and VM names short).</summary>
    public const int MaxDeploymentNameLength = 32;

    private const int MaxDnsLabelLength = 63;

    /// <summary>Accounts that must never be (re)used for a lab user.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "root" };

    /// <summary>
    /// Trims and lowercases <paramref name="raw"/> and checks it against the Linux username
    /// rule <c>[a-z_][a-z0-9_-]{0,31}</c>. Returns null (with a reason) when invalid.
    /// </summary>
    public static string? NormalizeUser(string raw, out string error)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var user = raw.Trim().ToLowerInvariant();
        error = "";
        if (!LinuxUserRegex().IsMatch(user))
        {
            error = $"Invalid user '{raw}': must match [a-z_][a-z0-9_-]{{0,31}} (Linux username rules).";
            return null;
        }
        if (Reserved.Contains(user))
        {
            error = $"User '{user}' is reserved.";
            return null;
        }
        return user;
    }

    /// <summary>
    /// Validates an existing guest account name given with <c>--guest-user</c>. It is only ever
    /// passed to the agent's set-user-password call, so Windows-style names (spaces, mixed
    /// case) are allowed; control characters, ':' and a leading '-' are not.
    /// </summary>
    public static bool IsValidGuestAccount(string account) =>
        account.Length is > 0 and <= 64 &&
        account.Trim() == account &&
        !account.StartsWith('-') &&
        !account.Any(c => char.IsControl(c) || c == ':') &&
        !Reserved.Contains(account.ToLowerInvariant());

    /// <summary>
    /// Normalizes a deployment name to a lowercase DNS-label fragment of at most
    /// <see cref="MaxDeploymentNameLength"/> characters. Returns null if nothing usable remains.
    /// </summary>
    public static string? NormalizeDeploymentName(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var label = SanitizeDnsLabel(raw, MaxDeploymentNameLength);
        return label.Length == 0 ? null : label;
    }

    /// <summary>The resource pool holding a deployment's VMs.</summary>
    public static string PoolId(string deployment) => $"{Tag}-{deployment}";

    /// <summary>The VM name for one user: <c>hotlab-&lt;deployment&gt;-&lt;user&gt;</c>, as a valid DNS label.</summary>
    public static string VmName(string deployment, string user)
    {
        var name = SanitizeDnsLabel($"{Tag}-{deployment}-{user}", MaxDnsLabelLength);
        return name.Length == 0 ? Tag : name;
    }

    /// <summary>
    /// Lowercases and maps anything outside [a-z0-9-] to '-', collapses runs of '-', trims
    /// leading/trailing '-' and truncates to <paramref name="maxLength"/>.
    /// </summary>
    public static string SanitizeDnsLabel(string raw, int maxLength = MaxDnsLabelLength)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim().ToLowerInvariant())
        {
            char c = ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? ch : '-';
            if (c == '-' && (sb.Length == 0 || sb[^1] == '-'))
                continue;
            sb.Append(c);
        }
        var label = sb.ToString();
        if (label.Length > maxLength)
            label = label[..maxLength];
        return label.Trim('-');
    }

    /// <summary>A random <see cref="PasswordLength"/>-character password from <see cref="PasswordAlphabet"/>.</summary>
    public static string GeneratePassword() =>
        RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxUserRegex();
}
