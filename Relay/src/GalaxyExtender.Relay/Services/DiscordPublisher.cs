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

    public sealed record PublishResult(bool Success, TimeSpan? RetryAfter)
    {
        public static readonly PublishResult Ok = new(true, null);
    }

    /// <summary>Builds the webhook payload for one embed description.</summary>
    public string BuildPayload(string description, string? contributingClientId)
    {
        var current = options.CurrentValue;

        var embed = new Embed
        {
            Description = description,
            Color = current.EmbedColor,
            Fields = current.ShowContributingClient && !string.IsNullOrEmpty(contributingClientId)
                ? [new EmbedField { Name = "client", Value = contributingClientId }]
                : null
        };

        return JsonSerializer.Serialize(new WebhookPayload { Embeds = [embed] });
    }

    /// <summary>Sends one payload. Never throws on HTTP-level failure — the caller decides
    /// whether a failed payload is parked or dropped.</summary>
    public async Task<PublishResult> PostAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var webhookUrl = options.CurrentValue.WebhookUrl;

        if (!options.CurrentValue.IsConfigured || webhookUrl is null)
        {
            // Callers check IsConfigured first; this is the fail-safe, not a code path.
            return new PublishResult(false, null);
        }

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
        [JsonPropertyName("embeds")]
        public required Embed[] Embeds { get; init; }

        // Even with the sanitizer's rewrite, this is the hard guarantee that nothing in
        // player-authored text can ping anyone.
        [JsonPropertyName("allowed_mentions")]
        public AllowedMentions Mentions { get; init; } = new();
    }

    private sealed class AllowedMentions
    {
        [JsonPropertyName("parse")]
        public string[] Parse { get; init; } = [];
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
