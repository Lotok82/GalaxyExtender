namespace GalaxyExtender.Relay.Options;

/// <summary>
/// Discord credentials and presentation. Bound from the "Discord" configuration section.
/// The webhook URL is a live credential: user-secrets locally, environment variable on the host.
/// </summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Bot token for the Stage 2 read path (R3). Raw token, no "Bot " prefix — the reader adds
    /// that itself. Live credential: git-ignored appsettings.Production.json only.
    /// </summary>
    public string? BotToken { get; set; }

    /// <summary>Bridge channel id, as a string — snowflakes overflow JSON readers that guess int.</summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Operator kill switch for Stage 2. The config can be staged (token + channel present)
    /// while this stays false; nothing is fetched and /messages keeps reporting "disabled".
    /// </summary>
    public bool Stage2Enabled { get; set; }

    /// <summary>
    /// Operator switch for the channel-history cleanup (R10): bridge-channel messages older than
    /// <see cref="RelayOptions.CleanupMaxAgeHours"/> are deleted, pinned ones preserved. Off by
    /// default DELIBERATELY — deleting history is destructive, so a redeploy alone must never
    /// start it; turning it on is an explicit config decision, same as <see cref="Stage2Enabled"/>.
    /// Needs the bot invited with Manage Messages.
    /// </summary>
    public bool CleanupEnabled { get; set; }

    /// <summary>
    /// Operator switch for the bot commands (R11): the bot answers <c>@GalaxyExtender status</c> in
    /// the bridge channel with who has the extension running. Off by default like the switches
    /// above — the relay starts posting messages of its own authorship when this goes on, and a
    /// redeploy alone should never change what appears in a guild's channel. Needs the bot invited
    /// with Send Messages and Read Message History.
    /// </summary>
    public bool CommandsEnabled { get; set; }

    /// <summary>
    /// The bot's own user id, used to recognise mentions of it. Normally left empty: the relay
    /// discovers it from <c>GET /users/@me</c> on the first scan and caches it in durable state.
    /// Set it only to override that — a WRONG value here means no mention is ever recognised, which
    /// looks exactly like the bot being deaf.
    /// </summary>
    public string? BotUserId { get; set; }

    /// <summary>
    /// The configured override, or null when it is absent OR blank — the single answer both the
    /// command scan and the Stage 2 reader resolve against.
    ///
    /// Blank has to collapse to null HERE rather than at each call site. The two paths make
    /// opposite decisions from the same value: the scanner decides whether to answer a mention,
    /// the reader decides whether to keep that mention out of the guild room. A configured-but-
    /// empty value read as "" by one and as "not configured" by the other puts half a
    /// conversation with a bot in front of players.
    /// </summary>
    public string? ConfiguredBotUserId =>
        string.IsNullOrWhiteSpace(BotUserId) ? null : BotUserId;

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

    /// <summary>
    /// The Stage 2 read path is live: enabled by the operator AND plausibly credentialed. The
    /// same shape of check as <see cref="IsConfigured"/> — a malformed channel id reads as "not
    /// configured" rather than failing at the first fetch.
    /// </summary>
    public bool IsStage2Configured =>
        Stage2Enabled &&
        !string.IsNullOrWhiteSpace(BotToken) &&
        !string.IsNullOrWhiteSpace(ChannelId) &&
        ChannelId.All(char.IsAsciiDigit);

    /// <summary>
    /// The cleanup sweep is live: enabled AND plausibly credentialed. Deliberately independent of
    /// <see cref="Stage2Enabled"/> — the channel wants tidying even while the read path is off.
    /// </summary>
    public bool IsCleanupConfigured =>
        CleanupEnabled &&
        !string.IsNullOrWhiteSpace(BotToken) &&
        !string.IsNullOrWhiteSpace(ChannelId) &&
        ChannelId.All(char.IsAsciiDigit);

    /// <summary>
    /// The bot answers commands: enabled AND plausibly credentialed. Independent of
    /// <see cref="Stage2Enabled"/> for a specific reason — "is the bridge working?" is asked most
    /// often when it is NOT, so the answer must not depend on the read path being on.
    /// </summary>
    public bool IsCommandsConfigured =>
        CommandsEnabled &&
        !string.IsNullOrWhiteSpace(BotToken) &&
        !string.IsNullOrWhiteSpace(ChannelId) &&
        ChannelId.All(char.IsAsciiDigit);
}
