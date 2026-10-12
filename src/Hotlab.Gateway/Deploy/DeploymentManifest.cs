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

    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public int SourceVmId { get; set; }
    public string Node { get; set; } = "";
    public string Pool { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>Gateway users file the mappings were merged into, if any (teardown removes them there).</summary>
    public string? UsersFileTarget { get; set; }

    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public List<DeployedUser> Users { get; set; } = [];

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
        try
        {
            return JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a valid deployment manifest: {ex.Message}", ex);
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
    public string User { get; set; } = "";
    public string GuestAccount { get; set; } = "";
    public int? VmId { get; set; }
    public string? VmName { get; set; }
    public string Status { get; set; } = DeployStatus.Cloning;
    public string? Error { get; set; }
    public DateTimeOffset Updated { get; set; }
}
