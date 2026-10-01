using System.Net;
using System.Text;

namespace Hotlab.Proxmox.Tests;

/// <summary>
/// Records every request (including its content, read eagerly before disposal) and
/// replays a queue of canned responses.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<(HttpMethod Method, string Path, string? Body, string? AuthHeader)> Requests { get; } = [];

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public FakeHttpMessageHandler EnqueueData(string dataJson) =>
        Enqueue(HttpStatusCode.OK, $$"""{"data":{{dataJson}}}""");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Headers.TryGetValues("Authorization", out var auth);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, auth?.FirstOrDefault()));

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No canned response for {request.Method} {request.RequestUri}");
        return _responses.Dequeue();
    }
}
