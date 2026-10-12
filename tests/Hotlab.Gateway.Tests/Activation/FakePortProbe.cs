using System.Net;
using Hotlab.Gateway.Activation;

namespace Hotlab.Gateway.Tests.Activation;

/// <summary><see cref="IPortProbe"/> whose answer is scripted per attempt.</summary>
internal sealed class FakePortProbe(Func<int, bool>? isOpen = null) : IPortProbe
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public List<IPEndPoint> Endpoints { get; } = [];

    public Task<bool> CanConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _calls);
        lock (Endpoints)
            Endpoints.Add(endpoint);
        return Task.FromResult(isOpen?.Invoke(call) ?? true);
    }
}
