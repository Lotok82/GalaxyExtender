namespace GalaxyExtender.Relay.Contracts;

/// <summary>
/// A "still here" ping from one extension client (R11). Nullable for the same reason as
/// <see cref="ChatBatchRequest"/>: explicit validation produces a 400 naming the field, where a
/// `required` member would surface as a deserialisation failure the C++ side cannot act on.
///
/// Only <see cref="ChatClient.Id"/> is used — it is what distinguishes one install from another so
/// the relay can COUNT them. <c>character</c>/<c>galaxy</c> are accepted and ignored (the status
/// command reports numbers, not names), which keeps the shape identical to <c>/chat</c>'s client
/// block and lets an older client keep sending them.
/// </summary>
public sealed record PresenceRequest
{
    public ChatClient? Client { get; init; }
}

/// <summary>
/// What the relay knows about who is running the extension. Returned to the pinging client so the
/// in-game <c>/emu discord status</c> can show the same numbers the Discord bot reports, without a
/// second endpoint.
/// </summary>
/// <param name="Online">Clients that checked in inside <paramref name="OnlineWindowSeconds"/>.</param>
/// <param name="Known">Clients seen at all recently enough to still be counted as installed.</param>
/// <param name="OnlineWindowSeconds">The window the counts above were computed against.</param>
public sealed record PresenceResponse(int Online, int Known, int OnlineWindowSeconds);
