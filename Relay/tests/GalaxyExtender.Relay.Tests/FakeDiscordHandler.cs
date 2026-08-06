using System.Net;
using System.Text;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Stands in for Discord's webhook endpoint. Returns 204 (Discord's success shape) unless a
/// response has been scripted, and records every request body for assertions.
/// </summary>
public sealed class FakeDiscordHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly Queue<Func<HttpResponseMessage>> _scripted = new();
    private readonly List<string> _requestBodies = [];

    public IReadOnlyList<string> RequestBodies
    {
        get
        {
            lock (_lock)
            {
                return _requestBodies.ToArray();
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requestBodies.Count;
            }
        }
    }

    public void ScriptStatus(HttpStatusCode statusCode) =>
        Script(() => new HttpResponseMessage(statusCode));

    /// <summary>Scripts one 429 in Discord's shape: JSON body with retry_after in seconds.</summary>
    public void ScriptRateLimit(double retryAfterSeconds) =>
        Script(() => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent(
                $"{{\"retry_after\": {retryAfterSeconds}, \"global\": false}}",
                Encoding.UTF8, "application/json")
        });

    private void Script(Func<HttpResponseMessage> response)
    {
        lock (_lock)
        {
            _scripted.Enqueue(response);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_lock)
        {
            _requestBodies.Add(body);

            return _scripted.Count > 0
                ? _scripted.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
