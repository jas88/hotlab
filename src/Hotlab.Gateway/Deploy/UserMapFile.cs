using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// The gateway's users file (<c>Hotlab:UsersFile</c>): a JSON configuration file whose
/// <c>Hotlab:Users</c> section maps users to VMs. Edits preserve everything else in the file
/// and are written atomically, so the gateway's reload-on-change never sees a partial file.
/// Read-modify-write edits hold an exclusive lock on <c>&lt;path&gt;.lock</c>, so concurrent
/// deploy/teardown runs (in any process) cannot lose each other's changes.
/// </summary>
public static class UserMapFile
{
    /// <summary>Mode of a newly created users file (0640).</summary>
    public const UnixFileMode NewFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>How long <see cref="Merge"/> and <see cref="Remove"/> wait for another writer's lock.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the user → VMID map (case-insensitive keys); empty when the file is missing.
    /// Malformed entries (no parseable VmId) are left out; see <see cref="FindMalformed"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object.</exception>
    public static Dictionary<string, int> ReadMappings(string path) => ReadMappings(Load(path));

    /// <summary>
    /// Those of <paramref name="users"/> whose entry exists but has no parseable VmId. They are
    /// not conflicts: a merge simply overwrites them.
    /// </summary>
    public static IReadOnlyList<string> FindMalformed(string path, IEnumerable<string> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (Load(path) is not { } root || UsersObject(root, create: false) is not { } map)
            return [];
        var valid = ReadMappings(root);
        return users.Where(u => Property(map, u) is not null && !valid.ContainsKey(u)).ToList();
    }

    /// <summary>
    /// Users that the file already maps to a VM other than one of the VMIDs this deployment
    /// owns for them (<paramref name="owned"/>; a user absent there owns nothing).
    /// </summary>
    public static IReadOnlyList<string> FindConflicts(
        string path, IEnumerable<string> users, IReadOnlyDictionary<string, IReadOnlySet<int>> owned)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(owned);
        var existing = ReadMappings(path);
        return users
            .Where(u => existing.TryGetValue(u, out int vmId) &&
                !(owned.TryGetValue(u, out var mine) && mine.Contains(vmId)))
            .ToList();
    }

    /// <summary>Adds or replaces mappings (atomic write; creates the file if needed).</summary>
    public static void Merge(string path, IEnumerable<UserMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        using var _ = AcquireLock(path);
        var root = Load(path) ?? new JsonObject();
        var users = UsersObject(root, create: true)!;
        foreach (var m in mappings)
        {
            RemoveKey(users, m.User);
            users[m.User] = new JsonObject { ["Node"] = m.Node, ["VmId"] = m.VmId };
        }
        Save(path, root);
    }

    /// <summary>
    /// Removes mappings, but only where the file still maps the user to the given VMID (a
    /// user remapped elsewhere since is left alone). Returns how many were removed.
    /// </summary>
    public static int Remove(string path, IEnumerable<(string User, int VmId)> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (!File.Exists(path))
            return 0;
        using var _ = AcquireLock(path);
        if (Load(path) is not { } root || UsersObject(root, create: false) is not { } users)
            return 0;

        var current = ReadMappings(root);
        int removed = 0;
        foreach (var (user, vmId) in mappings)
        {
            if (current.TryGetValue(user, out int mapped) && mapped == vmId && RemoveKey(users, user))
                removed++;
        }
        if (removed > 0)
            Save(path, root);
        return removed;
    }

    private static Dictionary<string, int> ReadMappings(JsonObject? root)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root is null || UsersObject(root, create: false) is not { } users)
            return result;

        foreach (var (user, entry) in users)
        {
            var vmId = entry is JsonObject o ? Property(o, "VmId") : null;
            if (vmId is JsonValue v && TryGetInt(v, out int id))
                result[user] = id;
        }
        return result;
    }

    /// <summary>
    /// Takes an exclusive lock on <c>&lt;path&gt;.lock</c> (an OS file lock, released if the process
    /// dies), retrying with backoff up to <see cref="LockTimeout"/>. The lock file is left in place.
    /// </summary>
    /// <exception cref="IOException">The lock could not be taken in time.</exception>
    private static FileStream AcquireLock(string path)
    {
        var lockPath = Path.GetFullPath(path) + ".lock";
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = NewFileMode;

        var deadline = DateTime.UtcNow + LockTimeout;
        var delay = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 500));
            }
            catch (IOException ex)
            {
                throw new IOException($"Timed out after {LockTimeout} waiting for {lockPath} (another deploy or teardown?).", ex);
            }
        }
    }

    private static JsonObject? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject
                ?? throw new InvalidDataException($"{path} must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex);
        }
    }

    private static void Save(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // Keep an existing file's mode; a new one is 0640 so a gateway running as a different
        // user in the owner's group can read it. It holds no secrets.
        UnixFileMode? mode = File.Exists(path) ? null : NewFileMode;
        AtomicFile.WriteAllText(path, root.ToJsonString(DeploymentManifest.JsonOptions) + "\n", mode);
    }

    /// <summary>The Hotlab → Users object, matched case-insensitively like configuration keys.</summary>
    private static JsonObject? UsersObject(JsonObject root, bool create)
    {
        var hotlab = Property(root, "Hotlab") as JsonObject;
        if (hotlab is null)
        {
            if (!create)
                return null;
            RemoveKey(root, "Hotlab");
            root["Hotlab"] = hotlab = new JsonObject();
        }

        var users = Property(hotlab, "Users") as JsonObject;
        if (users is null && create)
        {
            RemoveKey(hotlab, "Users");
            hotlab["Users"] = users = new JsonObject();
        }
        return users;
    }

    private static JsonNode? Property(JsonObject obj, string name) =>
        obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool RemoveKey(JsonObject obj, string name)
    {
        var keys = obj.Select(kv => kv.Key)
            .Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in keys)
            obj.Remove(key);
        return keys.Count > 0;
    }

    private static bool TryGetInt(JsonValue value, out int result)
    {
        if (value.TryGetValue(out result))
            return true;
        return value.TryGetValue(out string? text) &&
            int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}
