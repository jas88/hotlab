using Hotlab.Gateway.Deploy;

namespace Hotlab.Gateway.Tests.Deploy;

public sealed class DeployArgsTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly string[] Required =
        ["--source", "9000", "--node", "pve1", "--address", "gw.example:3389", "--out", "out"];

    [Fact]
    public void Parses_all_options()
    {
        var request = DeployArgs.Parse(
            [.. Required, "--name", "Net 101", "--guest-user", "student", "--lifetime", "120d", "--leave-running",
             "--force", "--parallel", "8", "--users-file-target", "/etc/hotlab/users.json", "Alice", "bob"],
            out var error);

        Assert.Equal("", error);
        Assert.NotNull(request);
        Assert.Equal(9000, request.SourceVmId);
        Assert.Equal("pve1", request.Node);
        Assert.Equal("gw.example:3389", request.Address);
        Assert.Equal("out", request.OutDir);
        Assert.Equal("Net 101", request.Name);
        Assert.Equal("student", request.GuestUser);
        Assert.Equal(TimeSpan.FromDays(120), request.Lifetime);
        Assert.True(request.LeaveRunning);
        Assert.True(request.Force);
        Assert.Equal(8, request.Parallel);
        Assert.Equal("/etc/hotlab/users.json", request.UsersFileTarget);
        Assert.Equal(["alice", "bob"], request.Users);
    }

    [Fact]
    public void Rejects_users_whose_vm_names_collide()
    {
        var request = DeployArgs.Parse([.. Required, "a_b", "a-b"], out var error);

        Assert.Null(request);
        Assert.Contains("same VM name", error);
    }

    [Fact]
    public void Defaults()
    {
        var request = DeployArgs.Parse([.. Required, "alice"], out _)!;

        Assert.Null(request.GuestUser);
        Assert.Null(request.Lifetime);
        Assert.Equal(4, request.Parallel);
        Assert.False(request.LeaveRunning);
    }

    [Fact]
    public void Reads_users_file_with_comments_and_dedupes()
    {
        var file = _temp.Combine("users.txt");
        File.WriteAllText(file, "# class of 2026\nalice\n\n  Bob  \n#carol\nalice\n");

        var request = DeployArgs.Parse([.. Required, "--users-file", file, "dave"], out var error);

        Assert.Equal("", error);
        Assert.Equal(["dave", "alice", "bob"], request!.Users);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("alice")] // missing required options
    [InlineData("--source", "x", "--node", "pve1", "--address", "gw", "--out", "o", "alice")]
    [InlineData("--source", "99", "--node", "pve1", "--address", "gw", "--out", "o", "alice")]
    [InlineData("--source", "9000", "--node", "pve/1", "--address", "gw", "--out", "o", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw host", "--out", "o", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o")] // no users
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "9lives")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "root")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "a:b")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--parallel", "0", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--lifetime", "soon", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--guest-user", "root", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--name", "---", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--bogus", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--users-file", "/nonexistent/u.txt")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--users-file", "", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--users-file", "a\0b", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--users-file-target", "", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "--name", "abcdefghijabcdefghijabcdefghijabc", "alice")]
    [InlineData("--source", "9000", "--node", "pve1", "--address", "gw", "--out", "o", "\u212Aelvin")]
    public void Rejects(params string[] args)
    {
        Assert.Null(DeployArgs.Parse(args, out var error));
        if (!args.Contains("--help"))
            Assert.NotEqual("", error);
    }
}
