using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hotlab.Gateway.Deploy;

/// <summary>
/// The gateway's users file (<c>Hotlab:UsersFile</c>): a JSON configuration file whose
/// <c>Hotlab:Users</c> section maps users to VMs. Edits preserve everything else in the file
/// and are written atomically, so the gateway's reload-on-change never sees a partial file.
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

    /// <summary>Reads the user → VMID map (case-insensitive keys); empty when the file is missing.</summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object.</exception>
    public static Dictionary<string, int> ReadMappings(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Load(path) is not { } root || UsersObject(root, create: false) is not { } users)
            return result;

        foreach (var (user, entry) in users)
        {
            var vmId = entry is JsonObject o ? Property(o, "VmId") : null;
            result[user] = vmId is JsonValue v && TryGetInt(v, out int id) ? id : 0;
        }
        return result;
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
        if (Load(path) is not { } root || UsersObject(root, create: false) is not { } users)
            return 0;

        var current = ReadMappings(path);
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
