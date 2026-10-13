using System.Net.Sockets;
using Hotlab.Gateway.Routing;
using Hotlab.Gateway.Tests.Tokens;
using static Hotlab.Gateway.Tests.Relay.RelayHarness;

namespace Hotlab.Gateway.Tests.Relay;

/// <summary>End-to-end routing on signed Hotlab routing tokens (.rdp loadbalanceinfo).</summary>
public sealed class RdpRelayTokenRoutingTests
{
    private static readonly VmTarget Alice = new("pve1", 101);
    private static readonly VmTarget JohnSmith = new("pve2", 202);

    [Fact]
    public async Task ValidToken_WithoutCookie_RoutesToTokenUser()
    {
        await using var h = await StartAsync();
        var token = h.Tokens.Mint("johnsmith"); // longer than mstsc's 9-char cookie limit
        var cr = BuildConnectionRequest(user: null, routingToken: token);

        using var client = await h.ConnectClientAsync();
        await SendAllAsync(client, cr);

        using var backend = await h.AcceptBackendAsync();
        Assert.Equal(cr, await ReadExactlyAsync(backend, cr.Length)); // token forwarded verbatim
        Assert.Equal(JohnSmith, h.Activator.LastTarget);
        AssertLogsRedacted(h, token);
        Assert.Contains(h.HandlerLog.Entries, e => e.Contains("valid Hotlab token for user johnsmith", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidToken_BeatsConflictingCookie()
    {
        await using var h = await StartAsync();
        var token = h.Tokens.Mint("alice");
        var cr = BuildConnectionRequest(user: "johnsmith", routingToken: token);

        using var client = await h.ConnectClientAsync();
        await SendAllAsync(client, cr);

        using var backend = await h.AcceptBackendAsync();
        Assert.Equal(cr, await ReadExactlyAsync(backend, cr.Length));
        Assert.Equal(Alice, h.Activator.LastTarget);
        AssertLogsRedacted(h, token);
    }

    [Theory]
    [InlineData("bad signature")]
    [InlineData("tampered user")]
    [InlineData("unknown version")]
    [InlineData("malformed")]
    public async Task InvalidToken_IsRejected_EvenWithValidCookie(string kind)
    {
        await using var h = await StartAsync();
        var good = h.Tokens.Mint("johnsmith");
        var sig = TokenTestSupport.SignatureOf(good);
        var token = kind switch
        {
            "bad signature" => good[..^sig.Length] + new string('A', sig.Length),
            "tampered user" => good.Replace(":johnsmith:", ":alice:", StringComparison.Ordinal),
            "unknown version" => good.Replace("hotlab:v1:", "hotlab:v9:", StringComparison.Ordinal),
            _ => "hotlab:v1:alice",
        };

        using var client = await h.ConnectClientAsync();
        await SendAllAsync(client, BuildConnectionRequest(user: "alice", routingToken: token));

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
        AssertLogsRedacted(h, token);
        Assert.Contains(h.HandlerLog.Entries, e => e.Contains("invalid Hotlab token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidTokenForUnmappedUser_IsRejected()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectClientAsync();
        await SendAllAsync(client, BuildConnectionRequest(user: "alice", routingToken: h.Tokens.Mint("mallory")));

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
    }

    [Fact]
    public async Task TsvToken_StillUsesCookiePath()
    {
        await using var h = await StartAsync();
        const string tsv = "tsv://MS Terminal Services Plugin.1.A-Very-Long-Collection-Name-Secret";
        var cr = BuildConnectionRequest(user: "alice", routingToken: tsv);

        using var client = await h.ConnectClientAsync();
        await SendAllAsync(client, cr);

        using var backend = await h.AcceptBackendAsync();
        Assert.Equal(cr, await ReadExactlyAsync(backend, cr.Length));
        Assert.Equal(Alice, h.Activator.LastTarget);
        Assert.DoesNotContain(h.HandlerLog.Entries, e => e.Contains("Name-Secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TsvToken_WithoutCookie_IsRejected()
    {
        await using var h = await StartAsync();
        using var client = await h.ConnectClientAsync();
        await SendAllAsync(client, BuildConnectionRequest(user: null, routingToken: "tsv://MS Terminal Services Plugin.1.Coll"));

        await AssertClosedAsync(client);
        Assert.Equal(0, h.Activator.Calls);
    }

    /// <summary>Neither the signature nor the whole token may appear in any log entry.</summary>
    private static void AssertLogsRedacted(RelayHarness h, string token)
    {
        var sig = TokenTestSupport.SignatureOf(token);
        Assert.NotEmpty(h.HandlerLog.Entries);
        foreach (var entry in h.HandlerLog.Entries)
        {
            Assert.DoesNotContain(token, entry, StringComparison.Ordinal);
            if (token.Split(':').Length == 5)
                Assert.DoesNotContain(sig, entry, StringComparison.Ordinal);
        }
    }
}
