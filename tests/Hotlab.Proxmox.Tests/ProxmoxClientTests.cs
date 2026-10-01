using System.Net;

namespace Hotlab.Proxmox.Tests;

public class ProxmoxClientTests
{
    private static readonly Uri BaseUrl = new("https://pve.test:8006");

    private static (ProxmoxClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new ProxmoxClient(BaseUrl, "hotlab@pve!spike", "s3cret", handler);
        return (client, handler);
    }

    [Fact]
    public async Task Sends_PVEAPIToken_authorization_header()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"100\"");

        await client.GetNextVmIdAsync();

        Assert.Equal("PVEAPIToken=hotlab@pve!spike=s3cret", handler.Requests[0].AuthHeader);
    }

    [Fact]
    public async Task GetNextVmId_parses_string_data_envelope()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"105\"");

        var id = await client.GetNextVmIdAsync();

        Assert.Equal(105, id);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api2/json/cluster/nextid", handler.Requests[0].Path);
    }

    [Fact]
    public async Task CloneVm_posts_form_to_clone_endpoint_and_returns_upid()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:pve1:0001:clone:\"");

        var upid = await client.CloneVmAsync("pve1", 9000, 123, name: "spike-123", pool: "hotlab");

        Assert.Equal("UPID:pve1:0001:clone:", upid);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/9000/clone", request.Path);
        Assert.Contains("newid=123", request.Body);
        Assert.Contains("full=0", request.Body);
        Assert.Contains("name=spike-123", request.Body);
        Assert.Contains("pool=hotlab", request.Body);
    }

    [Fact]
    public async Task CloneVm_full_clone_sets_full_1()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:x\"");

        await client.CloneVmAsync("pve1", 9000, 124, fullClone: true);

        Assert.Contains("full=1", handler.Requests[0].Body);
    }

    [Fact]
    public async Task StartVm_posts_to_status_start()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:start\"");

        var upid = await client.StartVmAsync("pve1", 123);

        Assert.Equal("UPID:start", upid);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/status/start", handler.Requests[0].Path);
    }

    [Fact]
    public async Task StopVm_posts_to_status_stop()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:stop\"");

        await client.StopVmAsync("pve1", 123);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/status/stop", handler.Requests[0].Path);
    }

    [Fact]
    public async Task DestroyVm_sends_delete()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:destroy\"");

        var upid = await client.DestroyVmAsync("pve1", 123);

        Assert.Equal("UPID:destroy", upid);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123", handler.Requests[0].Path);
    }

    [Fact]
    public async Task WaitForTask_polls_until_stopped_and_returns_exitstatus()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""{"status":"running"}""");
        handler.EnqueueData("""{"status":"stopped","exitstatus":"OK"}""");

        var exit = await client.WaitForTaskAsync("pve1", "UPID:pve1:0001:clone:", TimeSpan.Zero);

        Assert.Equal("OK", exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api2/json/nodes/pve1/tasks/UPID%3Apve1%3A0001%3Aclone%3A/status", handler.Requests[0].Path);
    }

    [Fact]
    public async Task AgentPing_returns_false_on_error_and_true_on_success()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"data":null}""");
        handler.EnqueueData("{}");

        Assert.False(await client.AgentPingAsync("pve1", 123));
        Assert.True(await client.AgentPingAsync("pve1", 123));
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/agent/ping", handler.Requests[0].Path);
    }

    [Fact]
    public async Task AgentGetIpAddresses_returns_ipv4_skipping_loopback()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""
            {"result":[
              {"name":"lo","ip-addresses":[
                {"ip-address":"127.0.0.1","ip-address-type":"ipv4","prefix":8},
                {"ip-address":"::1","ip-address-type":"ipv6","prefix":128}]},
              {"name":"eth0","ip-addresses":[
                {"ip-address":"192.168.7.42","ip-address-type":"ipv4","prefix":24},
                {"ip-address":"fe80::1","ip-address-type":"ipv6","prefix":64}]}
            ]}
            """);

        var ips = await client.AgentGetIpAddressesAsync("pve1", 123);

        Assert.Equal(["192.168.7.42"], ips);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/agent/network-get-interfaces", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Non_success_response_throws_ProxmoxApiException()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.Enqueue(HttpStatusCode.Forbidden, """{"errors":{"token":"permission denied"}}""");

        var ex = await Assert.ThrowsAsync<ProxmoxApiException>(() => client.GetNextVmIdAsync());

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("permission denied", ex.ResponseBody);
        Assert.Contains("/cluster/nextid", ex.Message);
    }
}
