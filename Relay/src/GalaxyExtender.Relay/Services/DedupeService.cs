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
    /// <summary>Outcome of admitting a batch. Exactly one of the two shapes:</summary>
    /// <param name="ReplayedResponse">Set when this batchId was already processed — the caller
    /// returns it verbatim and must NOT post anything.</param>
    /// <param name="UniqueLines">Display-form lines seen for the first time, in arrival order.</param>
    /// <param name="Deduped">Lines recognised as duplicates of an earlier arrival.</param>
    public sealed record Admission(
        ChatBatchResponse? ReplayedResponse,
        IReadOnlyList<string> UniqueLines,
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
    /// </summary>
    public Admission Admit(
        string batchId,
        string? clientId,
        IReadOnlyList<(string Key, string DisplayText)> lines)
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

            var unique = new List<string>();
            var deduped = 0;
            var seenThisBatch = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (key, displayText) in lines)
            {
                // Within one batch the extension never sends the same (text, occurrence) twice,
                // but the contract does not forbid it — treat an in-batch repeat as a duplicate.
                if (!seenThisBatch.Add(key) ||
                    state.Dedupe.Any(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)))
                {
                    deduped++;
                    continue;
                }

                state.Dedupe.Add(new DedupeEntry { Key = key, FirstSeenUtc = now, FirstSeenBy = clientId });
                unique.Add(displayText);
            }

            return new Admission(null, unique, deduped);
        });
    }

    /// <summary>
    /// Records the response produced for <paramref name="batchId"/> so a client retry replays it
    /// instead of reprocessing, and stamps the last successful forward for /health.
    /// </summary>
    public void Complete(string batchId, ChatBatchResponse response, bool forwardedSomething)
    {
        var now = DateTimeOffset.UtcNow;

        store.Mutate<object?>(state =>
        {
            state.Batches.Add(new BatchEntry { Id = batchId, SeenUtc = now, Response = response });

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
