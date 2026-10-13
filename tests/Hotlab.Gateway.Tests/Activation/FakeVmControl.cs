using Hotlab.Gateway.Activation;

namespace Hotlab.Gateway.Tests.Activation;

/// <summary>Scriptable <see cref="IVmControl"/> that counts calls.</summary>
internal sealed class FakeVmControl : IVmControl
{
    private int _statusCalls;
    private int _startCalls;
    private int _agentCalls;

    public string Status { get; set; } = "running";

    /// <summary>Invoked on start; default sets Status to running and returns "OK".</summary>
    public Func<CancellationToken, Task<string>>? OnStart { get; set; }

    /// <summary>Invoked on agent query; default returns <see cref="AgentAddresses"/>.</summary>
    public Func<int, IReadOnlyList<string>>? OnAgent { get; set; }

    public IReadOnlyList<string> AgentAddresses { get; set; } = ["10.0.0.5"];

    public int StatusCalls => Volatile.Read(ref _statusCalls);
    public int StartCalls => Volatile.Read(ref _startCalls);
    public int AgentCalls => Volatile.Read(ref _agentCalls);

    public Task<string> GetStatusAsync(string node, int vmId, CancellationToken ct)
    {
        Interlocked.Increment(ref _statusCalls);
        return Task.FromResult(Status);
    }

    public async Task<string> StartAndWaitAsync(string node, int vmId, CancellationToken ct)
    {
        Interlocked.Increment(ref _startCalls);
        if (OnStart is not null)
            return await OnStart(ct);
        Status = "running";
        return "OK";
    }

    public Task<IReadOnlyList<string>> GetAgentIPv4AddressesAsync(string node, int vmId, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _agentCalls);
        return Task.FromResult(OnAgent is null ? AgentAddresses : OnAgent(call));
    }
}
