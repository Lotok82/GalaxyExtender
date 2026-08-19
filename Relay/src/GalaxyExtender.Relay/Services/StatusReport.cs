using System.Text;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Composes what the bot says in Discord (R11). Pure string building, separate from the scan that
/// fetches and posts, so the wording is unit-testable without a fake Discord.
///
/// "Online" here means "an extension client checked in within the presence window", which is the
/// only thing the relay can actually observe — it never talks to the game server. The wording says
/// so rather than implying the relay knows who is logged into the galaxy.
///
/// **Counts only — never names.** The bridge's client labels are optional ini fields that most
/// players never fill in (the handed-out file ships them blank on purpose, so nobody has to edit
/// anything), which makes any name list either empty or misleading. Reporting a number also means
/// nothing self-reported by a client can reach a message the relay itself authored.
///
/// **The bot's own name appears nowhere in here either**, and must not: whoever runs the relay names
/// their Discord application whatever they like (and can rename it later), so any name baked into
/// this text would eventually contradict the name Discord shows beside the very same message. It
/// would also be redundant — a reply already renders under the bot's current display name. What the
/// wording describes instead is the SUBJECT: the guild-chat bridge.
/// </summary>
public static class StatusReport
{
    /// <summary>Discord's hard limit on a message's content.</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>
    /// What the status line calls the thing it is reporting on. Deliberately a description rather
    /// than a product or bot name — see the class summary.
    /// </summary>
    private const string Subject = "Guild chat bridge";

    public static string Status(
        PresenceSnapshot presence,
        int onlineWindowSeconds,
        bool forwardingConfigured,
        bool stage2Enabled,
        DateTimeOffset? lastAlertUtc = null)
    {
        var builder = new StringBuilder();
        var window = Describe(TimeSpan.FromSeconds(onlineWindowSeconds));

        if (presence.Online > 0)
        {
            builder.Append($"**{Subject}: online** — ")
                .Append(presence.Online == presence.Known
                    ? $"{Clients(presence.Online)} connected"
                    : $"{presence.Online} of {Clients(presence.Known)} connected")
                .Append(" (checked in within the last ")
                .Append(window)
                .Append(").");
        }
        else if (presence.Known == 0)
        {
            builder.Append($"**{Subject}: offline** — no client has ever checked in with this relay.");
        }
        else
        {
            builder.Append($"**{Subject}: offline** — nobody has checked in within the last ")
                .Append(window)
                .Append(". ")
                .Append(Clients(presence.Known))
                // "seen recently", not "known": the roster can briefly carry both the old and the
                // new id of one install across an extension rollout, so this must not read as a
                // hard count of who has it installed.
                .Append(" seen recently");

            if (presence.LastSeenUtc is { } lastSeen)
            {
                builder.Append("; last seen ")
                    .Append(Describe(DateTimeOffset.UtcNow - lastSeen))
                    .Append(" ago");
            }

            builder.Append('.');
        }

        // Answered only when there is a stamp to report: a relay that has never seen an alert (or
        // whose alert feed is switched off) says nothing rather than "never", which would read as
        // an accusation that the feed is broken.
        if (lastAlertUtc is { } lastAlert)
        {
            builder.Append("\nLast World Boss Alert: ")
                .Append(DescribeHoursAndMinutes(DateTimeOffset.UtcNow - lastAlert))
                .Append(" ago.");
        }

        // The two questions that follow "is it online?" whenever a switch is off, answered before
        // anyone has to ask them.
        if (!forwardingConfigured)
        {
            builder.Append("\nGame → Discord forwarding is not configured on the relay.");
        }

        if (!stage2Enabled)
        {
            builder.Append("\nDiscord → game delivery is switched off, so messages posted here are " +
                           "not injected into the guild room.");
        }

        return Clamp(builder.ToString());
    }

