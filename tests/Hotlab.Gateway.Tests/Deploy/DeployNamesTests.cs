using Hotlab.Gateway.Deploy;

namespace Hotlab.Gateway.Tests.Deploy;

public sealed class DeployNamesTests
{
    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("  Alice ", "alice")]
    [InlineData("_svc", "_svc")]
    [InlineData("a-b_c9", "a-b_c9")]
    [InlineData("abcdefghijabcdefghijabcdefghijab", "abcdefghijabcdefghijabcdefghijab")] // 32 chars
    public void NormalizeUser_accepts(string raw, string expected) =>
        Assert.Equal(expected, DeployNames.NormalizeUser(raw, out _));

    [Theory]
    [InlineData("")]
    [InlineData("9lives")]
    [InlineData("-dash")]
    [InlineData("a.b")]
    [InlineData("a b")]
    [InlineData("DOMAIN\\alice")]
    [InlineData("abcdefghijabcdefghijabcdefghijabc")] // 33 chars
    [InlineData("root")]
    [InlineData("ROOT")]
    [InlineData("\u212Aelvin")] // Kelvin sign: lowercases to an ASCII 'k'
    [InlineData("caf\u00e9")]
    [InlineData("\u0130nci")] // dotted capital I
    public void NormalizeUser_rejects(string raw)
    {
        Assert.Null(DeployNames.NormalizeUser(raw, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("lab", "alice", "hotlab-lab-alice")]
    [InlineData("lab", "a_b", "hotlab-lab-a-b")]
    [InlineData("lab", "_x_", "hotlab-lab-x")]
    public void VmName_is_dns_label(string deployment, string user, string expected) =>
        Assert.Equal(expected, DeployNames.VmName(deployment, user));

    [Fact]
    public void VmName_is_capped_at_63_chars()
    {
        var name = DeployNames.VmName(new string('d', 32), new string('u', 32));
        Assert.True(name.Length <= 63);
        Assert.False(name.EndsWith('-'));
    }

    [Fact]
    public void Truncated_vm_names_stay_unique_and_stable()
    {
        var deployment = new string('d', 32);
        var a = DeployNames.VmName(deployment, new string('u', 31) + "a");
        var b = DeployNames.VmName(deployment, new string('u', 31) + "b");

        Assert.NotEqual(a, b);
        Assert.Equal(a, DeployNames.VmName(deployment, new string('u', 31) + "a"));
        Assert.All(new[] { a, b }, n => Assert.Matches("^hotlab-d+-u*-?[0-9a-f]{8}$", n));
        Assert.All(new[] { a, b }, n => Assert.Equal(63, n.Length));
        Assert.Equal("hotlab-lab-alice", DeployNames.VmName("lab", "alice")); // short names unchanged
    }

    [Theory]
    [InlineData("Net 101", "net-101")]
    [InlineData("  CS/2026 Spring ", "cs-2026-spring")]
    [InlineData("---", null)]
    [InlineData("abcdefghijabcdefghijabcdefghijab", "abcdefghijabcdefghijabcdefghijab")] // 32
    [InlineData("abcdefghijabcdefghijabcdefghijabc", null)] // 33: rejected, not truncated
    [InlineData("  abcdefghij abcdefghij abcdefghij  ", "abcdefghij-abcdefghij-abcdefghij")] // 32 after trimming
    [InlineData("abcdefghijk abcdefghijk abcdefghijk", null)] // 35
    public void NormalizeDeploymentName(string raw, string? expected) =>
        Assert.Equal(expected, DeployNames.NormalizeDeploymentName(raw));

    [Fact]
    public void PoolId_prefixes_hotlab() => Assert.Equal("hotlab-lab", DeployNames.PoolId("lab"));

    [Fact]
    public void Passwords_are_random_and_unambiguous()
    {
        var passwords = Enumerable.Range(0, 200).Select(_ => DeployNames.GeneratePassword()).ToList();

        Assert.Equal(200, passwords.Distinct().Count());
        Assert.All(passwords, p => Assert.Equal(16, p.Length));
        Assert.All(passwords, p => Assert.All(p, c => Assert.Contains(c, DeployNames.PasswordAlphabet)));
        Assert.DoesNotContain(DeployNames.PasswordAlphabet, c => "0O1lIio".Contains(c, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("student", true)]
    [InlineData("Lab User", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("-x", false)]
    [InlineData("a:b", false)]
    [InlineData("a\nb", false)]
    [InlineData("Root", false)]
    public void IsValidGuestAccount(string account, bool expected) =>
        Assert.Equal(expected, DeployNames.IsValidGuestAccount(account));
}
