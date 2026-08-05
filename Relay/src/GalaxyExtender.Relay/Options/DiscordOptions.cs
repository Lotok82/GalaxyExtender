namespace GalaxyExtender.Relay.Options;

/// <summary>
/// Discord credentials and presentation. Bound from the "Discord" configuration section.
/// The webhook URL is a live credential: user-secrets locally, environment variable on the host.
/// </summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public string? WebhookUrl { get; set; }

    /// <summary>Embed colour for game -> Discord lines. 0x2ECC71 green, per the bridge plan.</summary>
    public int EmbedColor { get; set; } = 3066993;

    /// <summary>
    /// Add the contributing client id to the embed as a debug field. Off by default: in relay
    /// mode the relay is the author of record, and the client that happened to win the dedupe
    /// race is not meaningful to readers.
    /// </summary>
    public bool ShowContributingClient { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);
}
