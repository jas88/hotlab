using Hotlab.Gateway.Tokens;
using Microsoft.Extensions.Configuration;
using static Hotlab.Gateway.Tests.Tokens.TokenTestSupport;

namespace Hotlab.Gateway.Tests.Tokens;

public sealed class MintRdpCommandTests
{
    private readonly ManualTimeProvider _time = new(Epoch);

    private static IConfiguration Config(string? key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hotlab:Tokens:SigningKey"] = key,
            ["Hotlab:Tokens:DefaultLifetime"] = "02:00:00",
        }).Build();

    private (int Code, string Out, string Err) Run(IConfiguration config, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = MintRdpCommand.Run(args, config, stdout, stderr, _time);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void PrintsRdpFile_WithValidatingToken()
    {
        var (code, output, err) = Run(Config(Key), "Alice", "--address", "lab.example.org:3389", "--lifetime", "30m");
        Assert.Equal(0, code);
        Assert.Equal("", err);

        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("full address:s:lab.example.org:3389", lines[0]);
        Assert.Equal("username:s:alice", lines[1]);
        Assert.StartsWith("loadbalanceinfo:s:hotlab:v1:alice:", lines[2], StringComparison.Ordinal);

        var token = lines[2]["loadbalanceinfo:s:".Length..];
        var result = Create(_time).Validate(token);
        Assert.Equal(TokenStatus.Valid, result.Status);
        Assert.Equal("alice", result.User);
        Assert.Equal(Epoch.AddMinutes(30), result.Expiry);
    }

    [Fact]
    public void UsesConfiguredDefaultLifetime()
    {
        var (code, output, _) = Run(Config(Key), "bob", "--address", "gw");
        Assert.Equal(0, code);
        var token = output.Split("\r\n")[2]["loadbalanceinfo:s:".Length..];
        Assert.Equal(Epoch.AddHours(2), Create(_time).Validate(token).Expiry);
    }

    [Fact]
    public void NoKey_FailsWithConfigError()
    {
        var (code, output, err) = Run(Config(""), "alice", "--address", "gw");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("SigningKey", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("alice")]
    [InlineData("--address", "gw")]
    [InlineData("alice", "--address")]
    [InlineData("alice", "--address", "gw host")]
    [InlineData("alice", "bob", "--address", "gw")]
    [InlineData("alice", "--address", "gw", "--lifetime", "soon")]
    [InlineData("alice", "--address", "gw", "--lifetime", "-1h")]
    [InlineData("alice", "--address", "gw", "--bogus")]
    [InlineData("a:b", "--address", "gw")]
    [InlineData("--help")]
    public void UsageErrors_ReturnTwo(params string[] args)
    {
        var (code, output, err) = Run(Config(Key), args);
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("Usage:", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8h", 8 * 3600)]
    [InlineData("30m", 1800)]
    [InlineData("90s", 90)]
    [InlineData("1d", 86400)]
    [InlineData("1.5h", 5400)]
    [InlineData("08:00:00", 8 * 3600)]
    public void TryParseLifetime_Accepts(string text, int seconds)
    {
        Assert.True(MintRdpCommand.TryParseLifetime(text, out var lifetime));
        Assert.Equal(TimeSpan.FromSeconds(seconds), lifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("8x")]
    [InlineData("0h")]
    [InlineData("00:00:00")]
    public void TryParseLifetime_Rejects(string text) =>
        Assert.False(MintRdpCommand.TryParseLifetime(text, out _));
}
