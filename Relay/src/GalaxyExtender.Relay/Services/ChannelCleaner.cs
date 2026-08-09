using System.Text;
using System.Text.Json;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Channel-history cleanup (R10): deletes bridge-channel messages older than
/// <see cref="RelayOptions.CleanupMaxAgeHours"/>, preserving pinned ones. The bridge channel is a
/// live ticker, not an archive — anything hours old was long since delivered (the Stage 2 TTL is
/// minutes) or read.
///
/// The sweep piggybacks on authenticated request traffic (chat POST, heartbeat, Stage 2 poll) and
/// on the <see cref="BackgroundTicker"/>, throttled by a durable
/// <see cref="RelayState.LastCleanupUtc"/> stamp — at most one sweep per
/// <see cref="RelayOptions.CleanupIntervalMinutes"/>, claimed atomically so a request and a tick
/// cannot both pay for one. The timer is what keeps the channel tidy through a night with nobody
/// online; the request path remains as the answer for a host that idle-stops the timer away.
///
/// One sweep is deliberately bounded: one page fetch (≤100 messages, newest-old-enough first) and
/// one bulk-delete. A backlog beyond that self-heals across sweeps, because deleting a page makes
/// the next <c>?before=</c> read return the page behind it. Messages older than 14 days —
/// possible only on a first run against an old channel — are outside bulk-delete's contract and
/// fall back to per-message DELETEs, capped per sweep like the outbox drain.
/// </summary>
public sealed class ChannelCleaner(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DiscordOptions> options,
    IOptionsMonitor<RelayOptions> relayOptions,
    IStateStore store,
    ILogger<ChannelCleaner> logger)
{
    /// <summary>2015-01-01T00:00:00Z in unix ms — the epoch Discord snowflakes count from.</summary>
    private const long DiscordEpochUnixMs = 1420070400000;

    /// <summary>One page per sweep; Discord's own maximum per read.</summary>
    private const int FetchLimit = 100;

    /// <summary>
    /// Bulk-delete rejects ids older than 14 days — and rejects the WHOLE request, not just the
    /// stale ids. Half a day of margin keeps a message that ages past the line between our fetch
    /// and the delete from failing the batch.
    /// </summary>
    private static readonly TimeSpan BulkDeleteMaxAge = TimeSpan.FromDays(13.5);

    /// <summary>
    /// Runs a sweep if the interval has elapsed. Never throws — cleanup must not be the reason a
    /// chat batch or poll fails. <paramref name="cancellationToken"/> (the caller's request abort)
    /// only stops the sweep from starting further deletes; in-flight Discord calls run to
    /// completion, matching the outbox's reasoning.
    /// </summary>
    public async Task SweepIfDueAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.IsCleanupConfigured)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(relayOptions.CurrentValue.CleanupIntervalMinutes);
        var now = DateTimeOffset.UtcNow;

        // Cheap read first, so the every-request check does not pay a state-file write between
        // sweeps (the overwhelmingly common case).
        if (store.Read(state => state.LastCleanupUtc) is { } last && now - last < interval)
        {
            return;
        }

        // Claim under the store lock. The stamp marks the ATTEMPT, success or not — a failing
        // Discord gets one sweep per interval, not a retry storm on every request.
        var claimed = store.Mutate(state =>
        {
            if (state.LastCleanupUtc is { } current && now - current < interval)
            {
                return false;
            }

            state.LastCleanupUtc = now;
            return true;
        });

        if (!claimed)
        {
            return;
        }

        try
        {
            await SweepAsync(now, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Channel cleanup sweep failed: {Error}", ex.Message);
        }
    }

    private async Task SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var relay = relayOptions.CurrentValue;
        var channelId = current.ChannelId!;

        var cutoff = ToSnowflake(now - TimeSpan.FromHours(relay.CleanupMaxAgeHours));
        var client = httpClientFactory.CreateClient(DiscordReader.HttpClientName);

        // ?before= filters server-side: everything returned is already older than the cutoff.
        using var fetch = NewRequest(HttpMethod.Get,
            $"channels/{channelId}/messages?before={cutoff}&limit={FetchLimit}");
        using var fetchResponse = await client.SendAsync(fetch, CancellationToken.None);

        if (!fetchResponse.IsSuccessStatusCode)
        {
            // 403 = Manage Messages / Read Message History missing; 429 = rate limited. Either
            // way the next interval retries; a permission problem logs every sweep until fixed.
            logger.LogWarning("Channel cleanup read failed with HTTP {Status}",
                (int)fetchResponse.StatusCode);
            return;
        }

        var candidates = ParseCandidates(
            await fetchResponse.Content.ReadAsStringAsync(CancellationToken.None), cutoff);

        if (candidates.Count == 0)
        {
            return;
        }

        // Bulk-delete covers everything younger than 14 days; anything older (first run on an
        // old channel) goes one DELETE at a time, capped. Bulk needs at least 2 ids — a lone
        // bulkable id just joins the singles.
        var bulkFloor = ToSnowflake(now - BulkDeleteMaxAge);
        var bulk = candidates.Where(id => ulong.Parse(id) > bulkFloor).ToList();
        var singles = candidates.Where(id => ulong.Parse(id) <= bulkFloor).ToList();

        if (bulk.Count == 1)
        {
            singles.Insert(0, bulk[0]);
            bulk.Clear();
        }

        var deleted = 0;

        if (bulk.Count > 0)
        {
            using var request = NewRequest(HttpMethod.Post,
                $"channels/{channelId}/messages/bulk-delete");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { messages = bulk }), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Channel cleanup bulk-delete failed with HTTP {Status}",
                    (int)response.StatusCode);
                return;
            }

            deleted += bulk.Count;
        }

        foreach (var id in singles.Take(relay.CleanupMaxSingleDeletesPerSweep))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using var request = NewRequest(HttpMethod.Delete,
                $"channels/{channelId}/messages/{id}");
            using var response = await client.SendAsync(request, CancellationToken.None);

            // 404 = already gone (deleted by hand between fetch and now) — the outcome we wanted.
            if (!response.IsSuccessStatusCode &&
                response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Channel cleanup delete of {Id} failed with HTTP {Status}",
                    id, (int)response.StatusCode);
                break;
            }

            deleted++;
        }

        if (deleted > 0)
        {
            logger.LogInformation("Channel cleanup deleted {Deleted} message(s) older than {Hours} h",
                deleted, relay.CleanupMaxAgeHours);
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bot {options.CurrentValue.BotToken}");
        return request;
    }

    /// <summary>Deletable ids from a channel-messages page: unpinned and genuinely old enough.</summary>
    private static List<string> ParseCandidates(string json, ulong cutoff)
    {
        var ids = new List<string>();

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected a message array");
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String ||
                !ulong.TryParse(idProperty.GetString(), out var numericId))
            {
                continue;
            }

            if (element.TryGetProperty("pinned", out var pinned) &&
                pinned.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            // ?before= already guarantees this; the guard is against ever deleting a fresh
            // message on a malformed or misrouted response.
            if (numericId >= cutoff)
            {
                continue;
            }

            ids.Add(idProperty.GetString()!);
        }

        return ids;
    }

    /// <summary>The snowflake a message created at <paramref name="utc"/> would carry.</summary>
    private static ulong ToSnowflake(DateTimeOffset utc) =>
        (ulong)(utc.ToUnixTimeMilliseconds() - DiscordEpochUnixMs) << 22;
}
