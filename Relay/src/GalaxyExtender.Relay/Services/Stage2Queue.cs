using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// The Stage 2 work queue: per-message claims with redelivery (R6) and marker-ack matching (R7).
///
/// Claim semantics (pinned in README.md): a poll claims up to <see
/// cref="RelayOptions.Stage2MaxPerPoll"/> unclaimed-or-expired messages, oldest first. A claim
/// expires after <see cref="RelayOptions.Stage2RedeliveryTimeoutSeconds"/> unless the injected
/// line re-enters through /chat and acks it; after <see cref="RelayOptions.Stage2MaxDeliveries"/>
/// claims the message is dropped and counted. Drops are report-once via the state's counter.
///
/// Ack matching (decided 2026-08-05, stage2 plan R7): the relayed line's body from the
/// <c>[Discord] </c> marker onward is compared against each outstanding claim's composed body —
/// exact first, then mask-tolerant (same length, characters equal wherever the received character
/// is not '*'), because the ack passes through each RECEIVING client's profanity filter and can
/// arrive masked even though the relay composed clean text. Loose matching is deliberately NOT
/// used: a spoofed marked line acking an undelivered claim would turn a duplicate into a silent
/// loss, which is the wrong failure direction.
/// </summary>
public sealed class Stage2Queue(
    IStateStore store,
    IOptionsMonitor<RelayOptions> options,
    ILogger<Stage2Queue> logger)
{
    private const string Marker = "[Discord] ";

    /// <summary>Longest plausible "Name: " sender prefix, mirroring the extension's rewrite rule.</summary>
    private const int MaxSenderPrefixLength = 48;

    /// <summary>The body the claimant will inject — also what the ack must match.</summary>
    public static string ComposeInjectedBody(string author, string text) =>
        $"{Marker}{author}: {text}";

    /// <summary>
    /// Admission into the queue — the ONLY way entries should be added, so the rules live once:
    /// each entry gets the next claim-order sequence, and the cap (R6) then drops the oldest
    /// entries, counted for the report-once drop counter. Oldest-first eviction means a
    /// question/answer pair appended together is never split by any sane cap (both entries are
    /// the newest in the queue). Callers run this inside a store mutate.
    /// </summary>
    public static void Enqueue(RelayState state, int maxPending, IReadOnlyList<PendingEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.Sequence = ++state.Stage2Sequence;
            state.Stage2Pending.Add(entry);
        }

        // Queue cap (R6): oldest dropped and counted — newest chat is what still matters.
        while (state.Stage2Pending.Count > maxPending)
        {
            state.Stage2Pending.RemoveAt(0);
            state.Stage2Dropped++;
        }
    }

    /// <summary>
    /// Claims the next batch for <paramref name="claimant"/>. Runs the prune (TTL, delivery cap)
    /// first so nothing stale is ever handed out. The cheap read-only guard keeps idle polls —
    /// the overwhelmingly common case — free of state-file writes.
    /// </summary>
    public MessagesResponse Claim(string claimant)
    {
        var current = options.CurrentValue;
        var now = DateTimeOffset.UtcNow;

        if (!store.Read(state => state.Stage2Pending.Count > 0 || state.Stage2Dropped > 0))
        {
            return new MessagesResponse([], Dropped: 0);
        }

        return store.Mutate(state =>
        {
            var dropped = Prune(state, now, current);

            var timeout = TimeSpan.FromSeconds(current.Stage2RedeliveryTimeoutSeconds);
            var claimed = new List<PendingMessage>();

            // Discord-chronological by snowflake, with the admission sequence breaking ties —
            // BotCommandScanner relies on this pair of keys to keep an eight-ball answer directly
            // after its question even when the answer's id had to be fabricated (see
            // PendingEntry.Id); change the ordering only together with that.
            foreach (var entry in state.Stage2Pending
                         .Where(e => e.ClaimedUtc is null || now - e.ClaimedUtc >= timeout)
                         .OrderBy(e => ulong.TryParse(e.Id, out var id) ? id : ulong.MaxValue)
                         .ThenBy(e => e.Sequence)
                         .Take(current.Stage2MaxPerPoll))
            {
                if (entry.Deliveries > 0)
                {
                    logger.LogInformation(
                        "Stage 2 message {Id} redelivered (delivery {Delivery}) to {Claimant}",
                        entry.Id, entry.Deliveries + 1, claimant);
                }

                entry.ClaimedUtc = now;
                entry.ClaimedBy = claimant;
                entry.Deliveries++;

                claimed.Add(new PendingMessage(entry.Id, entry.Author, entry.Text, entry.TimestampUtc));
            }

            var report = state.Stage2Dropped + dropped;
            state.Stage2Dropped = 0;

            return new MessagesResponse(claimed, report);
        });
    }

    /// <summary>
    /// Recognises a relayed guild line as a bridged-message echo and, when it matches an
    /// outstanding claim, completes the delivery. Returns true when the line is MARKED —
    /// matched or not — because marked lines must never be forwarded to Discord either way.
    /// Safe to call for every copy the relaying clients send; after the first match the entry
    /// is gone and the rest are no-ops.
    /// </summary>
    public bool TryAckMarkedLine(string normalizedLine)
    {
        if (!TryExtractMarkedBody(normalizedLine, out var body))
        {
            return false;
        }

        // Cheap read first: every relaying client sends its own copy of the ack line, and all
        // copies after the first (plus spoofed or stale marked lines) must not cost a state-file
        // write. The read-then-mutate race is harmless — the mutate re-checks.
        var hasCandidate = store.Read(state => state.Stage2Pending.Any(entry =>
            BodiesMatch(ComposeInjectedBody(entry.Author, entry.Text), body)));

        if (!hasCandidate)
        {
            // Spoofed, stale (already acked via another client's copy), or a claim that
            // expired and was dropped. Dropping unmatched marked lines is the safe default.
            logger.LogDebug("Marked line did not match any outstanding Stage 2 claim");
            return true;
        }

        store.Mutate<object?>(state =>
        {
            var match = state.Stage2Pending.FirstOrDefault(entry =>
                BodiesMatch(ComposeInjectedBody(entry.Author, entry.Text), body));

            if (match is not null)
            {
                logger.LogInformation(
                    "Stage 2 message {Id} delivered (acked after {Deliveries} delivery/ies)",
                    match.Id, match.Deliveries);

                state.Stage2Pending.Remove(match);
            }

            return null;
        });

        return true;
    }

    /// <summary>
    /// Extracts the <c>[Discord] …</c> body from a normalised relayed line, tolerating an
    /// optional leading channel tag (<c>[GuildChat] </c>) and requiring a strict game-stamped
    /// sender prefix (<c>Name: </c> — one colon, nothing bracket-like, plausible length) directly
    /// before the marker. Mirrors the extension's display-rewrite rule, so a guild line that
    /// merely MENTIONS the marker mid-sentence is not treated as bridged and forwards normally.
    /// </summary>
    public static bool TryExtractMarkedBody(string normalizedLine, out string body)
    {
        body = string.Empty;

        var i = 0;

        while (i < normalizedLine.Length && normalizedLine[i] == ' ')
        {
            i++;
        }

        // Optional channel tag — but the marker is bracketed too. A line whose first bracket IS
        // the marker has no sender prefix to demand (belt for a server that stamps nothing).
        if (i < normalizedLine.Length && normalizedLine[i] == '[')
        {
            if (string.CompareOrdinal(normalizedLine, i, Marker, 0, Marker.Length) == 0)
            {
                body = normalizedLine[i..];
                return true;
            }

            var closing = normalizedLine.IndexOf(']', i);

            if (closing < 0 || closing - i > 24)
            {
                return false;
            }

            i = closing + 1;

            while (i < normalizedLine.Length && normalizedLine[i] == ' ')
            {
                i++;
            }
        }

        var marker = normalizedLine.IndexOf(Marker, i, StringComparison.Ordinal);

        if (marker < 0)
        {
            return false;
        }

        if (marker == i)
        {
            body = normalizedLine[marker..];
            return true;
        }

        // The stretch between tag and marker must be exactly the game-stamped "Name: ".
        var prefix = normalizedLine[i..marker].TrimEnd();

        if (prefix.Length < 2 || prefix.Length > MaxSenderPrefixLength || prefix[^1] != ':')
        {
            return false;
        }

        foreach (var c in prefix[..^1])
        {
            if (c is ':' or '[' or ']' or '\\')
            {
                return false;
            }
        }

        body = normalizedLine[marker..];
        return true;
    }

    /// <summary>R7: exact, then mask-tolerant (see class summary).</summary>
    public static bool BodiesMatch(string expected, string received)
    {
        if (string.Equals(expected, received, StringComparison.Ordinal))
        {
            return true;
        }

        if (expected.Length != received.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (received[i] != expected[i] && received[i] != '*')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// TTL expiry plus the delivery cap: an entry whose latest claim expired unacked after the
    /// maximum number of deliveries is dropped here rather than redelivered forever. Returns how
    /// many were dropped by this pass (the caller folds them into the report-once counter).
    /// </summary>
    private static int Prune(RelayState state, DateTimeOffset now, RelayOptions options)
    {
        var ttlCutoff = now.AddSeconds(-options.Stage2TtlSeconds);
        var timeout = TimeSpan.FromSeconds(options.Stage2RedeliveryTimeoutSeconds);

        return state.Stage2Pending.RemoveAll(entry =>
            entry.ReceivedUtc < ttlCutoff ||
            (entry.Deliveries >= options.Stage2MaxDeliveries &&
             entry.ClaimedUtc is { } claimed && now - claimed >= timeout));
    }
}
