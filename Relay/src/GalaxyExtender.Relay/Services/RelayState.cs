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
    /// <summary>Discord snowflake — unique, ascending, and the claim key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Sanitized display name, ≤ 32 chars.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Sanitized message text, ≤ 200 chars.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When Discord recorded the message.</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>When the relay fetched it — the TTL runs from here, not from Discord's stamp.</summary>
    public DateTimeOffset ReceivedUtc { get; set; }

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
}
