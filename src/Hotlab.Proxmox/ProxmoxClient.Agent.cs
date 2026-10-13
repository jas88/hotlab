using System.Globalization;
using System.Text.Json;

namespace Hotlab.Proxmox;

/// <summary>QEMU guest agent operations (<c>/nodes/{node}/qemu/{vmid}/agent/...</c>).</summary>
public sealed partial class ProxmoxClient
{
    /// <summary>
    /// Pings the QEMU guest agent. Returns true when the agent responds, false when
    /// the API reports an error (typically: agent not yet running in the guest).
    /// </summary>
    public async Task<bool> AgentPingAsync(string node, int vmId, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, $"/nodes/{node}/qemu/{vmId}/agent/ping", form: null, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Returns the guest's non-loopback IPv4 addresses, as reported by the QEMU
    /// guest agent. Empty if the agent reports no usable addresses yet.
    /// </summary>
    public async Task<IReadOnlyList<string>> AgentGetIpAddressesAsync(
        string node, int vmId, CancellationToken ct = default)
    {
        using var data = await GetAsync($"/nodes/{node}/qemu/{vmId}/agent/network-get-interfaces", ct).ConfigureAwait(false);
        var addresses = new List<string>();
        if (!data.RootElement.TryGetProperty("result", out var interfaces))
            return addresses;

        foreach (var iface in interfaces.EnumerateArray())
        {
            if (iface.TryGetProperty("name", out var name) && name.GetString() == "lo")
                continue;
            if (!iface.TryGetProperty("ip-addresses", out var ips))
                continue;
            foreach (var ip in ips.EnumerateArray())
            {
                if (ip.GetProperty("ip-address-type").GetString() != "ipv4")
                    continue;
                var address = ip.GetProperty("ip-address").GetString();
                if (address is null || address.StartsWith("127.", StringComparison.Ordinal))
                    continue;
                addresses.Add(address);
            }
        }

        return addresses;
    }

    /// <summary>
    /// Returns the guest OS identity (<c>agent/get-osinfo</c>). The id is e.g. "mswindows",
    /// "debian" or "ubuntu"; either field may be empty if the agent omits it.
    /// </summary>
    public async Task<AgentOsInfo> AgentGetOsInfoAsync(string node, int vmId, CancellationToken ct = default)
    {
        using var data = await GetAsync($"/nodes/{node}/qemu/{vmId}/agent/get-osinfo", ct).ConfigureAwait(false);
        if (!data.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return new AgentOsInfo("", "");
        return new AgentOsInfo(StringProperty(result, "id"), StringProperty(result, "name"));
    }

    /// <summary>
    /// Starts a program in the guest (<c>agent/exec</c>) and returns its guest PID. The
    /// program is run directly (no shell); each element of <paramref name="command"/> is one
    /// argv entry, sent as a repeated <c>command</c> form parameter (the API's array encoding).
    /// Never pass secrets here: arguments are visible in the guest's process list.
    /// </summary>
    /// <param name="node">Node hosting the VM.</param>
    /// <param name="vmId">VMID.</param>
    /// <param name="command">Program and arguments; at least one element.</param>
    /// <param name="inputData">Optional data passed to the program's stdin.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> AgentExecAsync(
        string node, int vmId, IReadOnlyList<string> command, string? inputData = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
            throw new ArgumentException("Command must have at least one element.", nameof(command));

        var form = command.Select(arg => new KeyValuePair<string, string>("command", arg)).ToList();
        if (inputData is not null)
            form.Add(new("input-data", inputData));

        using var data = await PostAsync($"/nodes/{node}/qemu/{vmId}/agent/exec", form, ct).ConfigureAwait(false);
        return data.RootElement.GetProperty("pid").GetInt32();
    }

    /// <summary>Returns the state of a program started with <see cref="AgentExecAsync"/>.</summary>
    public async Task<AgentExecStatus> AgentExecStatusAsync(
        string node, int vmId, int pid, CancellationToken ct = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture,
            $"/nodes/{node}/qemu/{vmId}/agent/exec-status?pid={pid}");
        using var data = await GetAsync(path, ct).ConfigureAwait(false);
        var root = data.RootElement;
        bool exited = root.TryGetProperty("exited", out var e) &&
            (e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.Number && e.GetInt32() != 0));
        int? exitCode = root.TryGetProperty("exitcode", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32() : null;
        int? signal = root.TryGetProperty("signal", out var sig) && sig.ValueKind == JsonValueKind.Number
            ? sig.GetInt32() : null;
        return new AgentExecStatus(exited, exitCode, signal,
            StringProperty(root, "out-data"), StringProperty(root, "err-data"));
    }

    /// <summary>
    /// Sets a guest account's password through the agent (<c>agent/set-user-password</c>,
    /// qemu-ga <c>guest-set-user-password</c>). The password travels only in the request
    /// body, never on a guest command line.
    /// </summary>
    /// <param name="node">Node hosting the VM.</param>
    /// <param name="vmId">VMID.</param>
    /// <param name="username">Existing guest account.</param>
    /// <param name="password">New password (plain text unless <paramref name="crypted"/>).</param>
    /// <param name="crypted">True when <paramref name="password"/> is already crypt()-hashed.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task AgentSetUserPasswordAsync(
        string node, int vmId, string username, string password, bool crypted = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var form = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["crypted"] = crypted ? "1" : "0",
        };
        using var _ = await PostAsync($"/nodes/{node}/qemu/{vmId}/agent/set-user-password", form, ct)
            .ConfigureAwait(false);
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
