using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Middleware;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Endpoints;

/// <summary>
/// <c>POST /api/v1/presence</c> — "I am here" (R11).
///
/// This exists because nothing else on the wire answers "who has the extension running". A chat
/// batch only arrives when somebody talks, and the Stage 2 poll is gated client-side on the read
/// path being enabled, a live ground-scene frame tick AND a guild room id the player has caused to
/// be cached this session — so a silent guild, or a player who has not typed in the guild tab, looks
/// identical to nobody being online. The ping has no such conditions: while the bridge is active,
/// the client says so.
///
/// It is also the most reliable request stream the relay gets, so it drains the outbox and carries
/// the cleanup sweep and the command scan like every other authenticated request.
/// </summary>
public static class PresenceEndpoints
{
    public static void MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        // Authentication comes from ApiKeyAuthenticationMiddleware on the /api prefix, like /chat.
        app.MapPost("/api/v1/presence", async (
                PresenceRequest? request,
                HttpContext http,
                IOptionsMonitor<RelayOptions> relayOptions,
                PresenceTracker presence,
                BotCommandScanner commands,
                Outbox outbox,
                ChannelCleaner cleaner,
                ILogger<Presence> logger,
                CancellationToken cancellationToken) =>
            {
                if (!ChatBatchValidator.TryValidateClient(request?.Client, out var errors))
                {
                    logger.LogInformation("Rejected presence ping from key={KeyLabel}: {Fields}",
                        http.Items[ApiKeyAuthenticationMiddleware.KeyLabelItem],
                        string.Join(", ", errors.Keys));

                    return Results.ValidationProblem(errors);
                }

                // Validation guarantees this. `character`/`galaxy` are accepted and ignored: the
                // status command answers with counts, and older clients still send them.
                presence.Touch(request!.Client!.Id!);

                await outbox.DrainAsync(cancellationToken);
                await cleaner.SweepIfDueAsync(cancellationToken);
                await commands.ScanIfDueAsync(cancellationToken);

                var snapshot = presence.Snapshot();

                // Answering with the counts lets /emu discord status in game show the same figures
                // the Discord bot reports, without the extension needing a second endpoint.
                return Results.Ok(new PresenceResponse(
                    snapshot.Online,
                    snapshot.Known,
                    relayOptions.CurrentValue.PresenceOnlineWindowSeconds));
            })
            .RequireRateLimiting(ChatEndpoints.RateLimitPolicy);
    }

    /// <summary>Log category marker for presence pings.</summary>
    private sealed class Presence;
}
