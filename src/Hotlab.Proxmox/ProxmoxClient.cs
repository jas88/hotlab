using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Hotlab.Proxmox;

/// <summary>
/// Minimal typed client for the Proxmox VE REST API, covering the operations the
/// Hotlab broker/spike needs: VM cloning, power management, destruction, task
/// polling, and QEMU guest agent queries. Authenticates with an API token.
/// </summary>
public sealed partial class ProxmoxClient : IDisposable
{
    private const string ApiPrefix = "/api2/json";

    private readonly HttpClient _http;

    /// <summary>Connects to a Proxmox VE host using an API token.</summary>
    /// <param name="baseUrl">Host base URL, e.g. https://pve.example:8006 (no /api2 suffix).</param>
    /// <param name="tokenId">Full token id, e.g. hotlab@pve!spike.</param>
    /// <param name="tokenSecret">The token secret (UUID).</param>
    /// <param name="tls">TLS validation policy; null for normal validation.</param>
    public ProxmoxClient(Uri baseUrl, string tokenId, string tokenSecret, ProxmoxTlsOptions? tls = null)
        : this(baseUrl, tokenId, tokenSecret, CreateHandler(tls))
    {
    }

    /// <summary>Test constructor: supply a custom message handler (owned by this client).</summary>
    public ProxmoxClient(Uri baseUrl, string tokenId, string tokenSecret, HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { BaseAddress = baseUrl };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"PVEAPIToken={tokenId}={tokenSecret}");
    }

    private static HttpMessageHandler CreateHandler(ProxmoxTlsOptions? tls)
    {
        var handler = new SocketsHttpHandler();
        if (tls is null)
            return handler;

        if (tls.DangerousAcceptAnyCertificate)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }
        else if (tls.NormalizedFingerprint is { } expected)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c2 &&
                    Convert.ToHexString(c2.GetCertHash(HashAlgorithmName.SHA256)) == expected,
            };
        }

        return handler;
    }

    /// <summary>Returns the next free VMID in the cluster.</summary>
    public async Task<int> GetNextVmIdAsync(CancellationToken ct = default)
    {
        using var data = await GetAsync("/cluster/nextid", ct).ConfigureAwait(false);
        return int.Parse(data.RootElement.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Clones a VM (linked clone by default; the source must be a template for linked
    /// clones). Returns the UPID of the clone task.
    /// </summary>
    public async Task<string> CloneVmAsync(
        string node, int sourceVmId, int newVmId,
        string? name = null, bool fullClone = false, string? pool = null,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["newid"] = newVmId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["full"] = fullClone ? "1" : "0",
        };
        if (name is not null) form["name"] = name;
        if (pool is not null) form["pool"] = pool;

        using var data = await PostAsync($"/nodes/{node}/qemu/{sourceVmId}/clone", form, ct).ConfigureAwait(false);
        return data.RootElement.GetString()!;
    }

    /// <summary>Starts a VM. Returns the task UPID.</summary>
    public async Task<string> StartVmAsync(string node, int vmId, CancellationToken ct = default)
    {
        using var data = await PostAsync($"/nodes/{node}/qemu/{vmId}/status/start", form: null, ct).ConfigureAwait(false);
        return data.RootElement.GetString()!;
    }

    /// <summary>
    /// Returns the VM's current power status as reported by Proxmox
    /// (e.g. "running", "stopped", "paused").
    /// </summary>
    public async Task<string> GetVmStatusAsync(string node, int vmId, CancellationToken ct = default)
    {
        using var data = await GetAsync($"/nodes/{node}/qemu/{vmId}/status/current", ct).ConfigureAwait(false);
        return data.RootElement.GetProperty("status").GetString() ?? "";
    }

    /// <summary>Stops a VM (hard power-off). Returns the task UPID.</summary>
    public async Task<string> StopVmAsync(string node, int vmId, CancellationToken ct = default)
    {
        using var data = await PostAsync($"/nodes/{node}/qemu/{vmId}/status/stop", form: null, ct).ConfigureAwait(false);
        return data.RootElement.GetString()!;
    }

    /// <summary>Destroys a VM and its disks. Returns the task UPID.</summary>
    public async Task<string> DestroyVmAsync(string node, int vmId, CancellationToken ct = default)
    {
        using var data = await SendAsync(HttpMethod.Delete, $"/nodes/{node}/qemu/{vmId}", form: null, ct).ConfigureAwait(false);
        return data.RootElement.GetString()!;
    }

    /// <summary>
    /// Polls a task until it stops and returns its exit status (e.g. "OK").
    /// </summary>
    public async Task<string> WaitForTaskAsync(
        string node, string upid, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        var path = $"/nodes/{node}/tasks/{Uri.EscapeDataString(upid)}/status";
        while (true)
        {
            using var data = await GetAsync(path, ct).ConfigureAwait(false);
            var root = data.RootElement;
            if (root.GetProperty("status").GetString() == "stopped")
                return root.TryGetProperty("exitstatus", out var exit) ? exit.GetString() ?? "" : "";
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private Task<JsonDocument> GetAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, form: null, ct);

    private Task<JsonDocument> PostAsync(string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, form, ct);

    /// <summary>Sends a request and unwraps Proxmox's <c>{"data": ...}</c> envelope.</summary>
    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProxmoxApiException(response.StatusCode, method.Method, path, body);

        using var doc = JsonDocument.Parse(body);
        return JsonDocument.Parse(doc.RootElement.GetProperty("data").GetRawText());
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiPrefix + path);
        if (form is not null)
            request.Content = new FormUrlEncodedContent(form);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
