using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tokens;

/// <summary>
/// <c>Hotlab.Gateway mint-rdp &lt;user&gt; --address &lt;host[:port]&gt; [--lifetime 8h]</c>:
/// prints an .rdp file carrying a signed routing token to stdout, for testing without a
/// portal. Uses the gateway's normal configuration (appsettings + environment) for the key.
/// </summary>
public static class MintRdpCommand
{
    /// <summary>The verb that selects this command as the first program argument.</summary>
    public const string Name = "mint-rdp";

    private const string Usage =
        "Usage: Hotlab.Gateway mint-rdp <user> --address <gateway host[:port]> [--lifetime 8h]\n" +
        "  --lifetime accepts <n>d|h|m|s or a TimeSpan (e.g. 08:00:00), at most 3650d; default Hotlab:Tokens:DefaultLifetime.";

    /// <summary>Runs the command. Returns the process exit code (0 success, 1 config error, 2 usage error).</summary>
    /// <param name="args">Arguments after the verb.</param>
    /// <param name="configuration">Gateway configuration (reads <see cref="RoutingTokenOptions.SectionName"/>).</param>
    /// <param name="stdout">Receives the .rdp file.</param>
    /// <param name="stderr">Receives diagnostics.</param>
    /// <param name="timeProvider">Clock; null for the system clock.</param>
    public static int Run(
        IReadOnlyList<string> args, IConfiguration configuration, TextWriter stdout, TextWriter stderr,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!TryParseArgs(args, out var user, out var address, out var lifetime, out var error))
        {
            stderr.WriteLine(error);
            stderr.WriteLine(Usage);
            return 2;
        }

        var options = new RoutingTokenOptions();
        configuration.GetSection(RoutingTokenOptions.SectionName).Bind(options);
        if (options.Validate() is { } configError)
        {
            stderr.WriteLine(configError);
            return 1;
        }

        string token;
        try
        {
            var service = new RoutingTokenService(Options.Create(options), timeProvider);
            token = service.Mint(user, lifetime);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            stderr.WriteLine(Usage);
            return 2;
        }

        stdout.Write(RdpFileRenderer.Render(address, user.Trim().ToLowerInvariant(), token));
        return 0;
    }

    private static bool TryParseArgs(
        IReadOnlyList<string> args, out string user, out string address, out TimeSpan? lifetime,
        out string error)
    {
        user = address = error = "";
        lifetime = null;
        string? u = null, a = null;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--address" or "--lifetime" when i + 1 >= args.Count:
                    error = $"{arg} requires a value.";
                    return false;
                case "--address":
                    a = args[++i];
                    break;
                case "--lifetime":
                    if (!TryParseLifetime(args[++i], out var life))
                    {
                        error = $"Invalid --lifetime '{args[i]}'.";
                        return false;
                    }
                    lifetime = life;
                    break;
                case "-h" or "--help":
                    error = "";
                    return false;
                default:
                    if (arg.StartsWith('-') || u is not null)
                    {
                        error = $"Unexpected argument '{arg}'.";
                        return false;
                    }
                    u = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(u))
        {
            error = "Missing <user>.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(a) || a.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = "Missing or invalid --address.";
            return false;
        }

        user = u;
        address = a;
        return true;
    }

    /// <summary>Longest lifetime <see cref="TryParseLifetime"/> accepts (10 years).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(3650);

    /// <summary>
    /// Parses <c>8h</c>, <c>30m</c>, <c>90s</c>, <c>1d</c>, or a <see cref="TimeSpan"/> string.
    /// Returns false for anything that is not positive and at most <see cref="MaxLifetime"/>
    /// (including values too large to represent), never throws for any text.
    /// </summary>
    public static bool TryParseLifetime(string text, out TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(text);
        lifetime = default;
        text = text.Trim();
        if (text.Length >= 2 && char.IsAsciiLetter(text[^1]) &&
            double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double n))
        {
            double? unitSeconds = char.ToLowerInvariant(text[^1]) switch
            {
                'd' => 86400,
                'h' => 3600,
                'm' => 60,
                's' => 1,
                _ => null,
            };
            if (unitSeconds is null)
                return false;
            // Range-check before converting: TimeSpan.FromSeconds throws on overflow/NaN.
            double seconds = n * unitSeconds.Value;
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > MaxLifetime.TotalSeconds)
                return false;
            lifetime = TimeSpan.FromSeconds(seconds);
        }
        else if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out lifetime))
        {
            return false;
        }

        return lifetime > TimeSpan.Zero && lifetime <= MaxLifetime;
    }
}
