namespace GalaxyExtender.Relay.Contracts;

/// <summary>
/// Result of a <c>POST /api/v1/chat</c>. Shape is fixed from Phase 1 so the extension does not need
/// changing as later phases start populating the currently-static fields.
/// </summary>
/// <param name="Accepted">Lines taken for forwarding.</param>
/// <param name="Deduped">Lines recognised as already seen. Always 0 until Phase 2.</param>
/// <param name="Queued">Lines parked in the durable outbox. Always 0 until Phase 4.</param>
/// <param name="RetryAfterMs">Set when the client should slow down; null otherwise.</param>
public sealed record ChatBatchResponse(
    int Accepted,
    int Deduped,
    int Queued,
    int? RetryAfterMs);
