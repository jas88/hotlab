using System.Globalization;
using System.Net;
using Hotlab.Gateway.Configuration;
using Hotlab.Gateway.Protocol;

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

    /// <summary>Default for <see cref="MaxConnections"/>.</summary>
    public const int DefaultMaxConnections = 256;

    /// <summary>Largest TPKT length (16-bit length field).</summary>
    public const int MaxTpktLength = ushort.MaxValue;

    /// <summary>
    /// Maximum concurrent connections (default <see cref="DefaultMaxConnections"/>); excess
    /// connections are closed. 0 means unlimited and must be chosen explicitly.
    /// </summary>
    public int MaxConnections { get; set; } = DefaultMaxConnections;

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

    /// <summary>
    /// Checks the settings: a parseable <see cref="Listen"/> endpoint, a
    /// <see cref="MaxFirstPduBytes"/> between <see cref="TpktReader.MinimumLength"/> and
    /// <see cref="MaxTpktLength"/>, positive timeouts and a non-negative
    /// <see cref="MaxConnections"/>. Returns an error message, or null when valid.
    /// </summary>
    public string? Validate() =>
        OptionsValidation.Combine(
            IPEndPoint.TryParse(Listen ?? "", out _)
                ? null
                : $"{SectionName}:Listen '{Listen}' is not a valid IP endpoint (e.g. 0.0.0.0:3389 or [::]:3389).",
            MaxFirstPduBytes is < TpktReader.MinimumLength or > MaxTpktLength
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{SectionName}:MaxFirstPduBytes must be between {TpktReader.MinimumLength} and {MaxTpktLength} (got {MaxFirstPduBytes}).")
                : null,
            MaxConnections < 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{SectionName}:MaxConnections must be 0 (unlimited) or positive (got {MaxConnections}).")
                : null,
            OptionsValidation.CheckTimeout($"{SectionName}:FirstPduTimeout", FirstPduTimeout),
            OptionsValidation.CheckTimeout($"{SectionName}:BackendConnectTimeout", BackendConnectTimeout),
            OptionsValidation.CheckTimeout($"{SectionName}:HalfCloseTimeout", HalfCloseTimeout));
}
