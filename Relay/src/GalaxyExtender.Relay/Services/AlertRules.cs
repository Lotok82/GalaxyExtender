using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>How one matched alert should be rendered: which tag matched, and the embed colour.</summary>
public sealed record AlertRule(string Tag, int Color);

/// <summary>
/// Decides whether a chat line is a world boss alert rather than ordinary guild chat.
///
/// The rule is deliberately the same literal the extension gates on: the line must BEGIN with a
/// configured tag. Starting position is doing double duty as an anti-spoof check — a server
/// broadcast arrives with no sender prefix, whereas anything a player types reaches us as
/// "Kaelen: [PvP World Boss] ..." and therefore never matches. Relaxing this to "contains" would
/// hand every player the ability to publish a red alert. See
/// Documentation/world-boss-alert-plan.md.
/// </summary>
public sealed class AlertRules(IOptionsMonitor<DiscordOptions> options)
{
    /// <summary>
    /// Rules resolved from one options snapshot, LONGEST tag first. Cached per snapshot because
    /// <see cref="Match"/> runs once per line and <see cref="DiscordOptions.ResolvedAlertTags"/>
    /// builds a fresh dictionary on every read; the cache is rebuilt only when the monitor hands
    /// out a new options instance (config reload). The benign race — two requests building the
    /// snapshot at once — costs a duplicate allocation, never a wrong answer.
    ///
    /// Longest-first is a correctness rule, not a tidy-up: with overlapping tags ("[Boss]",
    /// "[Boss Elite]") dictionary enumeration order would pick the colour nondeterministically.
    /// The most specific tag must own the line.
    /// </summary>
    private sealed record Snapshot(DiscordOptions Source, AlertRule[] Rules);

    private volatile Snapshot? _snapshot;

    /// <summary>
    /// The rule for a line, or null when it is ordinary chat. Match on the NORMALISED text, never
    /// the display form — display escaping can put a backslash in front of the tag's opening
    /// bracket, and the tag would stop matching itself.
    ///
    /// A tag the relay does not know (one added to a client's ini but not to this config) returns
    /// null and publishes as ordinary chat. That is the intended degradation: unstyled, never lost.
    /// </summary>
    public AlertRule? Match(string normalizedText)
    {
        var current = options.CurrentValue;

        if (!current.IsAlertsConfigured || string.IsNullOrEmpty(normalizedText))
        {
            return null;
        }

        var snapshot = _snapshot;

        if (snapshot is null || !ReferenceEquals(snapshot.Source, current))
        {
            snapshot = new Snapshot(current, [.. current.ResolvedAlertTags
                .Where(pair => !string.IsNullOrEmpty(pair.Key))
                .OrderByDescending(pair => pair.Key.Length)
                .Select(pair => new AlertRule(pair.Key, pair.Value))]);

            _snapshot = snapshot;
        }

        foreach (var rule in snapshot.Rules)
        {
            if (normalizedText.StartsWith(rule.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }
}
