namespace Hotlab.Gateway.Routing;

/// <summary>
/// The guest a user's RDP connection is routed to.
/// <paramref name="Host"/> overrides guest-agent IP discovery when set.
/// </summary>
public sealed record VmTarget(string Node, int VmId, int Port = 3389, string? Host = null);
