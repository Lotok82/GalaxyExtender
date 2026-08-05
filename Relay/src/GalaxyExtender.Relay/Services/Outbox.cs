using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Durable holding pen for webhook payloads that could not be delivered (Discord 429 or outage).
///
/// There is no background worker on shared IIS hosting, so the outbox is drained
/// OPPORTUNISTICALLY at the start of every authenticated request (chat POST, heartbeat). Entries
/// survive recycles via the state store; that durability is the only honest way not to lose lines
/// on this host.
/// </summary>
public sealed class Outbox(
    IStateStore store,
    DiscordPublisher publisher,
    IOptionsMonitor<RelayOptions> options,
    ILogger<Outbox> logger)
{
    /// <summary>Most entries attempted per drain, so no single request pays for a long backlog.</summary>
    private const int MaxDrainPerRequest = 5;

    public int Depth => store.Read(state => state.Outbox.Count);

    /// <summary>Parks a payload for a later request to deliver.</summary>
    public void Park(string payloadJson, int lineCount, TimeSpan delay)
    {
        var current = options.CurrentValue;

        store.Mutate<object?>(state =>
        {
            while (state.Outbox.Count >= current.OutboxMaxEntries)
            {
                // Oldest-first drop keeps the newest chat, which is what readers still care about.
                logger.LogWarning("Outbox full ({Max}); dropping oldest entry", current.OutboxMaxEntries);
                state.Outbox.RemoveAt(0);
            }

            state.Outbox.Add(new OutboxEntry
            {
                Payload = payloadJson,
                LineCount = lineCount,
                Attempts = 0,
                NotBeforeUtc = DateTimeOffset.UtcNow + delay
            });

            return null;
        });
    }

    /// <summary>
    /// Attempts due entries oldest-first. Stops at the first failure — if Discord is refusing,
    /// hammering it with the rest of the backlog only makes the rate limit worse.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;

        for (var drained = 0; drained < MaxDrainPerRequest; drained++)
        {
            var now = DateTimeOffset.UtcNow;

            var entry = store.Read(state =>
                state.Outbox.FirstOrDefault(e => e.NotBeforeUtc <= now));

            if (entry is null)
            {
                return;
            }

            var result = await publisher.PostAsync(entry.Payload, cancellationToken);

            if (result.Success)
            {
                store.Mutate<object?>(state =>
                {
                    state.Outbox.RemoveAll(e => e.Id == entry.Id);
                    state.LastForwardUtc = DateTimeOffset.UtcNow;
                    return null;
                });

                logger.LogInformation("Outbox entry {Id} delivered ({Lines} line(s))",
                    entry.Id, entry.LineCount);
                continue;
            }

            store.Mutate<object?>(state =>
            {
                var held = state.Outbox.FirstOrDefault(e => e.Id == entry.Id);

                if (held is null)
                {
                    return null;
                }

                held.Attempts++;

                if (held.Attempts >= current.OutboxMaxAttempts)
                {
                    logger.LogError(
                        "Outbox entry {Id} dropped after {Attempts} attempts ({Lines} line(s) lost)",
                        held.Id, held.Attempts, held.LineCount);
                    state.Outbox.RemoveAll(e => e.Id == held.Id);
                    return null;
                }

                // Exponential backoff, capped; a 429's retry_after overrides it when longer.
                var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, held.Attempts) * 5, 300));

                if (result.RetryAfter is { } retryAfter && retryAfter > backoff)
                {
                    backoff = retryAfter;
                }

                held.NotBeforeUtc = DateTimeOffset.UtcNow + backoff;
                return null;
            });

            return;
        }
    }
}
