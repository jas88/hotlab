namespace Hotlab.Proxmox;

/// <summary>Guest OS identity from the QEMU guest agent (<c>get-osinfo</c>).</summary>
/// <param name="Id">OS id, e.g. "mswindows", "debian", "ubuntu"; empty if unreported.</param>
/// <param name="Name">Human-readable OS name; empty if unreported.</param>
public sealed record AgentOsInfo(string Id, string Name)
{
    /// <summary>The id qemu-ga reports on Windows guests.</summary>
    public const string WindowsId = "mswindows";

    /// <summary>True for a Windows guest.</summary>
    public bool IsWindows => string.Equals(Id, WindowsId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>State of a guest program started via <c>agent/exec</c>.</summary>
/// <param name="Exited">True once the program has finished.</param>
/// <param name="ExitCode">Exit code, when exited normally.</param>
/// <param name="Signal">Terminating signal, when killed by one.</param>
/// <param name="OutData">Captured stdout (may be truncated by the agent).</param>
/// <param name="ErrData">Captured stderr (may be truncated by the agent).</param>
public sealed record AgentExecStatus(bool Exited, int? ExitCode, int? Signal, string OutData, string ErrData);

/// <summary>A member of a Proxmox resource pool.</summary>
/// <param name="Id">Resource id, e.g. "qemu/123" or "storage/pve1/local".</param>
/// <param name="Type">Member type, e.g. "qemu", "lxc", "storage".</param>
/// <param name="VmId">VMID for guests; null for storage members.</param>
/// <param name="Node">Node currently hosting the member, if reported.</param>
public sealed record ProxmoxPoolMember(string Id, string Type, int? VmId, string? Node);
