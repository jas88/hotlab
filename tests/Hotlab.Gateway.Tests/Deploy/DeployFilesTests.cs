using Hotlab.Gateway.Deploy;
using Hotlab.Gateway.Tokens;
using static Hotlab.Gateway.Tests.Deploy.DeployTestSupport;

namespace Hotlab.Gateway.Tests.Deploy;

/// <summary>Users file merging, CSV round trips, atomic writes and the .rdp renderer.</summary>
public sealed class DeployFilesTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Merge_creates_file_and_matches_keys_case_insensitively()
    {
        var path = _temp.Combine("sub", "users.json");
        UserMapFile.Merge(path, [new UserMapping("alice", "pve1", 200)]);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Hotlab\"", "\"hotlab\"", StringComparison.Ordinal)
            .Replace("\"alice\"", "\"ALICE\"", StringComparison.Ordinal));

        UserMapFile.Merge(path, [new UserMapping("alice", "pve1", 201), new UserMapping("bob", "pve1", 202)]);

        var map = UserMapFile.ReadMappings(path);
        Assert.Equal(2, map.Count);
        Assert.Equal(201, map["alice"]);
        Assert.DoesNotContain("ALICE", File.ReadAllText(path), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UserMapFile.NewFileMode, Mode(path));
    }

    [Fact]
    public void Merge_keeps_existing_file_mode_and_tolerates_comments()
    {
        var path = _temp.Combine("users.json");
        File.WriteAllText(path, "// managed by hotlab\n{\"Hotlab\":{\"Users\":{}},}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        UserMapFile.Merge(path, [new UserMapping("alice", "pve1", 200)]);

        Assert.Equal(200, UserMapFile.ReadMappings(path)["alice"]);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead, Mode(path));
    }

    [Fact]
    public void FindConflicts_ignores_owned_and_unmapped_users()
    {
        var path = _temp.Combine("users.json");
        UserMapFile.Merge(path, [new UserMapping("alice", "pve1", 200), new UserMapping("bob", "pve1", 300)]);
        var owned = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new HashSet<int> { 200 },
            ["bob"] = new HashSet<int> { 201 },
        };

        Assert.Equal(["bob"], UserMapFile.FindConflicts(path, ["alice", "bob", "carol"], owned));
        Assert.Empty(UserMapFile.FindConflicts(_temp.Combine("missing.json"), ["alice"], owned));
    }

    [Fact]
    public void Invalid_users_file_is_reported()
    {
        var path = _temp.Combine("users.json");
        File.WriteAllText(path, "[1,2]");
        Assert.Throws<InvalidDataException>(() => UserMapFile.ReadMappings(path));
    }

    [Fact]
    public void Passwords_csv_round_trips_quoted_fields()
    {
        var rows = new[]
        {
            new PasswordRow("bob", "Lab, \"User\"", "Abc23456789xyzAB", 201, "bob.rdp"),
            new PasswordRow("alice", "alice", "Zxc23456789xyzAB", 200, "alice.rdp"),
        };

        DeploymentOutputs.WritePasswords(_temp.Path, rows);
        var read = DeploymentOutputs.ReadPasswords(_temp.Path);

        Assert.Equal(rows[0], read["bob"]);
        Assert.Equal(rows[1], read["alice"]);
        Assert.StartsWith("user,guest_account,password,vmid,rdp_file\nalice,", File.ReadAllText(_temp.Combine("passwords.csv")), StringComparison.Ordinal);

        DeploymentOutputs.WritePasswords(_temp.Path, []);
        Assert.False(File.Exists(_temp.Combine("passwords.csv")));
    }

    [Fact]
    public void AtomicFile_replaces_content_without_leaving_temp_files()
    {
        var path = _temp.Combine("f.txt");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_temp.Path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(AtomicFile.PrivateFile, Mode(path));
    }

    [Fact]
    public void RdpFileRenderer_renders_and_rejects_line_breaks()
    {
        Assert.Equal(
            "full address:s:gw:3389\r\nusername:s:student\r\nloadbalanceinfo:s:hotlab:v1:x\r\n",
            RdpFileRenderer.Render("gw:3389", "student", "hotlab:v1:x"));
        Assert.Throws<ArgumentException>(() => RdpFileRenderer.Render("gw", "a\r\nb", "t"));
    }
}
