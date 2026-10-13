using System.Globalization;
using Hotlab.Gateway.Configuration;

namespace Hotlab.Gateway.Deploy;

/// <summary>A parsed <c>deploy</c> invocation.</summary>
public sealed record DeployRequest
{
    /// <summary>Source VM (or template) VMID.</summary>
    public required int SourceVmId { get; init; }

    /// <summary>Node hosting the source; clones are created there.</summary>
    public required string Node { get; init; }

    /// <summary>Gateway <c>host[:port]</c> written into the .rdp files.</summary>
    public required string Address { get; init; }

    /// <summary>Output directory (manifest, .rdp files, passwords.csv, users.json).</summary>
    public required string OutDir { get; init; }

    /// <summary>Normalized users, in order, without duplicates.</summary>
    public required IReadOnlyList<string> Users { get; init; }

    /// <summary>Deployment name; null to reuse the manifest's or derive one from the output directory.</summary>
    public string? Name { get; init; }

    /// <summary>Existing guest account to set passwords on; null to create one account per user.</summary>
    public string? GuestUser { get; init; }

    /// <summary>Routing-token lifetime; null for the configured default.</summary>
    public TimeSpan? Lifetime { get; init; }

    /// <summary>Leave clones running instead of shutting them down.</summary>
    public bool LeaveRunning { get; init; }

    /// <summary>Allow remapping users that the users file maps to other VMs.</summary>
    public bool Force { get; init; }

    /// <summary>Maximum number of clones provisioned at once.</summary>
    public int Parallel { get; init; } = 4;

    /// <summary>Gateway users file to merge the mappings into; null to leave it alone.</summary>
    public string? UsersFileTarget { get; init; }
}

/// <summary>Timeouts for guest provisioning, bound from <c>Hotlab:Deploy</c>.</summary>
public sealed class DeployTimings
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Hotlab:Deploy";

    /// <summary>How long to wait for the guest agent after starting a clone.</summary>
    public TimeSpan AgentTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a guest command (id, useradd) may run.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a clean shutdown may take before falling back to a hard stop.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Extra time allowed on top of <see cref="ShutdownTimeout"/> for the shutdown task to
    /// start and be polled, before the deployer gives up on it.
    /// </summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(30);

    /// <summary>Polling interval for the agent and guest commands.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns the problems with these settings (each must be a positive timeout, and
    /// <see cref="ShutdownTimeout"/> plus <see cref="ShutdownGrace"/> must also be one), or null when valid.
    /// </summary>
    public string? Validate()
    {
        var shutdownName = $"{SectionName}:{nameof(ShutdownTimeout)}";
        return OptionsValidation.Combine(
            OptionsValidation.CheckTimeout($"{SectionName}:{nameof(AgentTimeout)}", AgentTimeout),
            OptionsValidation.CheckTimeout($"{SectionName}:{nameof(CommandTimeout)}", CommandTimeout),
            OptionsValidation.CheckTimeout(shutdownName, ShutdownTimeout) ??
                (ShutdownTimeout > OptionsValidation.MaxTimeout - ShutdownGrace
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{shutdownName} must be at most {OptionsValidation.MaxTimeout - ShutdownGrace} (got {ShutdownTimeout}).")
                    : null),
            OptionsValidation.CheckTimeout($"{SectionName}:{nameof(PollInterval)}", PollInterval));
    }
}

/// <summary>A per-user deployment step failed; the message is safe to print (no secrets).</summary>
public sealed class DeployException : Exception
{
    public DeployException()
    {
    }

    public DeployException(string message) : base(message)
    {
    }

    public DeployException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
