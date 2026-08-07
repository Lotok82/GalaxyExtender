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
    public sealed record RecordedRequest(string Method, string Uri, string? Body);

    private readonly object _lock = new();
    private readonly Queue<Func<HttpResponseMessage>> _scripted = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<string> RequestUris
    {
        get
        {
            lock (_lock)
            {
                return _requests.Select(r => r.Uri).ToArray();
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count;
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

    /// <summary>Scripts one 200 with an arbitrary JSON body — the bot-identity read, for instance.</summary>
    public void ScriptBody(string json) =>
        Script(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
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

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read outside the lock; StringContent is buffered so this never actually blocks.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_lock)
        {
            _requests.Add(new RecordedRequest(
                request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, body));

            return _scripted.Count > 0
                ? _scripted.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
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
        string? mentionsJson = null, bool attachments = false, DateTimeOffset? timestamp = null)
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

        var stamp = timestamp?.ToString("O") ?? "2026-08-06T12:00:00+00:00";

        return $"{{\"id\":\"{id}\",\"content\":{Quote(content)}," +
               $"\"author\":{{\"id\":\"9{id}\",\"username\":\"{author}\",\"global_name\":\"{author}\"}}," +
               $"\"timestamp\":\"{stamp}\"{extra}}}";
    }

    /// <summary>
    /// A message that mentions the bot, as Discord actually delivers one: the <c>&lt;@id&gt;</c>
    /// token in the content AND the mentioned user in the <c>mentions</c> array. Stamped "now" by
    /// default, because a command older than the relay's max age is deliberately ignored.
    /// </summary>
    public static string Mention(
        string id, string author, string text, string botUserId, DateTimeOffset? timestamp = null) =>
        User(id, author, $"<@{botUserId}> {text}",
            mentionsJson: $"[{{\"id\":\"{botUserId}\",\"username\":\"GalaxyExtender\"}}]",
            timestamp: timestamp ?? DateTimeOffset.UtcNow);

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
