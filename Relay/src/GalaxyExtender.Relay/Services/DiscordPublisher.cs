using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Posts embed payloads to the configured Discord webhook.
///
/// Every payload carries <c>allowed_mentions: {parse: []}</c> — combined with the sanitizer's
/// zero-width-joiner rewrite, player-authored guild chat can never mass-ping the Discord server.
///
/// 429 handling is shaped by the no-background-worker constraint: honour a SHORT
/// <c>retry_after</c> with one bounded in-request retry, and report anything longer to the caller,
/// who parks the payload in the durable outbox for the next request to drain.
/// </summary>
public sealed class DiscordPublisher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DiscordOptions> options,
    ILogger<DiscordPublisher> logger)
{
    public const string HttpClientName = "discord";

    /// <summary>Longest retry_after honoured inside the request; anything above goes to the outbox.</summary>
    private static readonly TimeSpan MaxInRequestRetry = TimeSpan.FromSeconds(2);

    /// <summary>Subtext prefix for the contributing-client label on a plain message.</summary>
    private const string ClientSuffixPrefix = "\n-# client: ";

    /// <summary>Clamp for the client label — the id is self-reported and unauthenticated.</summary>
    private const int MaxClientLabelLength = 64;

    /// <summary>
    /// Headroom the chunker must reserve below <see cref="TextSanitizer.MaxContentLength"/> for
    /// the subtext <see cref="BuildPayload"/> appends; zero while the flag is off. Without the
    /// reserve, a chunk built right up to 2000 goes over once the suffix lands, Discord rejects
    /// the POST with a 400 on every retry, and the outbox eventually drops the lines.
    /// </summary>
    public int PlainContentReserve =>
        options.CurrentValue.ShowContributingClient
            ? ClientSuffixPrefix.Length + MaxClientLabelLength
            : 0;

    public sealed record PublishResult(bool Success, TimeSpan? RetryAfter)
    {
        public static readonly PublishResult Ok = new(true, null);
    }

    /// <summary>
    /// Builds the webhook payload for guild chat: a PLAIN message, no embed.
    ///
    /// Chat used to be posted as a green embed. It reads better unboxed, and against plain chat a
    /// boxed alert becomes the thing that stands out (see the world boss alert plan). One
    /// consequence is worth keeping in mind when editing this: an embed can never ping anyone
    /// whatever it contains, whereas <c>content</c> can — so the <c>allowed_mentions</c> lockdown
    /// on <see cref="WebhookPayload"/> stopped being belt-and-braces and became the guarantee.
    /// </summary>
    public string BuildPayload(string content, string? contributingClientId)
    {
        var current = options.CurrentValue;

        // An embed carries this as a field; a plain message has nowhere structural to put it, so
        // it goes in the body as subtext. Escaped like any other untrusted text — the id is
        // self-reported by the client and is not authenticated.
        if (current.ShowContributingClient && !string.IsNullOrEmpty(contributingClientId))
        {
            var label = TextSanitizer.ForDiscord(
                TextSanitizer.Normalize(contributingClientId), MaxClientLabelLength,
                DiscordTarget.PlainMessage);

            content = $"{content}{ClientSuffixPrefix}{label}";
        }

        return JsonSerializer.Serialize(new WebhookPayload { Content = content });
    }

    /// <summary>
    /// Builds an embed payload. Not used by guild chat since it moved to plain messages — this is
    /// the shape the world boss alert feed needs (its whole point is a coloured box against
    /// unboxed chat), and it keeps reverting the chat change a one-line swap at the call site.
    ///
    /// <paramref name="mentionRoleId"/> pings a role for the alert. Two things about it are forced
    /// by Discord rather than chosen: the mention goes in <c>content</c>, ABOVE the box, because
    /// mentions written inside an embed are rendered as text and never ping; and the role has to be
    /// named in <c>allowed_mentions.roles</c>, because <c>parse: []</c> otherwise suppresses it.
    /// That whitelist is exactly one id the relay itself supplies — <c>parse</c> stays empty, so
    /// nothing in player-authored text gains the ability to ping anything.
    /// </summary>
    public string BuildEmbedPayload(
        string description, int color, string? contributingClientId, string? mentionRoleId = null)
    {
        var current = options.CurrentValue;

        var embed = new Embed
        {
            Description = description,
            Color = color,
            Fields = current.ShowContributingClient && !string.IsNullOrEmpty(contributingClientId)
                ? [new EmbedField { Name = "client", Value = contributingClientId }]
                : null
        };

        // Trust the caller's id no further than the option that produced it, and by the SAME rule:
        // a non-snowflake here would post "<@&whatever>" as literal text on every alert, and an
        // out-of-range one would have Discord reject the payload outright.
        var roleId = DiscordOptions.IsSnowflake(mentionRoleId) ? mentionRoleId : null;

        return JsonSerializer.Serialize(new WebhookPayload
        {
            Content = roleId is null ? null : $"<@&{roleId}>",
            Embeds = [embed],
            Mentions = roleId is null ? new AllowedMentions() : new AllowedMentions { Roles = [roleId] }
        });
    }

    /// <summary>Sends one payload. NEVER throws — not on HTTP failure, not on cancellation —
    /// because the caller's park/complete bookkeeping must always run; an escaped exception here
    /// is what turns "Discord was slow" into silently lost lines (the batch is already admitted
    /// to the dedupe window). The caller decides whether a failed payload is parked or dropped.</summary>
    public async Task<PublishResult> PostAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var webhookUrl = options.CurrentValue.WebhookUrl;

        if (!options.CurrentValue.IsConfigured || webhookUrl is null)
        {
            // Callers check IsConfigured first; this is the fail-safe, not a code path.
            return new PublishResult(false, null);
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage response;

                try
                {
                    var client = httpClientFactory.CreateClient(HttpClientName);
                    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    response = await client.PostAsync(webhookUrl, content, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning("Webhook POST failed: {Error}", ex.Message);
                    return new PublishResult(false, null);
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return PublishResult.Ok;
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        var retryAfter = await ReadRetryAfterAsync(response, cancellationToken);

                        if (attempt == 0 && retryAfter <= MaxInRequestRetry)
                        {
                            await Task.Delay(retryAfter, cancellationToken);
                            continue;
                        }

                        logger.LogWarning("Webhook rate limited; retry_after={RetryAfter}s — parking payload",
                            retryAfter.TotalSeconds);
                        return new PublishResult(false, retryAfter);
                    }

                    logger.LogWarning("Webhook POST rejected with HTTP {Status}", (int)response.StatusCode);
                    return new PublishResult(false, null);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Reachable through the 429 handling: reading retry_after or the in-request
            // Task.Delay can be cancelled by the caller aborting mid-wait.
            logger.LogWarning("Webhook POST abandoned during retry handling: {Error}", ex.Message);
            return new PublishResult(false, null);
        }
    }

    private static async Task<TimeSpan> ReadRetryAfterAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Discord sends both a Retry-After header (seconds) and a JSON body {"retry_after": 1.3}.
        // Prefer the body — it has sub-second precision — and fall back to a conservative minute.
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("retry_after", out var value) &&
                value.TryGetDouble(out var seconds) && seconds >= 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
        }

        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        return TimeSpan.FromSeconds(60);
    }

    private sealed class WebhookPayload
    {
        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; init; }

        [JsonPropertyName("embeds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Embed[]? Embeds { get; init; }

        // Even with the sanitizer's rewrite, this is the hard guarantee that nothing in
        // player-authored text can ping anyone.
        [JsonPropertyName("allowed_mentions")]
        public AllowedMentions Mentions { get; init; } = new();
    }

    private sealed class AllowedMentions
    {
        [JsonPropertyName("parse")]
        public string[] Parse { get; init; } = [];

        /// <summary>
        /// Explicit ping whitelist for the alert feed. Discord allows this alongside an empty
        /// <see cref="Parse"/> (it rejects only the combination of "roles" in parse AND a roles
        /// list), which is precisely the shape wanted: no mention anywhere in the message text can
        /// ping, except the one id the relay put there itself.
        /// </summary>
        [JsonPropertyName("roles")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Roles { get; init; }
    }

    private sealed class Embed
    {
        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("color")]
        public int Color { get; init; }

        [JsonPropertyName("fields")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmbedField[]? Fields { get; init; }
    }

    private sealed class EmbedField
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("value")]
        public required string Value { get; init; }
    }
}
