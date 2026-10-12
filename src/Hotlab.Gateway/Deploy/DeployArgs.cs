using System.Globalization;
using Hotlab.Gateway.Tokens;

namespace Hotlab.Gateway.Deploy;

/// <summary>Command-line parsing for <c>deploy</c>.</summary>
public static class DeployArgs
{
    /// <summary>Usage text.</summary>
    public const string Usage =
        "Usage: Hotlab.Gateway deploy --source <vmid> --node <node> --address <gateway host[:port]> --out <dir>\n" +
        "         [--name <deployment>] [--guest-user <existing account>] [--lifetime <timespan>]\n" +
        "         [--leave-running] [--force] [--parallel <n>] [--users-file <path>]\n" +
        "         [--users-file-target <gateway users file>] [<user>...]\n" +
        "  Clones <vmid> once per user (linked clones if it is a template), creates a Linux account named\n" +
        "  after each user (or uses --guest-user), sets a random password, and writes <user>.rdp,\n" +
        "  passwords.csv, users.json and deployment.json to --out. Re-running skips deployed users.\n" +
        "  --users-file        one user per line; '#' starts a comment line\n" +
        "  --lifetime          routing-token lifetime, <n>d|h|m|s or a TimeSpan; default\n" +
        "                      Hotlab:Tokens:DefaultLifetime. For a lab used all term, use e.g. 120d.\n" +
        "  --parallel          clones provisioned at once (default 4; cloning itself is sequential)\n" +
        "  --users-file-target merge mappings into the gateway's Hotlab:UsersFile (refuses to remap\n" +
        "                      users mapped to other VMs unless --force); fixed once recorded\n" +
        "  --force             also allows changing --address on a re-run (regenerates every .rdp file)\n" +
        "  --name              deployment name (default: --out's directory name); at most 32 characters\n" +
        "                      once lowercased with anything but a-z, 0-9 turned into '-'";

    private const int MaxParallel = 64;

    /// <summary>Parses the arguments after the verb. Returns null with an error (empty for --help).</summary>
    public static DeployRequest? Parse(IReadOnlyList<string> args, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = "";
        int? source = null;
        int parallel = 4;
        string? node = null, address = null, outDir = null, name = null, guestUser = null, usersFile = null, target = null;
        TimeSpan? lifetime = null;
        bool leaveRunning = false, force = false;
        var rawUsers = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    return null;
                case "--leave-running":
                    leaveRunning = true;
                    continue;
                case "--force":
                    force = true;
                    continue;
                case "--source" or "--node" or "--address" or "--out" or "--name" or "--guest-user"
                    or "--lifetime" or "--parallel" or "--users-file" or "--users-file-target":
                    if (i + 1 >= args.Count)
                    {
                        error = $"{arg} requires a value.";
                        return null;
                    }
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unexpected argument '{arg}'.";
                        return null;
                    }
                    rawUsers.Add(arg);
                    continue;
            }

            var value = args[++i];
            switch (arg)
            {
                case "--source":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 100)
                        return Fail(out error, $"Invalid --source '{value}' (a VMID, 100 or more).");
                    source = id;
                    break;
                case "--node":
                    if (value.Length == 0 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
                        return Fail(out error, $"Invalid --node '{value}'.");
                    node = value;
                    break;
                case "--address":
                    if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                        return Fail(out error, "Invalid --address.");
                    address = value;
                    break;
                case "--out":
                    outDir = value;
                    break;
                case "--name":
                    if (DeployNames.NormalizeDeploymentName(value) is null)
                        return Fail(out error, $"Invalid --name '{value}' ({DeployNames.DeploymentNameRule}).");
                    name = value;
                    break;
                case "--guest-user":
                    if (!DeployNames.IsValidGuestAccount(value))
                        return Fail(out error, $"Invalid --guest-user '{value}'.");
                    guestUser = value;
                    break;
                case "--lifetime":
                    if (!MintRdpCommand.TryParseLifetime(value, out var life))
                        return Fail(out error, $"Invalid --lifetime '{value}'.");
                    lifetime = life;
                    break;
                case "--parallel":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parallel) ||
                        parallel is < 1 or > MaxParallel)
                        return Fail(out error, $"Invalid --parallel '{value}' (1-{MaxParallel}).");
                    break;
                case "--users-file":
                    if (string.IsNullOrWhiteSpace(value))
                        return Fail(out error, "--users-file requires a path.");
                    usersFile = value;
                    break;
                case "--users-file-target":
                    if (string.IsNullOrWhiteSpace(value))
                        return Fail(out error, "--users-file-target requires a path.");
                    target = value;
                    break;
            }
        }

        if (source is null || node is null || address is null || string.IsNullOrWhiteSpace(outDir))
            return Fail(out error, "--source, --node, --address and --out are required.");

        if (usersFile is not null)
        {
            try
            {
                rawUsers.AddRange(ReadUsersFile(usersFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
            {
                return Fail(out error, $"Cannot read --users-file: {ex.Message}");
            }
        }

        var users = new List<string>();
        foreach (var raw in rawUsers)
        {
            if (DeployNames.NormalizeUser(raw, out var userError) is not { } user)
                return Fail(out error, userError);
            if (users.Contains(user, StringComparer.Ordinal))
                continue;
            // VM names are DNS labels, so '_' becomes '-': a_b and a-b would share a name.
            var label = DeployNames.SanitizeDnsLabel(user);
            if (users.Find(u => DeployNames.SanitizeDnsLabel(u) == label) is { } clash)
                return Fail(out error, $"Users '{clash}' and '{user}' would get the same VM name.");
            users.Add(user);
        }
        if (users.Count == 0)
            return Fail(out error, "No users given.");

        return new DeployRequest
        {
            SourceVmId = source.Value,
            Node = node,
            Address = address,
            OutDir = outDir,
            Users = users,
            Name = name,
            GuestUser = guestUser,
            Lifetime = lifetime,
            LeaveRunning = leaveRunning,
            Force = force,
            Parallel = parallel,
            UsersFileTarget = target,
        };
    }

    /// <summary>Reads one user per line, skipping blank lines and lines starting with '#'.</summary>
    public static IEnumerable<string> ReadUsersFile(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    private static DeployRequest? Fail(out string error, string message)
    {
        error = message;
        return null;
    }
}
