using Hotlab.Gateway.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Routing;

public sealed class CookieResolutionTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly ConfigUserTargetResolver _resolver;

    public CookieResolutionTests()
    {
        var values = new Dictionary<string, string?>();
        void Add(string user, int vmId)
        {
            values[$"Hotlab:Users:{user}:Node"] = "pve1";
            values[$"Hotlab:Users:{user}:VmId"] = vmId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Add("JohnSmith", 101);
        Add("jo", 102);
        Add("annabelle", 103);
        Add("annabella", 104);
        Add("bob", 105);
        Add("bobby-tables", 106);

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<UserMapOptions>(config.GetSection(UserMapOptions.SectionName));
        _sp = services.BuildServiceProvider();
        _resolver = new ConfigUserTargetResolver(
            _sp.GetRequiredService<IOptionsMonitor<UserMapOptions>>(),
            NullLogger<ConfigUserTargetResolver>.Instance);
    }

    public void Dispose() => _sp.Dispose();

    [Theory]
    [InlineData("johnsmith", "johnsmith", 101)]
    [InlineData("CORP\\JohnSmith", "johnsmith", 101)]
    [InlineData("johnsmith@corp.example", "johnsmith", 101)]
    [InlineData("bob", "bob", 105)]
    public void ExactMatch_Wins(string cookie, string user, int vmId)
    {
        var r = _resolver.ResolveCookie(cookie);
        Assert.Equal(CookieMatch.Exact, r.Match);
        Assert.Equal(user, r.User);
        Assert.Equal(vmId, r.Target!.VmId);
    }

    [Fact]
    public void ExactMatch_PreferredOverPrefix_ForTruncatedCookie()
    {
        // "domain\jo" is 9 chars and "jo" is itself configured: exact match wins.
        var r = _resolver.ResolveCookie("domain\\jo");
        Assert.Equal(CookieMatch.Exact, r.Match);
        Assert.Equal(102, r.Target!.VmId);
    }

    [Fact]
    public void TruncatedCookie_UniquePrefix_Resolves()
    {
        var r = _resolver.ResolveCookie("corp\\john"); // 9 chars
        Assert.Equal(CookieMatch.UniquePrefix, r.Match);
        Assert.Equal("johnsmith", r.User);
        Assert.Equal(101, r.Target!.VmId);
    }

    [Fact]
    public void TruncatedCookie_NoDomain_UniquePrefix_Resolves()
    {
        var r = _resolver.ResolveCookie("bobby-tab"); // 9 chars, no domain
        Assert.Equal(CookieMatch.UniquePrefix, r.Match);
        Assert.Equal(106, r.Target!.VmId);
    }

    [Fact]
    public void TruncatedCookie_AmbiguousPrefix_Rejected()
    {
        var r = _resolver.ResolveCookie("x\\annabel"); // 9 chars; annabelle and annabella
        Assert.Equal(CookieMatch.AmbiguousPrefix, r.Match);
        Assert.Null(r.Target);
    }

    [Fact]
    public void TruncatedCookie_NoCandidate_NotFound()
    {
        var r = _resolver.ResolveCookie("corp\\zed1"); // 9 chars
        Assert.Equal(CookieMatch.NotFound, r.Match);
        Assert.Null(r.Target);
    }

    [Theory]
    [InlineData("corp\\joh")]   // 8 chars: not mstsc-truncated, so no prefix match
    [InlineData("johnsmit")]    // 8 chars
    [InlineData("corp\\johnsm")] // 11 chars
    public void NonTruncatedLength_DoesNotPrefixMatch(string cookie)
    {
        var r = _resolver.ResolveCookie(cookie);
        Assert.Equal(CookieMatch.NotFound, r.Match);
        Assert.Null(r.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corp\\")]
    public void NoUsableUser_IsNoUser(string? cookie) =>
        Assert.Equal(CookieMatch.NoUser, _resolver.ResolveCookie(cookie).Match);

    [Fact]
    public void DefaultInterfaceImplementation_IsExactOnly()
    {
        IUserTargetResolver exactOnly = new SingleUserResolver();
        Assert.Equal(CookieMatch.Exact, exactOnly.ResolveCookie("CORP\\Alice").Match);
        Assert.Equal(CookieMatch.NotFound, exactOnly.ResolveCookie("corp\\ali").Match);
        Assert.Equal(CookieMatch.NoUser, exactOnly.ResolveCookie(null).Match);
    }

    private sealed class SingleUserResolver : IUserTargetResolver
    {
        public VmTarget? Resolve(string username) =>
            username == "alice" ? new VmTarget("pve1", 1) : null;
    }
}
