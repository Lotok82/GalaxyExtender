using System.Text;
using System.Text.Json;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// The bridge bot answering <c>@GalaxyExtender status</c> in the bridge channel (R11).
///
/// There is no gateway connection, so "the bot is listening" is really "the relay reads the channel
/// whenever something drives it" — chat POSTs, Stage 2 polls, presence pings, the heartbeat, and
/// the <see cref="BackgroundTicker"/> — at most once per
/// <see cref="RelayOptions.CommandScanIntervalSeconds"/>, claimed atomically through the durable
/// <see cref="RelayState.LastCommandScanUtc"/> stamp exactly like the cleanup sweep. The ticker is
/// what makes "status" answerable when nobody is online at all — which is precisely when somebody
/// asks, and why the scan cannot be left to player traffic alone.
///
/// The scan keeps its OWN cursor, independent of the Stage 2 reader's, for two reasons: the status
/// command has to work with the Stage 2 read path switched off (asking whether the bridge is live is
/// most useful when it isn't), and the two paths must be able to run at different cadences.
///
/// Replies are at-MOST-once by construction: the cursor advances before anything is posted, so a
/// recycle mid-reply loses a reply rather than repeating one. For a chat channel that is the right
/// failure direction — a missed answer is invisible, a duplicated one is spam.
/// </summary>
public sealed class BotCommandScanner(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DiscordOptions> options,
    IOptionsMonitor<RelayOptions> relayOptions,
    IStateStore store,
    PresenceTracker presence,
    ILogger<BotCommandScanner> logger)
{
    /// <summary>Most messages examined per scan. Discord's own maximum per read is 100.</summary>
    private const int FetchLimit = 50;

    /// <summary>
    /// Runs a scan if the interval has elapsed. Never throws — answering a Discord command must
    /// never be the reason a chat batch or a poll fails.
    /// </summary>
    public async Task ScanIfDueAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.IsCommandsConfigured)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(relayOptions.CurrentValue.CommandScanIntervalSeconds);
        var now = DateTimeOffset.UtcNow;

        // Cheap read first: between scans the every-request check must not cost a state-file write.
        if (store.Read(state => state.LastCommandScanUtc) is { } last && now - last < interval)
        {
            return;
        }

        // Claim under the store lock. The stamp marks the ATTEMPT, so a failing Discord costs one
        // scan per interval rather than a retry storm on every request.
        var claimed = store.Mutate(state =>
        {
            if (state.LastCommandScanUtc is { } current && now - current < interval)
            {
                return false;
            }

            state.LastCommandScanUtc = now;
            return true;
        });

        if (!claimed)
        {
            return;
        }

        try
        {
            await ScanAsync(now, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Bot command scan failed: {Error}", ex.Message);
        }
    }

    private async Task ScanAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var botUserId = await ResolveBotUserIdAsync();

        if (botUserId is null)
        {
            // Without our own user id a mention cannot be recognised. Already logged.
            return;
        }

        var current = options.CurrentValue;
        var relay = relayOptions.CurrentValue;
        var cursor = store.Read(state => state.CommandCursor);

        // First run stamps "now" in message-id terms and answers NOTHING: enabling the command
        // path must not make the bot reply to every mention already sitting in the channel.
        var url = cursor is null
            ? $"channels/{current.ChannelId}/messages?limit=1"
            : $"channels/{current.ChannelId}/messages?after={Uri.EscapeDataString(cursor)}&limit={FetchLimit}";

        var body = await GetAsync(url, "command scan read");

        if (body is null)
        {
            return;
        }

        var fetched = DiscordMessageParser.Parse(body);

        if (fetched.Count == 0)
        {
            return;
        }

        // Discord's ordering differs between plain and ?after reads, so sort rather than trust it.
        fetched.Sort((a, b) => a.NumericId.CompareTo(b.NumericId));

        // Before any reply is posted, so a crash mid-reply cannot repeat it.
        store.Mutate<object?>(state =>
        {
            state.CommandCursor = fetched[^1].Id;
            return null;
        });

        if (cursor is null)
        {
            logger.LogInformation("Bot command cursor initialised at {Cursor}; existing mentions ignored",
                fetched[^1].Id);
            return;
        }

        var staleCutoff = now.AddSeconds(-relay.CommandMaxAgeSeconds);
        var replies = 0;

        foreach (var message in fetched)
        {
            // Our own replies and the forwarding webhook's posts are neither commands nor chat.
            if (message.FromBotOrWebhook)
            {
                continue;
            }

            var command = BotCommands.Mentions(message, botUserId)
                ? BotCommands.Parse(message.Content)
                : BotCommands.BotCommand.None;

            // Ordinary channel chat earns a word only when it is NOT reaching the guild room as
            // posted; the overwhelmingly common case is that it is, and the bot stays quiet.
            if (command == BotCommands.BotCommand.None && !ShouldNotice(message, current, relay, now))
            {
                continue;
            }

            // Something the relay only now caught up with — after a recycle, or after an evening
            // with nobody online — is answered by silence. A status line about a moment that has
            // passed is worse than no line at all, and telling somebody their message was not
            // delivered long after they stopped waiting on it is just noise.
            if (message.TimestampUtc < staleCutoff)
            {
                logger.LogDebug("Ignoring channel message from {Timestamp:O}: older than {MaxAge} s",
                    message.TimestampUtc, relay.CommandMaxAgeSeconds);
                continue;
            }

            // Matches the cleanup sweep: the caller's abort stops the scan from starting further
            // Discord calls, but never abandons one already in flight.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (replies >= relay.CommandMaxRepliesPerScan)
            {
                // The cursor has already moved past this message, so it is dropped rather than
                // deferred: a burst of mentions must not queue up a burst of bot posts.
                logger.LogWarning("Bot reply to {Author} dropped: {Max} replies already sent this scan",
                    message.GlobalName ?? message.Username, relay.CommandMaxRepliesPerScan);
                break;
            }

            var content = command switch
            {
                BotCommands.BotCommand.Status => StatusReport.Status(
                    presence.Snapshot(),
                    relay.PresenceOnlineWindowSeconds,
                    current.IsConfigured,
                    current.IsStage2Configured),
                BotCommands.BotCommand.Help => StatusReport.Help(),
                _ => StatusReport.DeliveryNotice(
                    presence.Snapshot(),
                    relay.PresenceOnlineWindowSeconds,
                    current.IsStage2Configured,
                    current.IsCleanupConfigured,
                    relay.CleanupMaxAgeHours)
            };

            // Counts the ATTEMPT, not the success. Discord failing wholesale (401/403 on the
            // channel, or a 429) fails every reply identically, so a success-counted cap would
            // let one scan issue up to FetchLimit POSTs and repeat that every interval. The
            // cursor has already moved past these messages, so a reply lost to a failure was
            // lost either way — spending budget on it costs nothing that was still recoverable.
            replies++;

            if (!await ReplyAsync(message.Id, content))
            {
                continue;
            }

            if (command == BotCommands.BotCommand.None)
            {
                // Stamped only after the notice actually landed, so a failed POST does not buy
                // silence for the whole interval.
                store.Mutate<object?>(state =>
                {
                    state.LastDeliveryNoticeUtc = now;
                    return null;
                });

                logger.LogInformation(
                    "Told the channel that {Author}'s message is not reaching the guild room",
                    message.GlobalName ?? message.Username ?? "someone");
            }
            else
            {
                logger.LogInformation("Answered bot command {Command} from {Author}",
                    command, message.GlobalName ?? message.Username ?? "unknown");
            }
        }
    }

    /// <summary>
    /// Whether ordinary channel chat deserves an unprompted "this is not being delivered" notice.
    ///
    /// Three gates, in cost order: it has to be a message that would actually have been relayed,
    /// it has to be genuinely undeliverable right now, and the channel must not have been told
    /// recently. The last one is what stops a conversation held while the guild is offline from
    /// being annotated line by line.
    /// </summary>
    private bool ShouldNotice(
        DiscordMessage message, DiscordOptions discord, RelayOptions relay, DateTimeOffset now)
    {
        // The same test the reader applies: nothing to inject means nothing to apologise for.
        if (Stage2Sanitizer.SanitizeText(message.Content, message.MentionNames,
                message.HasAttachments, message.HasEmbeds, message.HasStickers).Length == 0)
        {
            return false;
        }

        // Somebody is online with the read path on: this lands in the guild room within seconds.
        if (discord.IsStage2Configured && presence.Snapshot().Online > 0)
        {
            return false;
        }

        var interval = TimeSpan.FromMinutes(relay.DeliveryNoticeIntervalMinutes);

        return store.Read(state => state.LastDeliveryNoticeUtc) is not { } last ||
               now - last >= interval;
    }

    /// <summary>
    /// The bot's own user id, needed to tell "someone mentioned me" from "someone mentioned
    /// somebody". Configured value wins; otherwise it is discovered once from
    /// <c>GET /users/@me</c> and kept in durable state, so the operator does not have to copy an
    /// application id correctly for the feature to work at all.
    /// </summary>
    private async Task<string?> ResolveBotUserIdAsync()
    {
        // ConfiguredBotUserId rather than the raw option: the reader resolves the same identity
        // and the two must agree on what a blank value means, or a command gets answered here
        // AND injected into the guild room.
        if (options.CurrentValue.ConfiguredBotUserId is { } configured)
        {
            return configured;
        }

        if (store.Read(state => state.BotUserId) is { } known)
        {
            return known;
        }

        var body = await GetAsync("users/@me", "bot identity read");

        if (body is null)
        {
            return null;
        }

        string? id;

        try
        {
            using var document = JsonDocument.Parse(body);

            id = document.RootElement.ValueKind == JsonValueKind.Object &&
                 document.RootElement.TryGetProperty("id", out var value) &&
                 value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Bot identity read returned unparseable JSON: {Error}", ex.Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            logger.LogWarning("Bot identity read carried no user id; commands stay inactive");
            return null;
        }

        store.Mutate<object?>(state =>
        {
            state.BotUserId = id;
            return null;
        });

        logger.LogInformation("Bot user id discovered: {BotUserId}", id);

        return id;
    }

    /// <summary>Bot-authenticated GET. Returns null on any failure, having logged it.</summary>
    private async Task<string?> GetAsync(string url, string what)
    {
        var client = httpClientFactory.CreateClient(DiscordReader.HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bot {options.CurrentValue.BotToken}");

        // Deliberately not the request token: the caller's abort must not leave a half-done scan
        // that has already consumed its interval stamp.
        using var response = await client.SendAsync(request, CancellationToken.None);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync(CancellationToken.None);
        }

        // 401/403 = bad token or missing channel access; 429 = rate limited. All resolve the same
        // way: log, skip this round, try again after the interval.
        logger.LogWarning("Discord {What} failed with HTTP {Status}", what, (int)response.StatusCode);

        return null;
    }

    /// <summary>
    /// Posts the answer as a reply to the command, so a busy channel makes clear what was asked.
    /// <c>allowed_mentions.parse: []</c> for the same reason the webhook carries it — nothing the
    /// relay authors, including a self-reported character name, can ping anyone.
    /// <c>fail_if_not_exists: false</c> keeps the reply working if the command was deleted in the
    /// meantime (or swept by the cleanup) instead of failing the POST.
    /// </summary>
    private async Task<bool> ReplyAsync(string messageId, string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content,
            allowed_mentions = new { parse = Array.Empty<string>() },
            message_reference = new { message_id = messageId, fail_if_not_exists = false }
        });

        var client = httpClientFactory.CreateClient(DiscordReader.HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"channels/{options.CurrentValue.ChannelId}/messages")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bot {options.CurrentValue.BotToken}");

        using var response = await client.SendAsync(request, CancellationToken.None);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        logger.LogWarning("Bot reply failed with HTTP {Status}", (int)response.StatusCode);

        return false;
    }
}
