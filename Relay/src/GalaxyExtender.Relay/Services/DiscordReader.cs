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
    BotCommandScanner commands,
    GuildNicknames nicknames,
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
        // Only suppress what something is actually going to answer. The discovered id is durable
        // and outlives CommandsEnabled being switched back off, so without the gate a relay that
        // once had commands on makes "@bot status" vanish from the guild room with nothing
        // replying in Discord either.
        var (cursor, botUserId) = store.Read(state => (
            state.Stage2Cursor,
            current.IsCommandsConfigured ? current.ConfiguredBotUserId ?? state.BotUserId : null));

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

        List<DiscordMessage> fetched;

        try
        {
            fetched = DiscordMessageParser.Parse(body);
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

        // Suppression must not outlive answerability: the scan skips mentions older than
        // CommandMaxAgeSeconds, so a mention past that age (a backlog after a recycle) is
        // ordinary chat here or it would vanish — unanswered AND undelivered.
        var staleCutoff = now.AddSeconds(-relay.CommandMaxAgeSeconds);

        var mentionSuppressed = false;

        // Server nicknames for everyone this page names — speakers and mentioned users alike —
        // resolved BEFORE the store lock, because the lookups are HTTP and the mutate is not a
        // place to be doing HTTP. Empty whenever the feature is off, unavailable, or already
        // cached as "nobody here has one"; the sanitizer falls back to the account name per id.
        // Nothing is queued on the first run, so nothing is worth looking up on it either.
        var nicknamesById = initialising
            ? GuildNicknames.None
            : await nicknames.ResolveAsync(GuildNicknames.IdsIn(
                fetched.Where(message => !message.FromBotOrWebhook)));

        var enqueued = store.Mutate(state =>
        {
            state.Stage2Cursor = newCursor;

            if (initialising)
            {
                return 0;
            }

            var entries = new List<PendingEntry>();

            foreach (var message in fetched)
            {
                // R4, the echo filter: our webhook's posts (and any bot's) never re-enter.
                if (message.FromBotOrWebhook)
                {
                    continue;
                }

                // R11: "@GalaxyExtender status" — or any addressed mention, now that the eight
                // ball answers the rest — is bot conversation, not guild chat. The command scan
                // answers it in Discord and queues the eight-ball exchange itself; injecting the
                // raw mention here too would duplicate it. IsAddressed is the SAME predicate the
                // scan uses, so the two paths cannot disagree about a message; the freshness
                // bound mirrors the scan's stale skip for the same reason.
                if (botUserId is not null &&
                    message.TimestampUtc >= staleCutoff &&
                    BotCommands.IsAddressed(message, botUserId))
                {
                    mentionSuppressed = true;
                    continue;
                }

                var text = Stage2Sanitizer.SanitizeText(
                    message.Content, GuildNicknames.Merge(message.MentionNames, nicknamesById),
                    message.HasAttachments, message.HasEmbeds, message.HasStickers);

                if (text.Length == 0)
                {
                    continue;
                }

                entries.Add(new PendingEntry
                {
                    Id = message.Id,
                    Author = Stage2Sanitizer.SanitizeAuthor(
                        nicknamesById.GetValueOrDefault(message.AuthorId ?? string.Empty),
                        message.GlobalName, message.Username),
                    Text = text,
                    TimestampUtc = message.TimestampUtc,
                    ReceivedUtc = now
                });
            }

            Stage2Queue.Enqueue(state, relay.Stage2MaxPending, entries);

            return entries.Count;
        });

        if (initialising)
        {
            logger.LogInformation("Stage 2 cursor initialised at {Cursor}; history not queued", newCursor);
        }
        else if (enqueued > 0)
        {
            logger.LogInformation("Fetched {Enqueued} Discord message(s) into the Stage 2 queue", enqueued);
        }

        // Suppressing the mention was a promise that the command scan answers it; this is what
        // keeps the promise prompt. The scan runs on the caller's request path (the endpoints call
        // it after this fetch), regardless of its interval — see NoteAddressedMention.
        if (mentionSuppressed)
        {
            commands.NoteAddressedMention();
        }
    }
}
