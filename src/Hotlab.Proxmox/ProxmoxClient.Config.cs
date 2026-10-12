using System.Globalization;
using System.Text.Json;

namespace Hotlab.Proxmox;

/// <summary>VM configuration, shutdown and existence checks.</summary>
public sealed partial class ProxmoxClient
{
    /// <summary>
    /// Returns the VM's current configuration (<c>GET /nodes/{node}/qemu/{vmid}/config</c>)
    /// as raw strings: numbers and booleans are rendered as their JSON text ("1", "512").
    /// Keys are case-sensitive, as Proxmox reports them (e.g. "template", "tags", "agent").
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetVmConfigAsync(
        string node, int vmId, CancellationToken ct = default)
    {
        using var data = await GetAsync($"/nodes/{node}/qemu/{vmId}/config", ct).ConfigureAwait(false);
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.RootElement.ValueKind != JsonValueKind.Object)
            return config;

        foreach (var property in data.RootElement.EnumerateObject())
        {
            config[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => property.Value.GetRawText(),
            };
        }

        return config;
    }

    /// <summary>True when the VM is a template (its config has <c>template: 1</c>).</summary>
    public async Task<bool> IsTemplateAsync(string node, int vmId, CancellationToken ct = default)
    {
        var config = await GetVmConfigAsync(node, vmId, ct).ConfigureAwait(false);
        return config.TryGetValue("template", out var value) && value == "1";
    }

    /// <summary>
    /// Updates VM configuration options synchronously (<c>PUT /nodes/{node}/qemu/{vmid}/config</c>,
    /// form-encoded). Use for quick option changes such as <c>tags</c>; options that need a
    /// worker task (disk moves, etc.) belong to the asynchronous POST form instead.
    /// </summary>
    public async Task SetVmConfigAsync(
        string node, int vmId, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("At least one option is required.", nameof(values));
        using var _ = await SendAsync(HttpMethod.Put, $"/nodes/{node}/qemu/{vmId}/config", values, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a clean ACPI/agent shutdown (<c>POST status/shutdown</c>). Returns the task UPID.
    /// </summary>
    /// <param name="node">Node hosting the VM.</param>
    /// <param name="vmId">VMID.</param>
    /// <param name="timeout">How long Proxmox waits for the guest to power off before the
    /// task fails (or, with <paramref name="forceStop"/>, hard-stops it); null for the PVE default.</param>
    /// <param name="forceStop">Hard-stop the VM if it has not shut down within the timeout.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> ShutdownVmAsync(
        string node, int vmId, TimeSpan? timeout = null, bool forceStop = false, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>();
        if (timeout is { } t)
            form["timeout"] = ((long)Math.Ceiling(t.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        if (forceStop)
            form["forceStop"] = "1";

        using var data = await PostAsync($"/nodes/{node}/qemu/{vmId}/status/shutdown", form, ct).ConfigureAwait(false);
        return data.RootElement.GetString()!;
    }

    /// <summary>
    /// Returns where a QEMU VM currently lives (cluster-wide, via
    /// <c>GET /cluster/resources?type=vm</c>), or null if no such VM is visible to this token.
    /// Node-independent, so it also finds VMs that have migrated.
    /// </summary>
    public async Task<string?> FindVmNodeAsync(int vmId, CancellationToken ct = default)
    {
        using var data = await GetAsync("/cluster/resources?type=vm", ct).ConfigureAwait(false);
        foreach (var resource in data.RootElement.EnumerateArray())
        {
            if (resource.TryGetProperty("type", out var type) && type.GetString() == "qemu" &&
                resource.TryGetProperty("vmid", out var id) && id.ValueKind == JsonValueKind.Number &&
                id.GetInt32() == vmId)
            {
                return resource.TryGetProperty("node", out var node) ? node.GetString() : "";
            }
        }

        return null;
    }

    /// <summary>True if a QEMU VM with this VMID exists anywhere in the cluster (see <see cref="FindVmNodeAsync"/>).</summary>
    public async Task<bool> VmExistsAsync(int vmId, CancellationToken ct = default) =>
        await FindVmNodeAsync(vmId, ct).ConfigureAwait(false) is not null;
}
