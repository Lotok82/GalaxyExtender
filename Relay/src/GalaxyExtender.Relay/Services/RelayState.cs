using GalaxyExtender.Relay.Contracts;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// The single durable state document, persisted as JSON under <c>App_Data</c>. Everything in here
/// must survive an app-pool recycle or idle-stop — that is the whole reason it is a file and not
/// memory. See discord-relay-plan.md "StateStore — the crux".
/// </summary>
public sealed class RelayState
{
    /// <summary>Lines seen inside the dedupe window. First arrival wins and is forwarded.</summary>
    public List<DedupeEntry> Dedupe { get; set; } = [];

    /// <summary>
    /// Recently processed batch ids with the response each produced. A client retrying after a
    /// timeout replays the stored response instead of double-posting.
    /// </summary>
    public List<BatchEntry> Batches { get; set; } = [];

    /// <summary>Discord posts that have not landed yet, drained at the start of requests.</summary>
    public List<OutboxEntry> Outbox { get; set; } = [];

    /// <summary>Last time a webhook POST succeeded. Reported by /health.</summary>
    public DateTimeOffset? LastForwardUtc { get; set; }

    /// <summary>
    /// Last-seen Discord message id for the Stage 2 read path. Advances past every fetched
    /// message, filtered or not, so bot/webhook echoes are never re-examined.
    /// </summary>
    public string? Stage2Cursor { get; set; }

    /// <summary>Discord messages awaiting injection into the guild room (Stage 2 work queue).</summary>
    public List<PendingEntry> Stage2Pending { get; set; } = [];

    /// <summary>
    /// Monotonic admission counter for <see cref="Stage2Pending"/>. <see cref="Stage2Queue"/>'s
    /// enqueue stamps each entry's <see cref="PendingEntry.Sequence"/> from it, so claim order
    /// stays deterministic even between entries whose ids tie or cannot be parsed.
    /// </summary>
    public long Stage2Sequence { get; set; }

    /// <summary>
    /// Messages lost since the last poll that reported them (TTL expiry or redelivery cap).
    /// Report-once: handed to exactly one poller and reset to zero.
    /// </summary>
    public int Stage2Dropped { get; set; }

    /// <summary>
    /// When the last channel-history cleanup sweep (R10) started, successful or not. The stamp
    /// doubles as the sweep claim: it is advanced atomically under the store lock before any
    /// Discord call, so concurrent requests cannot both pay for a sweep.
    /// </summary>
    public DateTimeOffset? LastCleanupUtc { get; set; }

    /// <summary>
    /// Every extension client the relay has heard from, with when it was last alive (R11). Durable
    /// because "how many people have this installed" must survive a recycle — an app pool that
    /// idle-stops overnight would otherwise report an empty guild every morning.
    /// </summary>
    public List<PresenceEntry> Presence { get; set; } = [];

    /// <summary>
    /// Last channel message id examined by the bot-command scan (R11). Separate from
    /// <see cref="Stage2Cursor"/> on purpose: the status command answers whether or not the Stage 2
    /// read path is switched on, so the two paths cannot share a queue position.
    /// </summary>
    public string? CommandCursor { get; set; }

    // The bot-command scan's interval claim used to live here as LastCommandScanUtc; it is
    // in-memory in BotCommandScanner now (a durable stamp cost a full state-file write per scan
    // and bought only the avoidance of one early scan after a recycle). Old state files still
    // carrying the field deserialize fine — unknown JSON properties are ignored.

    /// <summary>
    /// The bot's own Discord user id, discovered once from <c>GET /users/@me</c> so that mentions of
    /// it can be recognised. Cached durably to keep that call off every scan; the operator can
    /// override it with <c>Discord:BotUserId</c>, which then takes precedence over this.
    /// </summary>
    public string? BotUserId { get; set; }

    /// <summary>
    /// The bot's username as <c>GET /users/@me</c> reported it, latched alongside
    /// <see cref="BotUserId"/>. Last-resort author for an injected eight-ball answer when the
    /// reply POST's response carried no author and the question's mention entry is missing too —
    /// better than the generic "discord" placeholder. The reply's own author still wins, so a
    /// rename shows up as soon as Discord reports it.
    /// </summary>
    public string? BotUserName { get; set; }

    /// <summary>
    /// The guild the bridge channel belongs to, discovered once from <c>GET /channels/{id}</c> so
    /// that per-server nicknames can be read. Cached durably for the same reason as
    /// <see cref="BotUserId"/>: it cannot change unless the channel id does, so paying for the
    /// lookup more than once would be paying for nothing. Overridable with <c>Discord:GuildId</c>,
    /// which then takes precedence over this.
    /// </summary>
    public string? GuildId { get; set; }

    /// <summary>
    /// Server nicknames the relay has read, so the guild room can name people the way the guild
    /// knows them without asking Discord about the same person twice.
    ///
    /// Durable rather than in-memory precisely because a nickname is a thing people change a
    /// handful of times a year: this app pool idle-stops, and an in-memory cache would re-read
    /// every speaker after each cold start — paying, over and over, for an answer that had not
    /// changed. See <see cref="GuildNicknames"/> for the refresh window and the size bound.
    /// </summary>
    public List<NicknameEntry> Nicknames { get; set; } = [];

    /// <summary>
    /// When the bot last told the channel that a message was not going to reach the guild room as
    /// posted. Durable so a recycle cannot turn one notice into one per app start, and rate-limited
    /// by <see cref="RelayOptions.DeliveryNoticeIntervalMinutes"/>.
    /// </summary>
    public DateTimeOffset? LastDeliveryNoticeUtc { get; set; }

