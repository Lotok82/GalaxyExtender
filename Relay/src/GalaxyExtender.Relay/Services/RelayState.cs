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

    /// <summary>Last-seen Discord message id for the Stage 2 read path. Unused until then.</summary>
    public string? Stage2Cursor { get; set; }
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
