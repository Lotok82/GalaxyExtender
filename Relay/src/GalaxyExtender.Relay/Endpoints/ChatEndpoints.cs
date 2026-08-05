using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Middleware;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Endpoints;

public static class ChatEndpoints
{
    /// <summary>Rate-limiting policy name, applied to the chat endpoint only.</summary>
    public const string RateLimitPolicy = "per-key";

    /// <summary>
    /// Advertises that accepted lines are validated and counted but not yet forwarded to Discord.
    /// A header rather than a response field so that turning forwarding on in Phase 3 is not a
    /// breaking change to the payload the extension parses.
    /// </summary>
    private const string ForwardingHeader = "X-Relay-Forwarding";

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // Authentication is applied by ApiKeyAuthenticationMiddleware on the /api prefix, not here —
        // see that class for why it is path-based rather than per-endpoint.
        app.MapPost("/api/v1/chat", (
                ChatBatchRequest? request,
                HttpContext http,
                IOptionsMonitor<RelayOptions> options,
                ILogger<ChatBatch> logger) =>
            {
                if (!ChatBatchValidator.TryValidate(request, options.CurrentValue, out var errors))
                {
                    logger.LogInformation(
                        "Rejected batch from key={KeyLabel}: {ErrorCount} validation error(s): {Fields}",
                        http.Items[ApiKeyAuthenticationMiddleware.KeyLabelItem],
                        errors.Count,
                        string.Join(", ", errors.Keys));

                    return Results.ValidationProblem(errors);
                }

                // Validation guarantees these.
                var batch = request!;
                var lines = batch.Lines!;

                // Phase 1 stops here: no de-duplication (Phase 2), no Discord (Phase 3), no outbox
                // (Phase 4). The endpoint exists now so the extension can be built and exercised
                // against the real contract without anything being able to reach the channel.
                logger.LogInformation(
                    "Accepted batch {BatchId} from key={KeyLabel} client={ClientId} character={Character}: {LineCount} line(s)",
                    batch.BatchId,
                    http.Items[ApiKeyAuthenticationMiddleware.KeyLabelItem],
                    batch.Client!.Id,
                    batch.Client.Character,
                    lines.Count);

                http.Response.Headers[ForwardingHeader] = "disabled";

                return Results.Ok(new ChatBatchResponse(
                    Accepted: lines.Count,
                    Deduped: 0,
                    Queued: 0,
                    RetryAfterMs: null));
            })
            .RequireRateLimiting(RateLimitPolicy);
    }

    /// <summary>Log category marker for chat batch handling.</summary>
    private sealed class ChatBatch;
}