    /// <summary>
    /// When the world boss alert feed last pinged its role. Durable for the same reason as
    /// <see cref="LastDeliveryNoticeUtc"/> and more sharply: this app pool idle-stops, so an
    /// in-memory window would grant a fresh ping on every cold start — precisely when a quiet
    /// spell has just ended. Advanced atomically under the store lock, so it is the claim as well
    /// as the record. See <see cref="AlertPingThrottle"/>.
    /// </summary>
    public DateTimeOffset? LastAlertPingUtc { get; set; }

    /// <summary>
    /// When a world boss alert last passed through the relay — stamped the moment an alert line is
    /// admitted for forwarding, whether the webhook POST succeeds immediately or the payload is
    /// parked in the outbox. Distinct from <see cref="LastAlertPingUtc"/> on purpose: that stamp is
    /// the role-ping throttle CLAIM (it skips pings inside the quiet window and is handed back when
    /// a parked ping is dropped), so it cannot answer "when was the last alert?" — an alert that
    /// published silently, or whose ping was released, would go missing from the answer. Reported
    /// by the bot's status reply.
    /// </summary>
    public DateTimeOffset? LastAlertUtc { get; set; }
}

/// <summary>
/// One Discord user's server nickname, as it stood when it was read. A null <see cref="Nick"/> is
/// a real answer — "this member has no nickname" — and is kept just as firmly as a name, because
/// having none is the common case and re-asking about it would cost the most.
/// </summary>
public sealed class NicknameEntry
{
    /// <summary>The Discord user id this is about.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Their nickname in the bridge channel's guild, or null if they have none.</summary>
    public string? Nick { get; set; }

    /// <summary>
    /// When Discord was asked. The refresh window runs from here, and it doubles as the eviction
    /// key when the list is trimmed — the least recently refreshed entry belongs to whoever has
    /// been quiet longest.
    /// </summary>
    public DateTimeOffset FetchedUtc { get; set; }
}

/// <summary>
/// One extension client's presence record: an id and when it was last alive, nothing else. The
/// status command reports COUNTS, so no character or galaxy label is kept — there is nothing to
/// leak and nothing to keep up to date.
/// </summary>
public sealed class PresenceEntry
{
    /// <summary>
    /// The client's self-reported id: a hash of the machine's Windows installation id, optionally
    /// behind a readable prefix from its ini. It is what separates one install from another — the
    /// extension makes it unique by construction so no player's configuration can collapse the
    /// count. Not authentication, and never shown to anyone.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>First time this client id was ever seen — how long they have been running it.</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>Last check-in. Inside the presence window this client counts as online.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class DedupeEntry
{
    /// <summary>sha256(normalised text)[..16] + ":" + occurrence.</summary>
    public string Key { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>Self-reported client id of the first arrival. Logging only.</summary>
    public string? FirstSeenBy { get; set; }
}

public sealed class BatchEntry
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset SeenUtc { get; set; }

    public ChatBatchResponse? Response { get; set; }
}

/// <summary>One Discord message in the Stage 2 work queue, with its claim bookkeeping.</summary>
public sealed class PendingEntry
{
    /// <summary>
    /// Discord snowflake — the claim key, and the primary claim-order key. Almost always the
    /// message's real id; the one exception is an eight-ball answer whose reply POST returned an
    /// unreadable body, where <see cref="BotCommandScanner"/> fabricates one past the question's
    /// id. That fabricated value can in principle collide with a real later snowflake, which is
    /// why <see cref="Sequence"/> — not id arithmetic — is what breaks ties.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Sanitized display name, ≤ 32 chars.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Sanitized message text, ≤ 200 chars.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When Discord recorded the message.</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>When the relay fetched it — the TTL runs from here, not from Discord's stamp.</summary>
    public DateTimeOffset ReceivedUtc { get; set; }

    /// <summary>
    /// Admission order, stamped by <see cref="Stage2Queue"/>'s enqueue from
    /// <see cref="RelayState.Stage2Sequence"/>. Claim order is id first, then this — it is what
    /// keeps an eight-ball answer directly after its question even when the answer's id had to be
    /// fabricated. Entries persisted before the field existed read as 0 and simply keep id order.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Claims handed out so far (initial delivery + redeliveries).</summary>
    public int Deliveries { get; set; }

    /// <summary>Start of the current claim; null when unclaimed or the claim expired and reset.</summary>
    public DateTimeOffset? ClaimedUtc { get; set; }

    /// <summary>key label + client of the current claimant. Logging/redelivery accounting only.</summary>
    public string? ClaimedBy { get; set; }
}

public sealed class OutboxEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The exact webhook payload JSON that failed to send.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>How many chat lines the payload carries — for the response's queued count.</summary>
    public int LineCount { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NotBeforeUtc { get; set; }

    /// <summary>
    /// Set when this payload carries the world boss alert role mention, to the stamp that claimed
    /// the ping window (<see cref="RelayState.LastAlertPingUtc"/>). Null on every other entry.
    ///
    /// It exists so the claim can be handed back if this entry is dropped instead of delivered: a
    /// ping nobody received must not go on costing the next alert its own. Kept as the stamp rather
    /// than a flag so the release can check the window is still the one this entry claimed, and not
    /// a newer ping's. See <see cref="Outbox"/>.
    /// </summary>
    public DateTimeOffset? AlertPingStampUtc { get; set; }
}
