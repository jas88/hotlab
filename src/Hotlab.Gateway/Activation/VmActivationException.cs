using Hotlab.Gateway.Routing;

namespace Hotlab.Gateway.Activation;

/// <summary>Raised when a VM could not be brought to a ready (RDP reachable) state.</summary>
public sealed class VmActivationException : Exception
{
    /// <summary>The VM that failed to activate.</summary>
    public VmTarget Target { get; }

    /// <summary>Creates the exception for <paramref name="target"/>.</summary>
    public VmActivationException(VmTarget target, string message, Exception? innerException = null)
        : base(FormatMessage(target, message), innerException)
    {
        Target = target;
    }

    private static string FormatMessage(VmTarget target, string message)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"VM {target.VmId} on node {target.Node}: {message}");
    }
}
