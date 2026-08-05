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

    /// <summary>
    /// True only for a URL that could plausibly work. A typo'd value failing here shows up as
    /// "not configured" on /health during setup, rather than as a failure at the first forward.
    /// </summary>
    public bool IsConfigured =>
        Uri.TryCreate(WebhookUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps;
}
