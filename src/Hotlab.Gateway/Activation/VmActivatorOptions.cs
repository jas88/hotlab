using Hotlab.Gateway.Configuration;

namespace Hotlab.Gateway.Activation;

/// <summary>Timing knobs for <see cref="VmActivator"/>.</summary>
public sealed class VmActivatorOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Hotlab:Activation";

    /// <summary>Overall budget for one activation (start, agent IP, port open). Default 90s.</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Delay between guest-agent and port-probe attempts. Default 1s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Timeout for a single TCP connect attempt. Default 2s.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Checks that every interval is positive (and within timer range). Returns an error
    /// message, or null when valid.
    /// </summary>
    public string? Validate() =>
        OptionsValidation.Combine(
            OptionsValidation.CheckTimeout($"{SectionName}:ReadyTimeout", ReadyTimeout),
            OptionsValidation.CheckTimeout($"{SectionName}:PollInterval", PollInterval),
            OptionsValidation.CheckTimeout($"{SectionName}:ProbeTimeout", ProbeTimeout));
}
