using System.Text.Json;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// On-demand Discord channel read for Stage 2 (R3) with the echo filter (R4).
///
/// There is no background worker on this host, so fetching happens when a client polls — but at
/// most once per <see cref="RelayOptions.Stage2FetchCacheSeconds"/>, so the Discord-facing request
/// rate is independent of how many players poll. The freshness stamp is deliberately in-memory
/// only: losing it to a recycle costs one extra fetch, and keeping it out of the state store keeps
/// idle polls free of file writes.
///
/// The cursor (last-seen snowflake) IS durable — it advances past every fetched message, filtered
/// or not, so the relay's own webhook posts are never re-examined and a recycle never replays
/// history. On the very first run with no cursor, the reader stamps the channel's newest message
/// and queues NOTHING: enabling Stage 2 must not spray channel history into the guild room.
/// </summary>
public sealed class DiscordReader(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DiscordOptions> options,
    IOptionsMonitor<RelayOptions> relayOptions,
    IStateStore store,
    ILogger<DiscordReader> logger)
{
    public const string HttpClientName = "discord-bot";

    /// <summary>Most messages requested per fetch. Discord's own maximum is 100.</summary>
    private const int FetchLimit = 50;

    private readonly object _fetchLock = new();
    private DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;
    private Task? _fetchInFlight;

    /// <summary>
    /// Fetches new channel messages into the pending queue if the cached snapshot is stale.
    /// Serialised: concurrent polls share one in-flight fetch rather than stacking requests.
    /// Never throws — a failed fetch just means this poll serves whatever is already queued.
    /// </summary>
    public async Task FetchIfDueAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.IsStage2Configured)
        {
            return;
        }

        Task fetch;

        lock (_fetchLock)
        {
            var window = TimeSpan.FromSeconds(relayOptions.CurrentValue.Stage2FetchCacheSeconds);

            if (_fetchInFlight is null &&
                DateTimeOffset.UtcNow - _lastFetchUtc < window)
            {
                return;
            }

            // Piggyback on an in-flight fetch instead of starting another; the stamp is set by
            // the fetch itself so the window measures from the attempt, success or not.
            _fetchInFlight ??= FetchAsync();
            fetch = _fetchInFlight;
        }

        try
        {
            // The poll that triggered the fetch waits for it (so its claim sees the new
            // messages); cancellation abandons the WAIT, never the fetch.
            await fetch.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_fetchLock)
            {
                if (fetch.IsCompleted && ReferenceEquals(_fetchInFlight, fetch))
                {
                    _fetchInFlight = null;
                }
            }
        }
    }

    private async Task FetchAsync()
    {
        lock (_fetchLock)
        {
            _lastFetchUtc = DateTimeOffset.UtcNow;
        }

        var current = options.CurrentValue;
        var channelId = current.ChannelId!;
        var cursor = store.Read(state => state.Stage2Cursor);

        // First run: stamp "now" in message-id terms without queueing history.
        var url = cursor is null
            ? $"channels/{channelId}/messages?limit=1"
            : $"channels/{channelId}/messages?after={Uri.EscapeDataString(cursor)}&limit={FetchLimit}";

        string body;

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bot {current.BotToken}");

            using var response = await client.SendAsync(request, CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                // 401/403 = bad token or missing channel access; 429 = rate limited. All of them
                // resolve the same way here: log, skip this round, retry after the cache window.
                logger.LogWarning("Discord channel read failed with HTTP {Status}",
                    (int)response.StatusCode);
                return;
            }

            body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord channel read failed: {Error}", ex.Message);
            return;
        }

        List<FetchedMessage> fetched;

        try
        {
            fetched = ParseMessages(body);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Discord channel read returned unparseable JSON: {Error}", ex.Message);
            return;
        }

        if (fetched.Count == 0)
        {
            return;
        }

        // Oldest first; Discord's ordering differs between plain and ?after reads, so sort
        // rather than trust it. Snowflakes are numeric time-ordered ids.
        fetched.Sort((a, b) => a.NumericId.CompareTo(b.NumericId));

        var newCursor = fetched[^1].Id;
        var initialising = cursor is null;
        var now = DateTimeOffset.UtcNow;
        var relay = relayOptions.CurrentValue;

        var enqueued = store.Mutate(state =>
        {
            state.Stage2Cursor = newCursor;

            if (initialising)
            {
                return 0;
            }

            var added = 0;

            foreach (var message in fetched)
            {
                // R4, the echo filter: our webhook's posts (and any bot's) never re-enter.
                if (message.FromBotOrWebhook)
                {
                    continue;
                }

                var text = Stage2Sanitizer.SanitizeText(
                    message.Content, message.MentionNames,
                    message.HasAttachments, message.HasEmbeds, message.HasStickers);

                if (text.Length == 0)
                {
                    continue;
                }

                state.Stage2Pending.Add(new PendingEntry
                {
                    Id = message.Id,
                    Author = Stage2Sanitizer.SanitizeAuthor(message.GlobalName, message.Username),
                    Text = text,
                    TimestampUtc = message.TimestampUtc,
                    ReceivedUtc = now
                });

                added++;
            }

            // Queue cap (R6): oldest dropped and counted — newest chat is what still matters.
            while (state.Stage2Pending.Count > relay.Stage2MaxPending)
            {
                state.Stage2Pending.RemoveAt(0);
                state.Stage2Dropped++;
            }

            return added;
        });

        if (initialising)
        {
            logger.LogInformation("Stage 2 cursor initialised at {Cursor}; history not queued", newCursor);
        }
        else if (enqueued > 0)
        {
            logger.LogInformation("Fetched {Enqueued} Discord message(s) into the Stage 2 queue", enqueued);
        }
    }

    private sealed record FetchedMessage(
        string Id,
        ulong NumericId,
        string? Content,
        string? GlobalName,
        string? Username,
        bool FromBotOrWebhook,
        bool HasAttachments,
        bool HasEmbeds,
        bool HasStickers,
        DateTimeOffset TimestampUtc,
        IReadOnlyDictionary<string, string> MentionNames);

    private static List<FetchedMessage> ParseMessages(string json)
    {
        var messages = new List<FetchedMessage>();

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected a message array");
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idProperty.GetString()!;

            if (!ulong.TryParse(id, out var numericId))
            {
                continue;
            }

            var fromBotOrWebhook = element.TryGetProperty("webhook_id", out _);
            string? globalName = null;
            string? username = null;

            if (element.TryGetProperty("author", out var author) &&
                author.ValueKind == JsonValueKind.Object)
            {
                if (author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
                {
                    fromBotOrWebhook = true;
                }

                if (author.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String)
                {
                    globalName = g.GetString();
                }

                if (author.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String)
                {
                    username = u.GetString();
                }
            }

            var timestamp = DateTimeOffset.UtcNow;

            if (element.TryGetProperty("timestamp", out var ts) &&
                ts.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ts.GetString(), out var parsed))
            {
                timestamp = parsed;
            }

            var mentionNames = new Dictionary<string, string>(StringComparer.Ordinal);

            if (element.TryGetProperty("mentions", out var mentions) &&
                mentions.ValueKind == JsonValueKind.Array)
            {
                foreach (var mention in mentions.EnumerateArray())
                {
                    if (mention.ValueKind != JsonValueKind.Object ||
                        !mention.TryGetProperty("id", out var mentionId) ||
                        mentionId.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name =
                        mention.TryGetProperty("global_name", out var mg) &&
                        mg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mg.GetString())
                            ? mg.GetString()!
                            : mention.TryGetProperty("username", out var mu) &&
                              mu.ValueKind == JsonValueKind.String
                                ? mu.GetString() ?? "someone"
                                : "someone";

                    mentionNames[mentionId.GetString()!] = name;
                }
            }

            messages.Add(new FetchedMessage(
                id,
                numericId,
                element.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : null,
                globalName,
                username,
                fromBotOrWebhook,
                HasItems(element, "attachments"),
                HasItems(element, "embeds"),
                HasItems(element, "sticker_items"),
                timestamp,
                mentionNames));
        }

        return messages;
    }

    private static bool HasItems(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() > 0;
}
