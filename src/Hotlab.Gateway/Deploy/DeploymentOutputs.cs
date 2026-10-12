using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hotlab.Gateway.Deploy;

/// <summary>A row of <c>passwords.csv</c>.</summary>
public sealed record PasswordRow(string User, string GuestAccount, string Password, int VmId, string RdpFile);

/// <summary>A user → VM mapping as the gateway's <c>Hotlab:Users</c> map stores it.</summary>
public sealed record UserMapping(string User, string Node, int VmId);

/// <summary>Reads and writes the per-deployment output files (all 0600, written atomically).</summary>
public static class DeploymentOutputs
{
    public const string PasswordsFile = "passwords.csv";
    public const string UsersJsonFile = "users.json";
    private const string PasswordsHeader = "user,guest_account,password,vmid,rdp_file";

    /// <summary>The .rdp file name for a user.</summary>
    public static string RdpFileName(string user) => $"{user}.rdp";

    /// <summary>Writes a user's .rdp file.</summary>
    public static void WriteRdp(string dir, string user, string content) =>
        AtomicFile.WriteAllText(Path.Combine(dir, RdpFileName(user)), content, AtomicFile.PrivateFile);

    /// <summary>Reads <c>passwords.csv</c>, keyed by user (case-insensitive); empty if missing.</summary>
    public static Dictionary<string, PasswordRow> ReadPasswords(string dir)
    {
        var rows = new Dictionary<string, PasswordRow>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(dir, PasswordsFile);
        if (!File.Exists(path))
            return rows;

        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (line.Length == 0)
                continue;
            var f = ParseCsvLine(line);
            if (f.Count != 5 || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int vmId))
                throw new InvalidDataException($"{path}: malformed row.");
            rows[f[0]] = new PasswordRow(f[0], f[1], f[2], vmId, f[4]);
        }
        return rows;
    }

    /// <summary>Rewrites <c>passwords.csv</c> (sorted by user); deletes it when there are no rows.</summary>
    public static void WritePasswords(string dir, IEnumerable<PasswordRow> rows)
    {
        var path = Path.Combine(dir, PasswordsFile);
        var list = rows.OrderBy(r => r.User, StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            File.Delete(path);
            return;
        }

        var sb = new StringBuilder(PasswordsHeader).Append('\n');
        foreach (var r in list)
        {
            sb.AppendJoin(',', new[]
            {
                Csv(r.User), Csv(r.GuestAccount), Csv(r.Password),
                r.VmId.ToString(CultureInfo.InvariantCulture), Csv(r.RdpFile),
            }).Append('\n');
        }
        AtomicFile.WriteAllText(path, sb.ToString(), AtomicFile.PrivateFile);
    }

    /// <summary>Renders a <c>{"Hotlab":{"Users":{...}}}</c> configuration fragment.</summary>
    public static string RenderUsersJson(IEnumerable<UserMapping> mappings)
    {
        var users = new JsonObject();
        foreach (var m in mappings.OrderBy(m => m.User, StringComparer.Ordinal))
            users[m.User] = new JsonObject { ["Node"] = m.Node, ["VmId"] = m.VmId };
        var root = new JsonObject { ["Hotlab"] = new JsonObject { ["Users"] = users } };
        return root.ToJsonString(DeploymentManifest.JsonOptions) + "\n";
    }

    /// <summary>Writes <c>users.json</c>.</summary>
    public static void WriteUsersJson(string dir, IEnumerable<UserMapping> mappings) =>
        AtomicFile.WriteAllText(Path.Combine(dir, UsersJsonFile), RenderUsersJson(mappings), AtomicFile.PrivateFile);

    /// <summary>
    /// True when two full paths name the same file (case-insensitively on Windows and macOS,
    /// whose default file systems are).
    /// </summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Quotes a field when needed. Line breaks are refused rather than quoted: no valid field
    /// contains one (users, accounts, passwords and file names are all validated), and
    /// <see cref="ReadPasswords"/> reads one row per line.
    /// </summary>
    /// <exception cref="ArgumentException">The value contains CR or LF.</exception>
    private static string Csv(string value)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("passwords.csv fields must not contain line breaks.", nameof(value));
        return value.AsSpan().IndexOfAny(",\"") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
