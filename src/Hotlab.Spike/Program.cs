using System.Diagnostics;
using System.Net.Sockets;
using Hotlab.Proxmox;

var url = Environment.GetEnvironmentVariable("HOTLAB_PVE_URL");
var tokenId = Environment.GetEnvironmentVariable("HOTLAB_PVE_TOKEN_ID");
var tokenSecret = Environment.GetEnvironmentVariable("HOTLAB_PVE_TOKEN_SECRET");
var node = Environment.GetEnvironmentVariable("HOTLAB_PVE_NODE");
var templateVar = Environment.GetEnvironmentVariable("HOTLAB_TEMPLATE_VMID");

if (url is null || tokenId is null || tokenSecret is null || node is null ||
    !int.TryParse(templateVar, out var templateVmId))
{
    Console.Error.WriteLine("""
        hotlab-spike: measure Proxmox clone -> boot -> RDP-listener timings.

        Required environment variables:
          HOTLAB_PVE_URL           e.g. https://pve.example:8006
          HOTLAB_PVE_TOKEN_ID      e.g. hotlab@pve!spike
          HOTLAB_PVE_TOKEN_SECRET  the API token secret
          HOTLAB_PVE_NODE          node name, e.g. pve1
          HOTLAB_TEMPLATE_VMID     VMID of the template to linked-clone

        Optional:
          HOTLAB_PVE_FINGERPRINT   pin server cert by SHA-256 fingerprint
          HOTLAB_PVE_INSECURE=1    accept any certificate (test hosts only)
          HOTLAB_RDP_PORT          guest RDP port to probe (default 3389)
          HOTLAB_KEEP=1            leave the clone running (skip teardown)

        The template must be a Proxmox *template* with qemu-guest-agent enabled and
        an RDP server (Windows TermService, or xrdp on Linux) installed so the
        listener probe has something to hit.
        """);
    return 1;
}

var rdpPort = int.TryParse(Environment.GetEnvironmentVariable("HOTLAB_RDP_PORT"), out var p) ? p : 3389;
var keep = Environment.GetEnvironmentVariable("HOTLAB_KEEP") == "1";
ProxmoxTlsOptions? tls =
    Environment.GetEnvironmentVariable("HOTLAB_PVE_INSECURE") == "1"
        ? new ProxmoxTlsOptions { DangerousAcceptAnyCertificate = true }
        : Environment.GetEnvironmentVariable("HOTLAB_PVE_FINGERPRINT") is { Length: > 0 } fp
            ? new ProxmoxTlsOptions { Sha256Fingerprint = fp }
            : null;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var ct = cts.Token;
using var client = new ProxmoxClient(new Uri(url), tokenId, tokenSecret, tls);

var total = Stopwatch.StartNew();
var phase = Stopwatch.StartNew();
int? cloneVmId = null;

try
{
    var newId = await client.GetNextVmIdAsync(ct);
    Console.WriteLine($"Cloning template {templateVmId} -> VM {newId} (linked clone)...");

    var cloneUpid = await client.CloneVmAsync(node, templateVmId, newId, name: $"hotlab-spike-{newId}", ct: ct);
    cloneVmId = newId;
    var cloneExit = await client.WaitForTaskAsync(node, cloneUpid, ct: ct);
    if (cloneExit != "OK")
        throw new InvalidOperationException($"Clone task failed: {cloneExit}");
    var cloneMs = phase.ElapsedMilliseconds;

    phase.Restart();
    var startUpid = await client.StartVmAsync(node, newId, ct);
    var startExit = await client.WaitForTaskAsync(node, startUpid, ct: ct);
    if (startExit != "OK")
        throw new InvalidOperationException($"Start task failed: {startExit}");
    var startMs = phase.ElapsedMilliseconds;
    Console.WriteLine($"VM {newId} started; waiting for guest agent...");

    phase.Restart();
    while (!await client.AgentPingAsync(node, newId, ct))
        await Task.Delay(500, ct);
    var agentMs = phase.ElapsedMilliseconds;

    phase.Restart();
    IReadOnlyList<string> ips;
    while ((ips = await client.AgentGetIpAddressesAsync(node, newId, ct)).Count == 0)
        await Task.Delay(500, ct);
    var ip = ips[0];
    Console.WriteLine($"Guest agent up, IP {ip}; probing RDP port {rdpPort}...");

    while (!await ProbeTcpAsync(ip, rdpPort, ct))
        await Task.Delay(250, ct);
    var rdpMs = phase.ElapsedMilliseconds;
    total.Stop();

    Console.WriteLine();
    Console.WriteLine("Phase               Elapsed (ms)");
    Console.WriteLine("------------------  ------------");
    Console.WriteLine($"Linked clone        {cloneMs,12}");
    Console.WriteLine($"Start task          {startMs,12}");
    Console.WriteLine($"Guest agent ready   {agentMs,12}");
    Console.WriteLine($"IP + RDP listener   {rdpMs,12}");
    Console.WriteLine($"TOTAL               {total.ElapsedMilliseconds,12}");
    return 0;
}
finally
{
    if (cloneVmId is { } vmId && !keep)
    {
        Console.WriteLine($"Tearing down VM {vmId}...");
        using var teardownCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            try
            {
                var stopUpid = await client.StopVmAsync(node, vmId, teardownCts.Token);
                await client.WaitForTaskAsync(node, stopUpid, ct: teardownCts.Token);
            }
            catch (ProxmoxApiException)
            {
                // Already stopped (or never started) — proceed to destroy.
            }
            var destroyUpid = await client.DestroyVmAsync(node, vmId, teardownCts.Token);
            var destroyExit = await client.WaitForTaskAsync(node, destroyUpid, ct: teardownCts.Token);
            Console.WriteLine($"Destroy: {destroyExit}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Teardown failed — VM {vmId} may need manual cleanup: {ex.Message}");
        }
    }
    else if (cloneVmId is { } kept && keep)
    {
        Console.WriteLine($"HOTLAB_KEEP=1 — VM {kept} left in place.");
    }
}

static async Task<bool> ProbeTcpAsync(string host, int port, CancellationToken ct)
{
    using var tcp = new TcpClient();
    try
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(1));
        await tcp.ConnectAsync(host, port, probeCts.Token);
        return true;
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return false;
    }
    catch (SocketException)
    {
        return false;
    }
}