    /// <summary>
    /// Unprompted: somebody posted ordinary chat that is NOT going to reach the guild room as
    /// posted. Says the status, then — the part people actually want — what becomes of their
    /// message.
    ///
    /// The two answers differ in kind, so they are worded as such rather than hedged into one:
    ///
    /// * **Read path off** — it is never delivered. Nothing is queued anywhere; saying "later" would
    ///   be a lie.
    /// * **Nobody online** — it IS delivered, whenever somebody next logs in, however long that
    ///   takes. Not obvious from the code: the injection TTL runs from the moment the relay FETCHES
    ///   a message, and fetching only happens when a client polls, so an idle channel accumulates
    ///   nothing that can expire. The message simply waits in Discord.
    ///
    /// The one thing that can still lose it is the channel tidy-up (R10) deleting it from Discord
    /// before anyone comes online, so that deadline is stated whenever the sweep is switched on.
    /// Promising delivery and then quietly dropping it would be the worst of the available
    /// behaviours.
    /// </summary>
    public static string DeliveryNotice(
        PresenceSnapshot presence,
        int onlineWindowSeconds,
        bool stage2Enabled,
        bool cleanupEnabled,
        int cleanupMaxAgeHours)
    {
        if (!stage2Enabled)
        {
            return Clamp(
                $"**{Subject}: offline** — Discord → game delivery is switched off on the relay.\n" +
                "This message will not appear in the guild room, now or later.");
        }

        var builder = new StringBuilder();

        builder.Append($"**{Subject}: offline** — ");

        if (presence.Known == 0)
        {
            builder.Append("no client has ever checked in with this relay.");
        }
        else
        {
            builder.Append("nobody has checked in within the last ")
                .Append(Describe(TimeSpan.FromSeconds(onlineWindowSeconds)));

            if (presence.LastSeenUtc is { } lastSeen)
            {
                builder.Append(" (last seen ")
                    .Append(Describe(DateTimeOffset.UtcNow - lastSeen))
                    .Append(" ago)");
            }

            builder.Append('.');
        }

        builder.Append("\nThis message is waiting, not lost: the first client to come online posts " +
                       "it into the guild room.");

        if (cleanupEnabled)
        {
            builder.Append(" If nobody comes online within about ")
                .Append(Describe(TimeSpan.FromHours(cleanupMaxAgeHours)))
                .Append(", the channel tidy-up removes it undelivered.");
        }

        return Clamp(builder.ToString());
    }

    /// <summary>
    /// The one-liner for `help` and for a bare mention. "Mention me followed by `status`" rather
    /// than a worked example, because a worked example would have to spell out a bot name that only
    /// the operator knows and can change at any time.
    /// </summary>
    public static string Help() => Clamp(
        $"**{Subject}.** Mention me followed by `status` and I will say how many clients have the " +
        "extension running and whether the bridge is live. Guild chat posted in game appears in " +
        "this channel; anything typed here goes back into the guild room while the bridge is on.");

    private static string Clients(int count) => count == 1 ? "1 client" : $"{count} clients";

    /// <summary>
    /// The alert age in the agreed shape — "x hours and xx minutes ago" — so it always parses the
    /// same way at a glance, however long it has been. Hours grow without a day rollover (an alert
    /// two days back reads "51 hours and 03 minutes"): the guild reads this to judge whether a boss
    /// window has come round again, and that arithmetic is easier from hours than from days. A
    /// future stamp (clock skew, a state file moved between hosts) clamps to zero rather than
    /// rendering negative.
    /// </summary>
    private static string DescribeHoursAndMinutes(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return $"{(int)elapsed.TotalHours} hours and {elapsed.Minutes:00} minutes";
    }

    /// <summary>Coarse, human duration — "2 h 11 min", not "2:11:04.7".</summary>
    private static string Describe(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 90)
        {
            return $"{Math.Max(1, (int)Math.Round(elapsed.TotalSeconds))} s";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes} min";
        }

        if (elapsed.TotalHours < 24)
        {
            var minutes = elapsed.Minutes;
            return minutes == 0
                ? $"{(int)elapsed.TotalHours} h"
                : $"{(int)elapsed.TotalHours} h {minutes} min";
        }

        var days = (int)elapsed.TotalDays;
        return days == 1 ? "1 day" : $"{days} days";
    }

    private static string Clamp(string text) =>
        text.Length <= MaxMessageLength ? text : text[..(MaxMessageLength - 3)] + "...";
}
