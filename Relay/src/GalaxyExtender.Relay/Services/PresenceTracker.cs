using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// What "@bot status" answers with. <paramref name="Known"/> is every client seen inside the
/// retention window — the closest the relay can get to "how many people have the extension
/// installed" — and <paramref name="Online"/> is the subset that checked in recently enough to
/// count as connected. <paramref name="LastSeenUtc"/> is what makes an "offline" answer useful:
/// how long it has been since anyone was here.
///
/// Counts, deliberately — no identities. See <see cref="StatusReport"/>.
/// </summary>
public sealed record PresenceSnapshot(int Online, int Known, DateTimeOffset? LastSeenUtc);

/// <summary>
/// Who is running the extension right now (R11).
///
/// The relay has no gateway and no background worker, so presence is what clients TELL it: the
/// extension pings <c>POST /api/v1/presence</c> on a fixed cadence while its bridge is active, and
/// /chat and /messages refresh the same stamp opportunistically so a client on an older build (one
/// that never pings) still registers as soon as it does anything.
///
/// Writes are throttled per client by <see cref="RelayOptions.PresenceWriteIntervalSeconds"/>. That
/// matters more than it looks: a Stage 2 poll arrives every 5 s per client and the state document is
/// rewritten in full on every mutation, so touching presence unthrottled would turn idle polling
/// into a continuous stream of file writes.
///
/// The only thing recorded per client is its id and when it was last seen — no character or galaxy
/// labels, because the status command reports counts rather than names and there is no reason to keep
/// what nothing reads.
///
/// **The id is what makes the count a count**, so it must be something no player can duplicate. The
/// extension does that end of it: the value is a hash of the machine's Windows installation id, with
/// any ini-configured label as a mere prefix, so a pre-filled ini handed to the whole guild still
/// reports one entry per install. Nothing here can verify that — an id is only ever as trustworthy
/// as the client sending it — but nothing here needs to: the count is diagnostics, not accounting.
/// </summary>
public sealed class PresenceTracker(
    IStateStore store,
    IOptionsMonitor<RelayOptions> options,
    ILogger<PresenceTracker> logger)
{
    /// <summary>
    /// Records that <paramref name="clientId"/> is alive. Never throws: presence is diagnostics, and
    /// losing a stamp must not fail a chat batch that Discord already accepted.
    /// </summary>
    public void Touch(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var current = options.CurrentValue;
        var now = DateTimeOffset.UtcNow;
        var writeInterval = TimeSpan.FromSeconds(current.PresenceWriteIntervalSeconds);

        try
        {
            // Cheap read first, so the steady poll/batch stream does not pay a state-file write
            // per request — the same guard Stage2Queue.Claim and ChannelCleaner use.
            var due = store.Read(state =>
            {
                var entry = Find(state, clientId);

                return entry is null || now - entry.LastSeenUtc >= writeInterval;
            });

            if (!due)
            {
                return;
            }

            store.Mutate<object?>(state =>
            {
                var entry = Find(state, clientId);

                if (entry is null)
                {
                    entry = new PresenceEntry
                    {
                        ClientId = clientId,
                        // Inherited where there is one, so "known since" survives an upgrade.
                        FirstSeenUtc = Supersede(state, clientId)?.FirstSeenUtc ?? now
                    };

                    state.Presence.Add(entry);
                }

                entry.LastSeenUtc = now;

                Prune(state, now, current);

                return null;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable App_Data is already reported by /health; it must not turn a chat
            // batch that Discord accepted into a 500.
            logger.LogWarning("Presence for {ClientId} could not be persisted: {Error}",
                clientId, ex.Message);
        }
    }

    /// <summary>
    /// Counts and names for the status reply. Read-only: nothing here prunes or writes, so asking
    /// for status costs no state-file write.
    /// </summary>
    public PresenceSnapshot Snapshot()
    {
        var current = options.CurrentValue;
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-current.PresenceOnlineWindowSeconds);
        var retention = DateTimeOffset.UtcNow.AddDays(-current.PresenceRetentionDays);

        return store.Read(state =>
        {
            var known = state.Presence.Where(entry => entry.LastSeenUtc >= retention).ToList();

            return new PresenceSnapshot(
                known.Count(entry => entry.LastSeenUtc >= cutoff),
                known.Count,
                known.Count == 0 ? null : known.Max(entry => entry.LastSeenUtc));
        });
    }

    /// <summary>
    /// Retires the entry <paramref name="clientId"/> replaces, if there is one, and returns it.
    ///
    /// The extension's client id gained a machine-fingerprint suffix, and extension rollouts are
    /// manual and staggered: the relay ships first, every install registers under its OLD id, and
    /// each machine then switches to a NEW one whenever its owner gets round to taking the DLL.
    /// Both entries would otherwise sit in the roster for the whole retention window, inflating
    /// "known" by one per upgraded install — and "known" is the denominator in "3 of 8 clients
    /// connected", so it is not a cosmetic count.
    ///
    /// A superseded id is always a "-"-delimited prefix of the id that replaced it
    /// ("james" -> "james-a1b2c3d4e5f60718"), which is the one link available without the client's
    /// help. It is a no-op until the DLL actually ships, so the relay is correct on both sides of a
    /// rollout it cannot schedule.
    ///
    /// What it cannot catch: an install with no configured label goes from hashing the hostname to
    /// hashing MachineGuid, and "client-&lt;a&gt;" has no visible relationship to "client-&lt;b&gt;".
    /// Those age out instead — see <see cref="RelayOptions.PresenceRetentionDays"/>.
    ///
    /// Where two players share one ini label and only one has upgraded, this collapses them to a
    /// single entry until the other follows. That is exactly what the old id scheme did to them
    /// anyway, and it corrects itself on the next upgrade.
    /// </summary>
    private static PresenceEntry? Supersede(RelayState state, string clientId)
    {
        var previous = state.Presence.FirstOrDefault(entry =>
            clientId.StartsWith(entry.ClientId + "-", StringComparison.OrdinalIgnoreCase));

        if (previous is not null)
        {
            state.Presence.Remove(previous);
        }

        return previous;
    }

    private static PresenceEntry? Find(RelayState state, string clientId) =>
        state.Presence.FirstOrDefault(entry =>
            string.Equals(entry.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Keeps the roster bounded: entries untouched for the retention window drop out (a client that
    /// stopped playing months ago is not "installed" in any useful sense), and a hard cap on the
    /// list stops a client id that varies per launch from growing the state document without limit.
    /// </summary>
    private static void Prune(RelayState state, DateTimeOffset now, RelayOptions options)
    {
        var retentionCutoff = now.AddDays(-options.PresenceRetentionDays);

        state.Presence.RemoveAll(entry => entry.LastSeenUtc < retentionCutoff);

        if (state.Presence.Count <= options.PresenceMaxClients)
        {
            return;
        }

        foreach (var stale in state.Presence
                     .OrderBy(entry => entry.LastSeenUtc)
                     .Take(state.Presence.Count - options.PresenceMaxClients)
                     .ToList())
        {
            state.Presence.Remove(stale);
        }
    }
}
