using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Configuration;

/// <summary>
/// Startup validation for the gateway's options: each options type exposes a
/// <c>string? Validate()</c> (null when valid), wired through <see cref="IValidateOptions{TOptions}"/>
/// with <c>ValidateOnStart</c> so a bad setting stops the host before it accepts connections.
/// </summary>
public static class OptionsValidation
{
    /// <summary>
    /// Longest timeout accepted for <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// and <see cref="Task.Delay(TimeSpan)"/> (<c>uint.MaxValue - 1</c> milliseconds, about 49.7 days).
    /// </summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from <paramref name="section"/> and validates it on
    /// host start with <paramref name="validate"/> (returns an error message, or null when valid).
    /// </summary>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services, IConfiguration section, Func<TOptions, string?> validate)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(validate);
        services.AddSingleton<IValidateOptions<TOptions>>(new DelegateValidator<TOptions>(validate));
        return services.AddOptions<TOptions>().Bind(section).ValidateOnStart();
    }

    /// <summary>
    /// Returns an error for <paramref name="value"/> unless it is positive and at most
    /// <see cref="MaxTimeout"/>; null otherwise.
    /// </summary>
    public static string? CheckTimeout(string name, TimeSpan value) =>
        value <= TimeSpan.Zero || value > MaxTimeout
            ? string.Create(CultureInfo.InvariantCulture,
                $"{name} must be positive and at most {MaxTimeout} (got {value}).")
            : null;

    /// <summary>Joins non-null errors into one message, or null if there are none.</summary>
    public static string? Combine(params string?[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var present = errors.Where(e => e is not null).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    private sealed class DelegateValidator<TOptions>(Func<TOptions, string?> validate) : IValidateOptions<TOptions>
        where TOptions : class
    {
        public ValidateOptionsResult Validate(string? name, TOptions options) =>
            validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
