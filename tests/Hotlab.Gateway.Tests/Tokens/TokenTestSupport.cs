using System.Collections.Concurrent;
using Hotlab.Gateway.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Tests.Tokens;

/// <summary>Manually advanced clock.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Logger that keeps every formatted message plus its structured values.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> kvs
            ? string.Join(" | ", kvs.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        _entries.Enqueue($"{logLevel}: {formatter(state, exception)} [{values}] {exception}");
    }
}

internal static class TokenTestSupport
{
    /// <summary>A fixed 32-byte test key.</summary>
    public static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public static readonly DateTimeOffset Epoch = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    public static RoutingTokenService Create(
        TimeProvider? time = null, string? key = null, TimeSpan? skew = null) =>
        new(Options.Create(new RoutingTokenOptions
        {
            SigningKey = key ?? Key,
            ClockSkew = skew ?? TimeSpan.FromMinutes(2),
        }), time);

    /// <summary>The signature (last field) of a Hotlab token.</summary>
    public static string SignatureOf(string token) => token[(token.LastIndexOf(':') + 1)..];
}
