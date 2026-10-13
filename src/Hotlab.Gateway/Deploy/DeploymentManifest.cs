using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotlab.Gateway.Deploy;

/// <summary>Per-user deployment states recorded in the manifest.</summary>
public static class DeployStatus
{
    public const string Cloning = "cloning";
    public const string Provisioning = "provisioning";
    public const string Deployed = "deployed";
    public const string Failed = "failed";
    public const string Destroyed = "destroyed";

    /// <summary>True for one of the statuses above.</summary>
    public static bool IsKnown(string? status) =>
        status is Cloning or Provisioning or Deployed or Failed or Destroyed;
}

/// <summary>
/// <c>deployment.json</c>: what a deployment created, used by re-runs (idempotency) and
/// teardown. Contains no secrets.
/// </summary>
public sealed class DeploymentManifest
{
    /// <summary>File name inside the output directory.</summary>
    public const string FileName = "deployment.json";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // [JsonRequired]: a property missing from the file must not silently keep its initializer
    // (a missing user "status" would otherwise read as "cloning", relaxing the ownership rule).
    public int Version { get; set; } = 1;
    [JsonRequired] public string Name { get; set; } = "";
    public int SourceVmId { get; set; }
    [JsonRequired] public string Node { get; set; } = "";
    [JsonRequired] public string Pool { get; set; } = "";

    /// <summary>Gateway address the .rdp files were last fully generated for.</summary>
    [JsonRequired] public string Address { get; set; } = "";

    /// <summary>
    /// Set while a forced --address change is incomplete: the new address, which becomes
    /// <see cref="Address"/> only once every existing .rdp file has been regenerated.
    /// </summary>
    public string? PendingAddress { get; set; }

    /// <summary>Gateway users file the mappings were merged into, if any (teardown removes them there).</summary>
    public string? UsersFileTarget { get; set; }

    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    [JsonRequired] public List<DeployedUser> Users { get; set; } = [];

    /// <summary>The row for <paramref name="user"/>, or null.</summary>
    public DeployedUser? Find(string user) =>
        Users.FirstOrDefault(u => string.Equals(u.User, user, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads the manifest from <paramref name="dir"/>, or null if there is none.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not a valid manifest.</exception>
    public static DeploymentManifest? Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
            return null;
        DeploymentManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a valid deployment manifest: {ex.Message}", ex);
        }
        if (manifest.Validate() is { } problem)
            throw new InvalidDataException($"{path} is not a valid deployment manifest: {problem}.");
        return manifest;
    }

    /// <summary>Describes the first structural problem, or null.</summary>
    private string? Validate()
    {
        // The serializer happily assigns JSON nulls to non-nullable properties, and missing
        // properties are rejected by [JsonRequired]; empty strings are rejected here.
        if (string.IsNullOrEmpty(Name) || string.IsNullOrEmpty(Node) || string.IsNullOrEmpty(Pool) ||
            string.IsNullOrEmpty(Address))
            return "\"name\", \"node\", \"pool\" and \"address\" must be non-empty strings";
        if (PendingAddress is { Length: 0 })
            return "\"pendingAddress\" must not be empty";
        if (UsersFileTarget is not null && !IsValidFullPath(UsersFileTarget))
            return "\"usersFileTarget\" is not a valid absolute path";
        if (Users is null)
            return "\"users\" must be a list";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Users.Count; i++)
        {
            var row = Users[i];
            if (row is null)
                return $"users[{i}] is null";
            if (string.IsNullOrEmpty(row.User) || string.IsNullOrEmpty(row.GuestAccount) || !DeployStatus.IsKnown(row.Status))
                return $"users[{i}] needs \"user\", \"guestAccount\" and a known \"status\"";
            // The user names files in the output directory (.rdp), so it must be exactly a
            // valid, normalized user name: no path separators or "..".
            if (DeployNames.NormalizeUser(row.User, out _) != row.User)
                return $"users[{i}].user is not a valid user name";
            if (!seen.Add(row.User))
                return $"users[{i}].user is a duplicate";
        }
        return null;
    }

    private static bool IsValidFullPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Writes the manifest atomically (0600).</summary>
    public void Save(string dir) =>
        AtomicFile.WriteAllText(
            Path.Combine(dir, FileName), JsonSerializer.Serialize(this, JsonOptions) + "\n", AtomicFile.PrivateFile);
}

/// <summary>One user's clone.</summary>
public sealed class DeployedUser
{
    [JsonRequired] public string User { get; set; } = "";
    [JsonRequired] public string GuestAccount { get; set; } = "";
    public int? VmId { get; set; }
    public string? VmName { get; set; }
    [JsonRequired] public string Status { get; set; } = DeployStatus.Cloning;

    /// <summary>
    /// The VM at <see cref="VmId"/> may be a clone this deployment started but never tagged
    /// (interrupted, or failed with its cleanup not completed). Until it is tagged or destroyed,
    /// it is recognised by pool and <see cref="VmName"/> alone, whatever <see cref="Status"/> says.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UntaggedClone { get; set; }

    public string? Error { get; set; }
    public DateTimeOffset Updated { get; set; }
}
