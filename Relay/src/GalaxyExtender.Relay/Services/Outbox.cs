using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Durable holding pen for webhook payloads that could not be delivered (Discord 429 or outage).
///
/// The outbox is drained OPPORTUNISTICALLY at the start of every authenticated request (chat POST,
/// heartbeat), and again on the <see cref="BackgroundTicker"/> so a parked line does not sit there
/// until somebody logs in. The request path stays load-bearing rather than deferring to the timer:
/// shared IIS hosting can idle-stop the process, and a drain that only happened on a tick would
/// stop happening on exactly the host that stops it. Entries survive recycles via the state store;
/// that durability is the only honest way not to lose lines here.
/// </summary>
public sealed class Outbox(
    IStateStore store,
    DiscordPublisher publisher,
    IOptionsMonitor<RelayOptions> options,
    ILogger<Outbox> logger)
{
    /// <summary>Most entries attempted per drain, so no single request pays for a long backlog.</summary>
    private const int MaxDrainPerRequest = 5;

    /// <summary>
    /// How long a claimed entry is invisible to other drains. Long enough to cover the webhook
    /// client's timeout plus its in-request 429 retry; short enough that a crash mid-POST only
    /// delays redelivery, not loses it.
    /// </summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(60);

    public int Depth => store.Read(state => state.Outbox.Count);

    /// <summary>
    /// Parks a payload for a later request to deliver.
    ///
    /// <paramref name="alertPingStampUtc"/> is set only for the payload carrying the alert role
    /// mention, and only when a ping window was actually claimed for it — see
    /// <see cref="ReleaseAlertPingClaim"/> for what it buys.
    /// </summary>
    public void Park(string payloadJson, int lineCount, TimeSpan delay,
        DateTimeOffset? alertPingStampUtc = null)
    {
        var current = options.CurrentValue;

        store.Mutate<object?>(state =>
        {
            while (state.Outbox.Count >= current.OutboxMaxEntries)
            {
                // Oldest-first drop keeps the newest chat, which is what readers still care about.
                logger.LogWarning("Outbox full ({Max}); dropping oldest entry", current.OutboxMaxEntries);
                ReleaseAlertPingClaim(state, state.Outbox[0]);
                state.Outbox.RemoveAt(0);
            }

            state.Outbox.Add(new OutboxEntry
            {
                Payload = payloadJson,
                LineCount = lineCount,
                Attempts = 0,
                NotBeforeUtc = DateTimeOffset.UtcNow + delay,
                AlertPingStampUtc = alertPingStampUtc
            });

            return null;
        });
    }

    /// <summary>
    /// Hands back the alert ping window claimed by an entry that is being DROPPED rather than
    /// delivered.
    ///
    /// The claim is made when the payload is built, which is right while the payload is still on
    /// its way: a mention parked by a 429 is delivered late, and a late ping beats no ping. It
    /// stops being right the moment the entry is discarded — the ping reached nobody, and leaving
    /// the window spent would silence the NEXT alert, which is the one that still has an audience.
    ///
    /// Released only if the window is still the one this entry claimed. A later alert that pinged
    /// successfully owns the window now, and its ping was heard.
    ///
    /// Must be called with the entry still in <paramref name="state"/> and from inside the store
    /// mutation that removes it, so the release and the drop are the same atomic step.
    /// </summary>
    private void ReleaseAlertPingClaim(RelayState state, OutboxEntry entry)
    {
        if (entry.AlertPingStampUtc is not { } stamp || state.LastAlertPingUtc != stamp)
        {
            return;
        }

        state.LastAlertPingUtc = null;

        logger.LogWarning(
            "Outbox entry {Id} carried the alert role ping and was never delivered; " +
            "releasing the ping window so the next alert can notify the role", entry.Id);
    }

    /// <summary>
    /// Attempts due entries oldest-first. Stops at the first failure — if Discord is refusing,
    /// hammering it with the rest of the backlog only makes the rate limit worse.
    ///
    /// <paramref name="cancellationToken"/> (the caller's request abort) only stops the drain from
    /// STARTING another delivery. An in-flight POST deliberately runs on no token: an abort
    /// mid-POST would both burn an attempt Discord never refused and — if Discord had already
    /// accepted — redeliver a duplicate later. The webhook client's timeout bounds the wait.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;

        for (var drained = 0; drained < MaxDrainPerRequest; drained++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // Cheap existence check first, so the every-request drain does not pay a state-file
            // write when the outbox is idle (the common case).
            if (!store.Read(state => state.Outbox.Any(e => e.NotBeforeUtc <= now)))
            {
                return;
            }

            // Claim under the store lock by pushing NotBeforeUtc past the lease: two requests
            // draining concurrently would otherwise both read the same entry and double-post it.
            // A crash mid-POST leaves the claim in place, so redelivery waits out the lease
            // instead of being lost.
            var entry = store.Mutate(state =>
            {
                var due = state.Outbox.FirstOrDefault(e => e.NotBeforeUtc <= now);

                if (due is not null)
                {
                    due.NotBeforeUtc = now + ClaimLease;
                }

                return due is null ? null : new { due.Id, due.Payload, due.LineCount };
            });

            if (entry is null)
            {
                // Another request claimed it between our check and our claim; leave the
                // backlog to that request.
                return;
            }

            var result = await publisher.PostAsync(entry.Payload, CancellationToken.None);

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
                    ReleaseAlertPingClaim(state, held);
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
