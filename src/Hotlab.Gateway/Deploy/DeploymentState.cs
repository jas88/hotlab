namespace Hotlab.Gateway.Deploy;

/// <summary>
/// The in-progress deployment's manifest and password rows, persisted after every change
/// (manifest and passwords.csv rewritten atomically under a lock), so an interrupted run
/// leaves an accurate record for re-runs and teardown.
/// </summary>
internal sealed class DeploymentState
{
    private readonly Lock _lock = new();
    private readonly string _dir;
    private readonly TimeProvider _time;

    public DeploymentState(string dir, DeploymentManifest manifest, Dictionary<string, PasswordRow> passwords, TimeProvider time)
    {
        _dir = dir;
        Manifest = manifest;
        Passwords = passwords;
        _time = time;
    }

    public DeploymentManifest Manifest { get; }

    /// <summary>The output directory.</summary>
    public string OutDir => _dir;

    public Dictionary<string, PasswordRow> Passwords { get; }

    public DeployedUser GetOrAdd(string user)
    {
        lock (_lock)
        {
            if (Manifest.Find(user) is { } row)
                return row;
            row = new DeployedUser { User = user, Updated = _time.GetUtcNow() };
            Manifest.Users.Add(row);
            return row;
        }
    }

    public void Update(DeployedUser row, Action<DeployedUser> change)
    {
        lock (_lock)
        {
            change(row);
            row.Updated = _time.GetUtcNow();
            SaveLocked();
        }
    }

    /// <summary>Marks a row failed and drops its now-stale password and .rdp file.</summary>
    public void MarkFailed(DeployedUser row, string error)
    {
        lock (_lock)
        {
            row.Status = DeployStatus.Failed;
            row.Error = error;
            row.Updated = _time.GetUtcNow();
            if (Passwords.Remove(row.User))
                DeploymentOutputs.WritePasswords(_dir, Passwords.Values);
            File.Delete(Path.Combine(_dir, DeploymentOutputs.RdpFileName(row.User)));
            SaveLocked();
        }
    }

    /// <summary>Writes the .rdp file and password row, then marks the row deployed.</summary>
    public void RecordDeployed(DeployedUser row, PasswordRow password, string rdpContent)
    {
        lock (_lock)
        {
            DeploymentOutputs.WriteRdp(_dir, row.User, rdpContent);
            Passwords[row.User] = password;
            DeploymentOutputs.WritePasswords(_dir, Passwords.Values);
            row.Status = DeployStatus.Deployed;
            row.Error = null;
            row.Updated = _time.GetUtcNow();
            SaveLocked();
        }
    }

    public void Save()
    {
        lock (_lock)
            SaveLocked();
    }

    private void SaveLocked()
    {
        Manifest.Updated = _time.GetUtcNow();
        Manifest.Save(_dir);
    }
}

/// <summary>Serialized progress output for concurrent deployment steps. Never pass secrets.</summary>
internal sealed class DeployLog(TextWriter stdout, TextWriter stderr)
{
    private readonly Lock _lock = new();

    public void Info(string message)
    {
        lock (_lock)
            stdout.WriteLine(message);
    }

    public void Error(string message)
    {
        lock (_lock)
            stderr.WriteLine(message);
    }
}
