using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Middleware;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Endpoints;

/// <summary>
/// Stage 2 (Discord → game) work queue. <c>GET /api/v1/messages</c> is a CONSUME: a 200 claims
/// the returned messages for the polling key+client until the redelivery timeout — see the
/// "Stage 2" section of README.md for the pinned contract.
///
/// R3 live: when Stage 2 is configured (bot token + channel + enabled flag), each poll first
/// gives <see cref="DiscordReader"/> a chance to refresh the pending queue (rate-capped by its
/// cache window, so the Discord-facing request rate is independent of player count), then claims
/// from <see cref="Stage2Queue"/>. Unconfigured behaves exactly like the R1 stub: 200 + empty +
/// <c>X-Relay-Stage2: disabled</c>, never 503 — bridge-off is the ordinary idle case.
/// </summary>
public static class MessagesEndpoints
{
    /// <summary>
    /// Advertises whether the Discord read path is live. A header rather than a response field
    /// for the same reason as X-Relay-Forwarding: flipping it on must not be a breaking change
    /// to the payload the extension parses.
    /// </summary>
    private const string Stage2Header = "X-Relay-Stage2";

    public static void MapMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        // Authentication comes from ApiKeyAuthenticationMiddleware on the /api prefix, like /chat.
        app.MapGet("/api/v1/messages", async (
                string? client,
                HttpContext http,
                IOptionsMonitor<DiscordOptions> discordOptions,
                DiscordReader reader,
                Stage2Queue queue,
                Outbox outbox,
                ChannelCleaner cleaner,
                CancellationToken cancellationToken) =>
            {
                if (!TryValidateClient(client, out var errors))
                {
                    return Results.ValidationProblem(errors);
                }

                if (!discordOptions.CurrentValue.IsStage2Configured)
                {
                    // Contract: an unconfigured Stage 2 is the ordinary idle case, not an error —
                    // 200 + empty + "disabled", never 503, so the poll loop needs no special casing.
                    http.Response.Headers[Stage2Header] = "disabled";

                    return Results.Ok(new MessagesResponse([], Dropped: 0));
                }

                // Polls are the steadiest request stream this host sees; letting them drain the
                // outbox gets parked game → Discord lines delivered even when nobody is chatting.
                // The channel cleanup (R10) rides the same stream, a no-op between sweeps.
                await outbox.DrainAsync(cancellationToken);
                await cleaner.SweepIfDueAsync(cancellationToken);

                await reader.FetchIfDueAsync(cancellationToken);

                var claimant = $"{http.Items[ApiKeyAuthenticationMiddleware.KeyLabelItem]}:{client}";
                var response = queue.Claim(claimant);

                http.Response.Headers[Stage2Header] = "enabled";

                return Results.Ok(response);
            })
            .RequireRateLimiting(ChatEndpoints.RateLimitPolicy);
    }

    /// <summary>
    /// `client` attributes claims for redelivery accounting and logging (the GET has no body to
    /// carry /chat's client.id, so it travels as a query parameter). Same rules as client.id;
    /// it is not authentication.
    /// </summary>
    private static bool TryValidateClient(string? client, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(client))
        {
            errors["client"] = ["Required."];
        }
        else if (client.Length > ChatBatchValidator.MaxIdentifierLength)
        {
            errors["client"] = [$"Must be {ChatBatchValidator.MaxIdentifierLength} characters or fewer."];
        }
        else if (ChatBatchValidator.ContainsControlCharacters(client))
        {
            errors["client"] = [ChatBatchValidator.ControlCharacterError];
        }

        return errors.Count == 0;
    }
}
