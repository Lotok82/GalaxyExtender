using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Rate limit for the world boss alert role ping: at most one ping per
/// <see cref="RelayOptions.AlertPingIntervalMinutes"/>, however many alerts publish in that window.
///
/// The throttle governs the PING, never the alert. An alert inside the quiet window still publishes,
/// coloured box and all — it simply arrives without notifying anyone. Suppressing the alert itself
/// would turn a noise control into data loss, and the second boss of a chain is exactly the one
/// somebody wants to read about.
///
/// The stamp is durable, and that is the point rather than a detail: this host idle-stops its app
/// pool, so an in-memory window would reset to "ping allowed" on every cold start — which is to say
/// on roughly every alert that arrives after a quiet spell. Those are the alerts the limit exists
/// for.
///
/// The window can also be handed BACK, by <see cref="Outbox"/>, when the payload carrying the
/// mention is dropped instead of delivered. A limit is only honest about pings that happened; one
/// that nobody received must not go on costing the next alert its own.
/// </summary>
public sealed class AlertPingThrottle(
    IOptionsMonitor<DiscordOptions> options,
    IOptionsMonitor<RelayOptions> relayOptions,
    IStateStore store,
    ILogger<AlertPingThrottle> logger)
{
    /// <summary>
    /// A won ping window: the role to mention, and the stamp that was written to claim it.
    ///
    /// The stamp is carried so the claim can be GIVEN BACK — see <see cref="Outbox"/>, which
    /// releases it if the payload holding the mention is ever dropped undelivered. Null when the
    /// throttle is disabled (<c>AlertPingIntervalMinutes</c> of 0): nothing was written, so there is
    /// nothing to release.
    /// </summary>
    public sealed record Claim(string RoleId, DateTimeOffset? StampUtc);

    /// <summary>
    /// The claim to ping on this alert, or null to publish it silently — either because no role is
    /// configured or because the window has not elapsed.
    ///
    /// Claiming and stamping happen together under the store lock, so two alerts admitted
    /// concurrently cannot both win the window. The stamp marks the DECISION to ping, not the
    /// delivery: a payload the webhook rejects is parked in the outbox and still carries its
    /// mention when it drains, so stamping on delivery instead would let one slow POST spend the
    /// window twice. A late ping beats no ping, which is why the mention is not stripped on the way
    /// to the outbox — but a payload the outbox eventually GIVES UP on delivered no ping at all,
    /// and returns the claim rather than leaving the next alert silent for a window nobody heard.
    /// </summary>
    public Claim? ClaimRoleMention()
    {
        var current = options.CurrentValue;
        var roleId = current.ResolvedAlertRoleId;

        if (roleId is null)
        {
            // Configured-but-unusable is worth a line in the log, because it is the failure an
            // operator cannot see: alerts keep publishing, nothing pings, and nothing says why.
            // The likely cause is the one Discord makes easy — copying the role as "<@&123…>"
            // rather than as its id. Alerts are rare by construction, so this cannot spam.
            if (!string.IsNullOrWhiteSpace(current.AlertRoleId))
            {
                logger.LogWarning(
                    "Discord:AlertRoleId is set but is not a bare role snowflake, so this alert " +
                    "published without a ping. It must be digits only — no \"<@&\" wrapper (Discord " +
                    "Developer Mode, right-click the role, Copy Role ID).");
            }

            return null;
        }

        // Negative config is a typo, not a request for a negative window; 0 disables the throttle
        // and every alert pings.
        var minutes = Math.Max(relayOptions.CurrentValue.AlertPingIntervalMinutes, 0);

        if (minutes == 0)
        {
            return new Claim(roleId, null);
        }

        var interval = TimeSpan.FromMinutes(minutes);
        var now = DateTimeOffset.UtcNow;

        // Cheap read first: alerts are rare, but a suppressed one must not cost a state-file write
        // just to say no.
        if (store.Read(state => state.LastAlertPingUtc) is { } seen && IsInsideWindow(seen, now, interval))
        {
            logger.LogInformation("Alert role ping suppressed: last ping was {Age:0} s ago",
                (now - seen).TotalSeconds);

            return null;
        }

        var claimed = store.Mutate(state =>
        {
            if (state.LastAlertPingUtc is { } last && IsInsideWindow(last, now, interval))
            {
                return false;
            }

            state.LastAlertPingUtc = now;
            return true;
        });

        return claimed ? new Claim(roleId, now) : null;
    }

    /// <summary>
    /// Whether <paramref name="stamp"/> still holds the window at <paramref name="now"/>.
    ///
    /// A stamp in the FUTURE counts as elapsed, deliberately. Plain subtraction would read a
    /// negative age as "well inside the window" and suppress every ping until real time caught up —
    /// silently, and for as long as the skew lasts. A clock correction or a state file carried over
    /// from another host is enough to produce one, and the recovery has to be automatic: the claim
    /// below then overwrites the bad stamp with a sane one.
    /// </summary>
    private static bool IsInsideWindow(DateTimeOffset stamp, DateTimeOffset now, TimeSpan interval) =>
        stamp <= now && now - stamp < interval;
}
