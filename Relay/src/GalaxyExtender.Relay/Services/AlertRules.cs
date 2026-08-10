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

        foreach (var (tag, color) in current.ResolvedAlertTags)
        {
            if (!string.IsNullOrEmpty(tag) &&
                normalizedText.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return new AlertRule(tag, color);
            }
        }

        return null;
    }
}
