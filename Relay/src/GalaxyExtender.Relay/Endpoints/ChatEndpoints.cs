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
    /// Advertises whether accepted lines reach Discord. A header rather than a response field so
    /// that turning forwarding on was not a breaking change to the payload the extension parses.
    /// </summary>
    private const string ForwardingHeader = "X-Relay-Forwarding";

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // Authentication is applied by ApiKeyAuthenticationMiddleware on the /api prefix, not here —
        // see that class for why it is path-based rather than per-endpoint.
        app.MapPost("/api/v1/chat", async (
                ChatBatchRequest? request,
                HttpContext http,
                IOptionsMonitor<RelayOptions> relayOptions,
                IOptionsMonitor<DiscordOptions> discordOptions,
                DedupeService dedupe,
                DiscordPublisher publisher,
                Outbox outbox,
                Stage2Queue stage2Queue,
                ChannelCleaner cleaner,
                ILogger<ChatBatch> logger,
                CancellationToken cancellationToken) =>
            {
                if (!ChatBatchValidator.TryValidate(request, relayOptions.CurrentValue, out var errors))
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
                var batchId = batch.BatchId!;
                var lines = batch.Lines!;
                var clientId = batch.Client!.Id;

                if (!discordOptions.CurrentValue.IsConfigured)
                {
                    // Contract: 503 when the webhook is not configured. Deliberately BEFORE any
                    // state mutation — an unconfigured relay must not eat lines into the dedupe
                    // window it will never forward.
                    return Results.Problem(
                        title: "Discord webhook not configured on the relay.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                // Opportunistic drain: anything a previous request failed to deliver goes first,
                // preserving order as closely as this host allows.
                await outbox.DrainAsync(cancellationToken);

                // Piggybacked channel cleanup (R10) — a no-op between sweeps.
                await cleaner.SweepIfDueAsync(cancellationToken);

                // Dedupe on the normalised form; forward the display form. The key MUST come from
                // the normalised text or two clients could disagree after presentation escaping.
                //
                // Marked lines — a bridged Discord message re-entering through the Stage 1
                // capture — peel off first: they are the Stage 2 delivery ack and are NEVER
                // forwarded to Discord, matched or not (stage2 plan, "Marker and echo rules").
                // Every relaying client sends its own copy; acking is idempotent.
                var markedLines = 0;
                var prepared = new List<(string Key, string Display)>(lines.Count);

                foreach (var line in lines)
                {
                    var normalized = TextSanitizer.Normalize(line.Text!);

                    if (stage2Queue.TryAckMarkedLine(normalized))
                    {
                        markedLines++;
                        continue;
                    }

                    prepared.Add((
                        DedupeService.Key(normalized, line.Occurrence!.Value),
                        TextSanitizer.ForDiscord(normalized, relayOptions.CurrentValue.MaxLineLength)));
                }

                var admission = dedupe.Admit(batchId, clientId, prepared);

                if (admission.ReplayedResponse is not null)
                {
                    logger.LogInformation("Replayed batch {BatchId} from key={KeyLabel} (client retry)",
                        batchId, http.Items[ApiKeyAuthenticationMiddleware.KeyLabelItem]);

                    http.Response.Headers[ForwardingHeader] = "enabled";
                    return Results.Ok(admission.ReplayedResponse);
                }

                var accepted = 0;
                var queued = 0;
                int? retryAfterMs = null;

                if (admission.UniqueLines.Count > 0)
                {
                    var chunks = TextSanitizer.BuildDescriptions(admission.UniqueLines);
                    var failed = false;

                    foreach (var (text, lineCount) in chunks)
                    {
                        var payload = publisher.BuildPayload(text, clientId);

                        if (!failed)
                        {
                            // Deliberately NOT the request token. Once the batch is admitted the
                            // only safe exit is through Park/Complete below: an extension timeout
                            // mid-POST must not abandon the batch half-processed (its retry would
                            // see every line as a dedupe hit), and an abort after Discord accepted
                            // must not park a payload that would then post twice. The webhook
                            // client's own timeout bounds the wait.
                            var result = await publisher.PostAsync(payload, CancellationToken.None);

                            if (result.Success)
                            {
                                accepted += lineCount;
                                continue;
                            }

                            failed = true;

                            if (result.RetryAfter is { } retryAfter)
                            {
                                // Pace the client too — the extension honours retryAfterMs.
                                retryAfterMs = (int)Math.Min(retryAfter.TotalMilliseconds, 900_000);
                            }
                        }

                        // First failure parks this and every later chunk, keeping order.
                        outbox.Park(payload, lineCount,
                            retryAfterMs is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(10));
                        queued += lineCount;
                    }
                }

                // Marked lines count as accepted — the relay took responsibility for them (as
                // acks), it just never forwards them. Keeps the client's counters honest.
                var response = new ChatBatchResponse(
                    accepted + markedLines, admission.Deduped, queued, retryAfterMs);
                dedupe.Complete(batchId, response, forwardedSomething: accepted > 0);

                logger.LogInformation(
                    "Batch {BatchId} from key={KeyLabel} client={ClientId} character={Character}: " +
                    "{Accepted} forwarded, {Deduped} deduped, {Queued} queued, {Marked} marked",
                    batchId,
                    http.Items[ApiKeyAuthenticationMiddleware.KeyLabelItem],
                    clientId,
                    batch.Client.Character,
                    accepted,
                    admission.Deduped,
                    queued,
                    markedLines);

                http.Response.Headers[ForwardingHeader] = "enabled";

                return Results.Ok(response);
            })
            .RequireRateLimiting(RateLimitPolicy);

        // Authenticated no-op that drains the outbox and keeps the app pool warm. Cheap insurance
        // given idle-stop: a cron/pinger POSTing here with the key both prevents cold starts and
        // delivers anything a 429 parked when no chat followed it.
        app.MapPost("/api/v1/heartbeat", async (
            Outbox outbox, ChannelCleaner cleaner, CancellationToken cancellationToken) =>
        {
            await outbox.DrainAsync(cancellationToken);
            await cleaner.SweepIfDueAsync(cancellationToken);
            return Results.Ok(new { outbox = outbox.Depth });
        });
    }

    /// <summary>Log category marker for chat batch handling.</summary>
    private sealed class ChatBatch;
}
