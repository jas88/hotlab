namespace Hotlab.Gateway.Routing;

/// <summary>
/// Static username → VM mapping, bound from the <c>Hotlab</c> configuration section
/// (<c>Hotlab:Users:{username}:{Node, VmId, Port?, Host?}</c>).
/// </summary>
public sealed class UserMapOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Hotlab";

    /// <summary>Per-user targets, keyed by username (case-insensitive).</summary>
    public Dictionary<string, UserTargetOptions> Users { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One user's configured target VM.</summary>
public sealed class UserTargetOptions
{
    /// <summary>Proxmox node hosting the VM.</summary>
    public string? Node { get; set; }

    /// <summary>Proxmox VMID.</summary>
    public int VmId { get; set; }

    /// <summary>Guest RDP port; defaults to 3389.</summary>
    public int? Port { get; set; }

    /// <summary>Fixed guest address, overriding guest-agent IP discovery.</summary>
    public string? Host { get; set; }
}
