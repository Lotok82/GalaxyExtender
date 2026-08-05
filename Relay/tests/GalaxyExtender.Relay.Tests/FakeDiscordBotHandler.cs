using System.Net;
using System.Text;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Stands in for Discord's channel-messages REST endpoint (the Stage 2 bot read). Returns an
/// empty message array unless a response has been scripted, and records every request URI so
/// tests can assert cursor behaviour (<c>limit=1</c> on first run, <c>after=</c> thereafter).
/// </summary>
public sealed class FakeDiscordBotHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly Queue<Func<HttpResponseMessage>> _scripted = new();
    private readonly List<string> _requestUris = [];

    public IReadOnlyList<string> RequestUris
    {
        get
        {
            lock (_lock)
            {
                return _requestUris.ToArray();
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requestUris.Count;
            }
        }
    }

    /// <summary>Scripts one 200 whose body is a JSON array of message objects.</summary>
    public void ScriptMessages(params string[] messageObjects) =>
        Script(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{string.Join(",", messageObjects)}]",
                Encoding.UTF8, "application/json")
        });

    public void ScriptStatus(HttpStatusCode statusCode) =>
        Script(() => new HttpResponseMessage(statusCode));

    private void Script(Func<HttpResponseMessage> response)
    {
        lock (_lock)
        {
            _scripted.Enqueue(response);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _requestUris.Add(request.RequestUri?.ToString() ?? string.Empty);

            var response = _scripted.Count > 0
                ? _scripted.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };

            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Builders for Discord message JSON, in the field shapes the R2 live verification confirmed:
/// users carry <c>content</c> and <c>global_name</c>/<c>username</c>; the relay's own webhook
/// posts carry <c>webhook_id</c> + <c>author.bot</c> with the text in embeds.
/// </summary>
public static class DiscordJson
{
    public static string User(
        string id, string author, string content,
        string? mentionsJson = null, bool attachments = false)
    {
        var extra = new StringBuilder();

        if (mentionsJson is not null)
        {
            extra.Append($",\"mentions\":{mentionsJson}");
        }

        if (attachments)
        {
            extra.Append(",\"attachments\":[{\"id\":\"1\",\"filename\":\"cat.png\"}]");
        }

        return $"{{\"id\":\"{id}\",\"content\":{Quote(content)}," +
               $"\"author\":{{\"id\":\"9{id}\",\"username\":\"{author}\",\"global_name\":\"{author}\"}}," +
               $"\"timestamp\":\"2026-08-06T12:00:00+00:00\"{extra}}}";
    }

    public static string Webhook(string id) =>
        $"{{\"id\":\"{id}\",\"content\":\"\",\"webhook_id\":\"777\"," +
        "\"author\":{\"id\":\"777\",\"username\":\"GalaxyExtender Bridge\",\"bot\":true}," +
        "\"embeds\":[{\"description\":\"[GuildChat] Kaelen: hello\"}]," +
        "\"timestamp\":\"2026-08-06T12:00:00+00:00\"}";

    public static string Bot(string id, string content) =>
        $"{{\"id\":\"{id}\",\"content\":{Quote(content)}," +
        "\"author\":{\"id\":\"888\",\"username\":\"SomeOtherBot\",\"bot\":true}," +
        "\"timestamp\":\"2026-08-06T12:00:00+00:00\"}";

    private static string Quote(string text) =>
        System.Text.Json.JsonSerializer.Serialize(text);
}
