namespace GalaxyExtender.Relay.Contracts;

/// <summary>
/// Result of a <c>GET /api/v1/messages</c> poll — a work-queue CONSUME, not a broadcast read: a
/// 200 claims every returned message for the polling key+client until the redelivery timeout.
/// Shape is fixed by the R1 stub so the extension's poll path does not need changing when the
/// real Discord read (R3) starts populating it.
/// </summary>
/// <param name="Messages">Claimed messages, oldest first. Empty while Stage 2 is disabled.</param>
/// <param name="Dropped">
/// Messages discarded (TTL expiry or redelivery cap) since the last poll that reported them.
/// Report-once: each loss reaches exactly one poller.
/// </param>
public sealed record MessagesResponse(
    IReadOnlyList<PendingMessage> Messages,
    int Dropped);

/// <summary>A Discord message pending injection into the guild room.</summary>
/// <param name="Id">Discord snowflake. Unique, ascending — also the claim key.</param>
/// <param name="Author">Display name, sanitized and clamped by the relay.</param>
/// <param name="Text">
/// Sanitized and pre-clamped so the full injected line <c>[Discord] &lt;author&gt;: &lt;text&gt;</c>
/// fits the game-safe room-message length — the client injects verbatim, never truncates.
/// </param>
/// <param name="TimestampUtc">When Discord recorded the message.</param>
public sealed record PendingMessage(
    string Id,
    string Author,
    string Text,
    DateTimeOffset TimestampUtc);
