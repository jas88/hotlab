namespace Hotlab.Gateway.Activation;

/// <summary>Timing knobs for <see cref="VmActivator"/>.</summary>
public sealed class VmActivatorOptions
{
    /// <summary>Overall budget for one activation (start, agent IP, port open). Default 90s.</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Delay between guest-agent and port-probe attempts. Default 1s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Timeout for a single TCP connect attempt. Default 2s.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
