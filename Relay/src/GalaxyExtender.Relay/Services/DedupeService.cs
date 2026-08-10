using System.Security.Cryptography;
using System.Text;
using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Cross-client de-duplication plus batch idempotency, both backed by the durable state store.
///
/// The dedupe key is <c>sha256(normalised text)[..16] + ":" + occurrence</c>. Every client counts
/// the same guild stream independently, so the same message from N clients arrives with the SAME
/// occurrence and collapses to one entry, while a genuine repeat arrives with occurrence+1 and gets
/// its own key. That is the whole trick — a plain time-window would eat real repeats.
/// </summary>
public sealed class DedupeService(IStateStore store, IOptionsMonitor<RelayOptions> options)
{
    /// <summary>
    /// One line ready to publish. <paramref name="Alert"/> is null for ordinary chat and set for a
    /// world boss alert; it rides through admission because the render decision is made BEFORE
    /// dedupe (it selects the escaping) and is needed again AFTER it (it selects the payload shape),
    /// and the display text alone cannot be classified once escaped.
    /// </summary>
    public sealed record PreparedLine(string Key, string DisplayText, AlertRule? Alert);

    /// <summary>Outcome of admitting a batch. Exactly one of the two shapes:</summary>
    /// <param name="ReplayedResponse">Set when this batchId was already processed — the caller
    /// returns it verbatim and must NOT post anything.</param>
    /// <param name="UniqueLines">Lines seen for the first time, in arrival order.</param>
    /// <param name="Deduped">Lines recognised as duplicates of an earlier arrival.</param>
    public sealed record Admission(
        ChatBatchResponse? ReplayedResponse,
        IReadOnlyList<PreparedLine> UniqueLines,
        int Deduped);

    public static string Key(string normalizedText, int occurrence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText));
        return $"{Convert.ToHexString(hash.AsSpan(0, 8))}:{occurrence}";
    }

    /// <summary>
    /// One atomic pass: prune both windows, replay a known batchId, otherwise classify every line
    /// and record first-arrivals. Runs entirely inside a single state mutation so a concurrent
    /// batch from another client cannot interleave between check and record.
    ///
    /// Deliberately NOT persisted here (<c>persist: false</c>): if the admitted keys became durable
    /// before the batch is delivered or parked, a crash in between would make the client's retry a
    /// dedupe no-op — accepted=0, nothing forwarded, lines silently gone. The keys become durable
    /// with the next persisting mutation: a Park, or <see cref="Complete"/>. Until then a recycle
    /// forgets the admission, and the retry re-does the work — re-processing is the recoverable
    /// failure; a durable admission with no delivery is not.
    /// </summary>
    public Admission Admit(
        string batchId,
        string? clientId,
        IReadOnlyList<PreparedLine> lines)
    {
        var current = options.CurrentValue;
        var now = DateTimeOffset.UtcNow;

        return store.Mutate(state =>
        {
            Prune(state, now, current);

            var known = state.Batches.FirstOrDefault(b =>
                string.Equals(b.Id, batchId, StringComparison.OrdinalIgnoreCase));

            if (known?.Response is not null)
            {
                return new Admission(known.Response, [], 0);
            }

            var unique = new List<PreparedLine>();
            var deduped = 0;
            var seenThisBatch = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                // Within one batch the extension never sends the same (text, occurrence) twice,
                // but the contract does not forbid it — treat an in-batch repeat as a duplicate.
                if (!seenThisBatch.Add(line.Key) ||
                    state.Dedupe.Any(entry => string.Equals(entry.Key, line.Key, StringComparison.Ordinal)))
                {
                    deduped++;
                    continue;
                }

                state.Dedupe.Add(new DedupeEntry { Key = line.Key, FirstSeenUtc = now, FirstSeenBy = clientId });
                unique.Add(line);
            }

            return new Admission(null, unique, deduped);
        }, persist: false);
    }

    /// <summary>
    /// Records the response produced for <paramref name="batchId"/> so a client retry replays it
    /// instead of reprocessing, and stamps the last successful forward for /health. This is also
    /// the persisting mutation that makes the batch's <see cref="Admit"/> keys durable.
    /// </summary>
    public void Complete(string batchId, ChatBatchResponse response, bool forwardedSomething)
    {
        var now = DateTimeOffset.UtcNow;

        store.Mutate<object?>(state =>
        {
            // A retry racing its still-running original can Complete the same batchId twice: the
            // retry saw every line as a dedupe hit (accepted=0) while the original did the real
            // work. Keep whichever record accounts for more lines so later retries replay the
            // truthful response rather than the loser of the race.
            var existing = state.Batches.FirstOrDefault(b =>
                string.Equals(b.Id, batchId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                state.Batches.Add(new BatchEntry { Id = batchId, SeenUtc = now, Response = response });
            }
            else if (existing.Response is null ||
                     response.Accepted + response.Queued >
                     existing.Response.Accepted + existing.Response.Queued)
            {
                existing.Response = response;
            }

            if (forwardedSomething)
            {
                state.LastForwardUtc = now;
            }

            return null;
        });
    }

    private static void Prune(RelayState state, DateTimeOffset now, RelayOptions options)
    {
        var dedupeCutoff = now.AddSeconds(-options.DedupeWindowSeconds);
        var batchCutoff = now.AddSeconds(-options.BatchIdWindowSeconds);

        state.Dedupe.RemoveAll(entry => entry.FirstSeenUtc < dedupeCutoff);
        state.Batches.RemoveAll(entry => entry.SeenUtc < batchCutoff);
    }
}
