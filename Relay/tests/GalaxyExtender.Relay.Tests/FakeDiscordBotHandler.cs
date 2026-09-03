using System.Net;
using System.Text;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Stands in for Discord's channel-messages REST endpoint (the Stage 2 bot read). Returns an
/// empty message array unless a response has been scripted, and records every request URI so
/// tests can assert cursor behaviour (<c>limit=1</c> on first run, <c>after=</c> thereafter).
///
/// Also stands in for the nickname reads, but as a separate conversation with its own scripts and
/// its own record — see the fields for why.
/// </summary>
public sealed class FakeDiscordBotHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(string Method, string Uri, string? Body);

    private readonly object _lock = new();
    private readonly Queue<Func<HttpResponseMessage>> _scripted = new();
    private readonly List<RecordedRequest> _requests = [];

    // The nickname reads (GET channels/{id} for the guild, GET guilds/{g}/members/{u} for the
    // member) are answered from their OWN scripts and recorded in their OWN list, deliberately
    // apart from the ordered queue above. Two reasons: the queue is positional, so letting a
    // lookup dequeue a response scripted for a message fetch would make every test's scripting
    // depend on what the nickname cache happened to hold; and the counts tests assert on are
    // about the channel conversation, which a lookup is not part of. Unscripted lookups answer
    // 404 — the relay's "no nickname here", which is what most tests want to see it fall back to.
    private readonly List<RecordedRequest> _nicknameRequests = [];
    private readonly Dictionary<string, Func<HttpResponseMessage>> _members = new(StringComparer.Ordinal);
    private string? _guildId;

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

    /// <summary>Every guild/member lookup made, in order — the nickname path's own record.</summary>
    public IReadOnlyList<RecordedRequest> NicknameRequests
    {
        get
        {
            lock (_lock)
            {
                return _nicknameRequests.ToArray();
            }
        }
    }

    /// <summary>Member lookups only, as the user ids they asked about.</summary>
    public IReadOnlyList<string> MemberLookups
    {
        get
        {
            lock (_lock)
            {
                return _nicknameRequests
                    .Where(r => r.Uri.Contains("/members/", StringComparison.Ordinal))
                    .Select(r => r.Uri[(r.Uri.LastIndexOf('/') + 1)..])
                    .ToArray();
            }
        }
    }

    /// <summary>Makes the bridge channel report a guild, so nickname lookups can proceed.</summary>
    public void ScriptGuild(string guildId)
    {
        lock (_lock)
        {
            _guildId = guildId;
        }
    }

    /// <summary>
    /// Answers this user's member lookup: <paramref name="nick"/> null means a member with no
    /// nickname (a 200 carrying no <c>nick</c>), which is a real answer and not a miss. Users left
    /// unscripted get a 404 — Discord's "not a member of this guild".
    /// </summary>
    public void ScriptMember(string userId, string? nick)
    {
        var json = nick is null
            ? "{\"user\":{\"id\":\"" + userId + "\"}}"
            : "{\"user\":{\"id\":\"" + userId + "\"},\"nick\":" +
              System.Text.Json.JsonSerializer.Serialize(nick) + "}";

        ScriptMemberResponse(userId, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    /// <summary>Answers this user's member lookup with a bare status — a 403 or a 429.</summary>
    public void ScriptMemberStatus(string userId, HttpStatusCode statusCode) =>
        ScriptMemberResponse(userId, () => new HttpResponseMessage(statusCode));

    private void ScriptMemberResponse(string userId, Func<HttpResponseMessage> response)
    {
        lock (_lock)
        {
            _members[userId] = response;
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

        var uri = request.RequestUri?.ToString() ?? string.Empty;
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        // "channels/{id}" with nothing after it is the guild-id read; anything deeper
        // ("channels/{id}/messages...") is the channel conversation.
        var isGuildRead = request.Method == HttpMethod.Get &&
                          path.Contains("/channels/", StringComparison.Ordinal) &&
                          !path.Contains("/messages", StringComparison.Ordinal);

        var isMemberRead = request.Method == HttpMethod.Get &&
                           path.Contains("/members/", StringComparison.Ordinal);

        lock (_lock)
        {
            if (isGuildRead || isMemberRead)
            {
                _nicknameRequests.Add(new RecordedRequest(request.Method.Method, uri, body));

                if (isGuildRead)
                {
                    return _guildId is null
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                $"{{\"id\":\"111222333444555666\",\"guild_id\":\"{_guildId}\"}}",
                                Encoding.UTF8, "application/json")
                        };
                }

                var userId = path[(path.LastIndexOf('/') + 1)..];

                return _members.TryGetValue(userId, out var member)
                    ? member()
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            _requests.Add(new RecordedRequest(request.Method.Method, uri, body));

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
