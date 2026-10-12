using System.Net;

namespace Hotlab.Gateway.Relay;

/// <summary>RDP listener and relay settings, bound from <c>Hotlab:Gateway</c>.</summary>
public sealed class GatewayOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Hotlab:Gateway";

    /// <summary>Listen endpoint, e.g. <c>0.0.0.0:3389</c> or <c>[::]:3389</c>. Port 0 picks a free port.</summary>
    public string Listen { get; set; } = "0.0.0.0:3389";

    /// <summary>How long a client has to send its complete first PDU.</summary>
    public TimeSpan FirstPduTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest first PDU accepted (a Connection Request is normally well under 100 bytes).</summary>
    public int MaxFirstPduBytes { get; set; } = 4096;

    /// <summary>Timeout for the TCP connect to the guest once it is reported ready.</summary>
    public TimeSpan BackendConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum concurrent connections; 0 or less means unlimited. Excess connections are closed.</summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// How long the relay keeps the other direction open after one side half-closes.
    /// RDP peers don't half-close in normal operation, so this only bounds stragglers.
    /// </summary>
    public TimeSpan HalfCloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Parses <see cref="Listen"/>.</summary>
    /// <exception cref="FormatException">The value is not a valid IP endpoint.</exception>
    public IPEndPoint GetListenEndPoint() =>
        IPEndPoint.TryParse(Listen, out var ep)
            ? ep
            : throw new FormatException($"Hotlab:Gateway:Listen '{Listen}' is not a valid IP endpoint.");
}
