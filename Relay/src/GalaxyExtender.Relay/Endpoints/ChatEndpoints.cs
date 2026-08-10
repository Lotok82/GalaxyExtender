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
                AlertRules alerts,
                DiscordPublisher publisher,
                Outbox outbox,
                Stage2Queue stage2Queue,
                ChannelCleaner cleaner,
                PresenceTracker presence,
                BotCommandScanner commands,
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

                // A client sending chat is unambiguously alive, so the presence stamp is refreshed
                // even on the paths that reject below: it costs nothing, and it keeps the status
                // command honest for a client on a build older than the presence ping.
                presence.Touch(clientId!);

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

                // Piggybacked channel cleanup (R10) and bot-command scan (R11) — both no-ops
                // between their intervals.
                await cleaner.SweepIfDueAsync(cancellationToken);
                await commands.ScanIfDueAsync(cancellationToken);

                // Dedupe on the normalised form; forward the display form. The key MUST come from
                // the normalised text or two clients could disagree after presentation escaping.
                //
                // Marked lines — a bridged Discord message re-entering through the Stage 1
                // capture — peel off first: they are the Stage 2 delivery ack and are NEVER
                // forwarded to Discord, matched or not (stage2 plan, "Marker and echo rules").
                // Every relaying client sends its own copy; acking is idempotent.
                var markedLines = 0;
                var prepared = new List<DedupeService.PreparedLine>(lines.Count);

                foreach (var line in lines)
                {
                    var normalized = TextSanitizer.Normalize(line.Text!);

                    if (stage2Queue.TryAckMarkedLine(normalized))
                    {
                        markedLines++;
                        continue;
                    }

                    // Classify before sanitising: the two destinations escape differently, and the
                    // tag would not survive being escaped for the wrong one.
                    var alert = alerts.Match(normalized);

                    prepared.Add(new DedupeService.PreparedLine(
                        DedupeService.Key(normalized, line.Occurrence!.Value),
                        TextSanitizer.ForDiscord(
                            normalized,
                            relayOptions.CurrentValue.MaxLineLength,
                            alert is null ? DiscordTarget.PlainMessage : DiscordTarget.Embed),
                        alert));
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
                    var chunks = BuildPayloads(admission.UniqueLines, publisher, clientId);
                    var failed = false;

                    foreach (var (payload, lineCount) in chunks)
                    {
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
        //
        // It also carries the bot-command scan, which is what makes "@bot status" answerable when
        // NOBODY is online — the case where the question actually gets asked. With no player
        // traffic, the pinger's cadence is the bot's response time.
        app.MapPost("/api/v1/heartbeat", async (
            Outbox outbox,
            ChannelCleaner cleaner,
            BotCommandScanner commands,
            CancellationToken cancellationToken) =>
        {
            await outbox.DrainAsync(cancellationToken);
            await cleaner.SweepIfDueAsync(cancellationToken);
            await commands.ScanIfDueAsync(cancellationToken);
            return Results.Ok(new { outbox = outbox.Depth });
        });
    }

    /// <summary>
    /// Turns admitted lines into webhook payloads, preserving arrival order.
    ///
    /// Consecutive lines that render the same way share a payload; a change of rendering starts a
    /// new one. Grouping all chat together and all alerts together would be fewer POSTs, but it
    /// would also reorder a batch against the order the guild actually said things. Alerts are
    /// rare, so in practice this is one payload per batch exactly as before, and an alert simply
    /// splits the batch around itself.
    ///
    /// The two renderings differ in more than colour: chat is a plain message capped at Discord's
    /// 2000-character `content` limit, an alert is an embed description capped at 4096.
    /// </summary>
    private static List<(string Payload, int LineCount)> BuildPayloads(
        IReadOnlyList<DedupeService.PreparedLine> lines,
        DiscordPublisher publisher,
        string? clientId)
    {
        var payloads = new List<(string, int)>();
        var index = 0;

        while (index < lines.Count)
        {
            // Colour is the grouping key rather than the rule: two tags sharing a colour can share
            // an embed, and null means ordinary chat.
            var color = lines[index].Alert?.Color;
            var run = new List<string>();

            while (index < lines.Count && lines[index].Alert?.Color == color)
            {
                run.Add(lines[index].DisplayText);
                index++;
            }

            var limit = color is null ? TextSanitizer.MaxContentLength : TextSanitizer.MaxDescriptionLength;

            foreach (var (text, lineCount) in TextSanitizer.BuildChunks(run, limit))
            {
                payloads.Add((
                    color is null
                        ? publisher.BuildPayload(text, clientId)
                        : publisher.BuildEmbedPayload(text, color.Value, clientId),
                    lineCount));
            }
        }

        return payloads;
    }

    /// <summary>Log category marker for chat batch handling.</summary>
    private sealed class ChatBatch;
}
