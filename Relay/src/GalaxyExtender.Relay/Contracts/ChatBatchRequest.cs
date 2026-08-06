namespace GalaxyExtender.Relay.Contracts;

/// <summary>
/// A batch of guild chat lines from one extension client.
///
/// Every member is nullable despite being required by the contract: explicit validation produces a
/// 400 naming the offending field, whereas `required` members would surface as a deserialisation
/// failure with a body the C++ side cannot act on.
/// </summary>
public sealed record ChatBatchRequest
{
    /// <summary>
    /// GUID identifying this batch, REUSED unchanged when the client retries after a timeout or
    /// 5xx. That is what makes retries idempotent — a fresh GUID on retry double-posts.
    /// </summary>
    public string? BatchId { get; init; }

    public ChatClient? Client { get; init; }

    public IReadOnlyList<ChatLine>? Lines { get; init; }
}

/// <summary>
/// Self-reported client identity. Used for logging and the optional debug embed field only —
/// this is NOT authentication and must not be trusted. Anyone holding a valid key can claim any
/// character name.
/// </summary>
public sealed record ChatClient
{
    public string? Id { get; init; }

    public string? Character { get; init; }

    public string? Galaxy { get; init; }
}

public sealed record ChatLine
{
    /// <summary>Chat text with SWG colour/format escapes already stripped by the client.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// How many times this client has seen this exact line in the last 60 s, including this one.
    /// Every guild member's client watches the same stream, so all of them independently label the
    /// first "lol" as 1 and the second as 2 — which is what lets de-duplication collapse
    /// cross-client copies while still letting a genuine repeat through. Must be >= 1.
    /// </summary>
    public int? Occurrence { get; init; }

    /// <summary>Monotonic per client. Ordering and debugging only.</summary>
    public long? ClientSeq { get; init; }
}
