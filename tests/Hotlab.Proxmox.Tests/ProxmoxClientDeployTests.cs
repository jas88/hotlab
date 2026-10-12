using System.Net;

namespace Hotlab.Proxmox.Tests;

/// <summary>Endpoints used by lab deployment: config, shutdown, pools, existence, guest agent.</summary>
public class ProxmoxClientDeployTests
{
    private static readonly Uri BaseUrl = new("https://pve.test:8006");

    private static (ProxmoxClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new ProxmoxClient(BaseUrl, "hotlab@pve!deploy", "s3cret", handler), handler);
    }

    [Fact]
    public async Task GetVmConfig_renders_values_as_strings()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""{"template":1,"name":"tpl","tags":"lab;linux","agent":"1","memory":"2048","onboot":true}""");

        var config = await client.GetVmConfigAsync("pve1", 9000);

        Assert.Equal("/api2/json/nodes/pve1/qemu/9000/config", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("1", config["template"]);
        Assert.Equal("lab;linux", config["tags"]);
        Assert.Equal("1", config["onboot"]);
    }

    [Theory]
    [InlineData("""{"template":1}""", true)]
    [InlineData("""{"template":true}""", true)]
    [InlineData("""{"template":0}""", false)]
    [InlineData("""{"name":"vm"}""", false)]
    public async Task IsTemplate_reads_template_flag(string json, bool expected)
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData(json);

        Assert.Equal(expected, await client.IsTemplateAsync("pve1", 9000));
    }

    [Fact]
    public async Task SetVmConfig_puts_form()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("null");

        await client.SetVmConfigAsync("pve1", 123, new Dictionary<string, string> { ["tags"] = "hotlab;lab" });

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/config", request.Path);
        Assert.Equal("tags=hotlab%3Blab", request.Body);
    }

    [Fact]
    public async Task ShutdownVm_posts_timeout_and_returns_upid()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:shutdown\"");

        var upid = await client.ShutdownVmAsync("pve1", 123, TimeSpan.FromSeconds(90.2));

        Assert.Equal("UPID:shutdown", upid);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/status/shutdown", handler.Requests[0].Path);
        Assert.Equal("timeout=91", handler.Requests[0].Body);
    }

    [Fact]
    public async Task ShutdownVm_forceStop_sets_flag()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("\"UPID:x\"");

        await client.ShutdownVmAsync("pve1", 123, forceStop: true);

        Assert.Equal("forceStop=1", handler.Requests[0].Body);
    }

    [Fact]
    public async Task FindVmNode_and_VmExists_use_cluster_resources()
    {
        var (client, handler) = Create();
        using var _ = client;
        const string Resources = """
            [{"id":"qemu/100","type":"qemu","vmid":100,"node":"pve2"},
             {"id":"lxc/101","type":"lxc","vmid":101,"node":"pve1"}]
            """;
        handler.EnqueueData(Resources).EnqueueData(Resources).EnqueueData(Resources);

        Assert.Equal("pve2", await client.FindVmNodeAsync(100));
        Assert.False(await client.VmExistsAsync(101)); // a container, not a VM
        Assert.False(await client.VmExistsAsync(102));
        Assert.Equal("/api2/json/cluster/resources?type=vm", handler.Requests[0].Path);
    }

    [Theory]
    [InlineData("""[{"type":"qemu","vmid":100}]""")]
    [InlineData("""[{"type":"qemu","vmid":100,"node":""}]""")]
    [InlineData("""[{"type":"qemu","vmid":100,"node":null}]""")]
    public async Task FindVmNode_rejects_vm_listed_without_node(string resources)
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData(resources);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FindVmNodeAsync(100));
    }

    [Fact]
    public async Task CreatePool_posts_poolid_and_comment()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("null");

        await client.CreatePoolAsync("hotlab-lab1", "Hotlab lab1");

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api2/json/pools", handler.Requests[0].Path);
        Assert.Equal("poolid=hotlab-lab1&comment=Hotlab+lab1", handler.Requests[0].Body);
    }

    [Fact]
    public async Task PoolExists_lists_pools()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""[{"poolid":"hotlab-a"},{"poolid":"other","comment":"x"}]""");
        handler.EnqueueData("""[{"poolid":"hotlab-a"}]""");

        Assert.True(await client.PoolExistsAsync("hotlab-a"));
        Assert.False(await client.PoolExistsAsync("hotlab-b"));
        Assert.Equal("/api2/json/pools", handler.Requests[0].Path);
    }

    [Fact]
    public async Task GetPoolMembers_parses_members()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""
            {"comment":"c","members":[
              {"id":"qemu/120","type":"qemu","vmid":120,"node":"pve1","name":"hotlab-lab-alice","status":"stopped"},
              {"id":"storage/pve1/local","type":"storage","node":"pve1","storage":"local"}]}
            """);

        var members = await client.GetPoolMembersAsync("hotlab-lab");

        Assert.Equal("/api2/json/pools/hotlab-lab", handler.Requests[0].Path);
        Assert.Equal(new ProxmoxPoolMember("qemu/120", "qemu", 120, "pve1"), members[0]);
        Assert.Equal(new ProxmoxPoolMember("storage/pve1/local", "storage", null, "pve1"), members[1]);
    }

    [Fact]
    public async Task DeletePool_sends_delete()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("null");

        await client.DeletePoolAsync("hotlab-lab");

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api2/json/pools/hotlab-lab", handler.Requests[0].Path);
    }

    [Fact]
    public async Task AgentExec_sends_command_as_repeated_form_parameters()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""{"pid":4242}""");

        var pid = await client.AgentExecAsync("pve1", 123, ["useradd", "-m", "-s", "/bin/bash", "alice"]);

        Assert.Equal(4242, pid);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/agent/exec", handler.Requests[0].Path);
        Assert.Equal("command=useradd&command=-m&command=-s&command=%2Fbin%2Fbash&command=alice", handler.Requests[0].Body);
    }

    [Fact]
    public async Task AgentExec_rejects_empty_command()
    {
        var (client, _) = Create();
        using var __ = client;
        await Assert.ThrowsAsync<ArgumentException>(() => client.AgentExecAsync("pve1", 123, []));
    }

    [Fact]
    public async Task AgentExecStatus_parses_running_and_exited()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""{"exited":0}""");
        handler.EnqueueData("""{"exited":1,"exitcode":0,"out-data":"1001\n"}""");
        handler.EnqueueData("""{"exited":true,"signal":9,"err-data":"killed"}""");

        Assert.Equal(new AgentExecStatus(false, null, null, "", ""), await client.AgentExecStatusAsync("pve1", 123, 7));
        Assert.Equal(new AgentExecStatus(true, 0, null, "1001\n", ""), await client.AgentExecStatusAsync("pve1", 123, 7));
        Assert.Equal(new AgentExecStatus(true, null, 9, "", "killed"), await client.AgentExecStatusAsync("pve1", 123, 7));
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/agent/exec-status?pid=7", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task AgentSetUserPassword_posts_username_password_crypted()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""{"result":{}}""");

        await client.AgentSetUserPasswordAsync("pve1", 123, "alice", "p&ss=word");

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/agent/set-user-password", request.Path);
        Assert.Equal("username=alice&password=p%26ss%3Dword&crypted=0", request.Body);
        Assert.DoesNotContain("p&ss", request.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentGetOsInfo_detects_windows()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.EnqueueData("""{"result":{"id":"mswindows","name":"Microsoft Windows","pretty-name":"Windows 11"}}""");
        handler.EnqueueData("""{"result":{"id":"debian","name":"Debian GNU/Linux"}}""");

        var windows = await client.AgentGetOsInfoAsync("pve1", 123);
        var debian = await client.AgentGetOsInfoAsync("pve1", 124);

        Assert.True(windows.IsWindows);
        Assert.False(debian.IsWindows);
        Assert.Equal("debian", debian.Id);
        Assert.Equal("/api2/json/nodes/pve1/qemu/123/agent/get-osinfo", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Agent_errors_throw_ProxmoxApiException()
    {
        var (client, handler) = Create();
        using var _ = client;
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"data":null,"message":"QEMU guest agent is not running\n"}""");

        await Assert.ThrowsAsync<ProxmoxApiException>(() => client.AgentSetUserPasswordAsync("pve1", 123, "alice", "longpassword"));
    }
}
