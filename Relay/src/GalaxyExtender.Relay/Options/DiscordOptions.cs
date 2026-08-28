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
    /// Show each speaker's SERVER nickname on injected lines, rather than the account-level display
    /// name Discord puts in the message payload. On by default, unlike every other switch here, and
    /// for the opposite reason: those turn on the relay AUTHORING something (posts, deletions), so a
    /// redeploy must never start them, while this only changes which of a person's own names is
    /// shown to the guild — and the guild recognises the nickname, which is why people set one.
    ///
    /// Costs one <c>GET /guilds/{guild}/members/{user}</c> per speaker per
    /// <see cref="RelayOptions.NicknameRefreshHours"/> — a day, and only for people who actually
    /// spoke; see <see cref="Services.GuildNicknames"/> for how that is bounded. Turning it off is the kill switch if those calls ever become a problem;
    /// nothing else changes, and names fall back to <c>global_name</c>.
    /// </summary>
    public bool NicknamesEnabled { get; set; } = true;

    /// <summary>
    /// The guild the bridge channel lives in, used for nickname reads. Normally left empty: the
    /// relay discovers it from <c>GET /channels/{id}</c> on first need and caches it durably, the
    /// same way it discovers <see cref="BotUserId"/>. Set it only to override that.
    /// </summary>
    public string? GuildId { get; set; }

    /// <summary>
    /// The configured guild override, or null when absent or blank. Blank collapses here rather
    /// than at the call site for the same reason as <see cref="ConfiguredBotUserId"/> below — an
    /// empty string read as a real id would send every nickname lookup to <c>guilds//members/...</c>
    /// and fail forever, which looks exactly like the feature being off.
    /// </summary>
    public string? ConfiguredGuildId =>
        string.IsNullOrWhiteSpace(GuildId) ? null : GuildId;

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

    /// <summary>
    /// Embed colour for game -> Discord lines. 0x2ECC71 green, per the bridge plan. No longer used
    /// by guild chat, which posts as a plain message — kept because it is what a revert of that
    /// change would read again, and because it documents the colour the channel used to be.
    /// </summary>
    public int EmbedColor { get; set; } = 3066993;

    /// <summary>
    /// Operator switch for the world boss alert feed: lines whose text begins with one of
    /// <see cref="ResolvedAlertTags"/> publish as a coloured embed instead of as ordinary chat.
    /// Off by default like the switches above — a redeploy alone must never change how a guild's
    /// channel looks.
    /// </summary>
    public bool AlertsEnabled { get; set; }

    /// <summary>
    /// Tag -> embed colour. Configuring ANY tag replaces the built-in set outright rather than
    /// merging with it (the property is null until bound), so an operator can rename or remove a
    /// tag, not only add one.
    /// </summary>
    public Dictionary<string, int>? AlertTags { get; set; }

    /// <summary>
    /// The tags actually in force, matched case-INSENSITIVELY. Case-insensitive on purpose: the
    /// exact casing the server broadcasts is unverified until the first live alert, and a mismatch
    /// there would silently drop every alert while looking like the feature simply not working.
    /// Insensitivity costs nothing and removes that failure mode.
    /// </summary>
    public IReadOnlyDictionary<string, int> ResolvedAlertTags =>
        AlertTags is { Count: > 0 }
            ? new Dictionary<string, int>(AlertTags, StringComparer.OrdinalIgnoreCase)
            : DefaultAlertTags;

    /// <summary>0x2ECC71 green for PvE, 0xE74C3C red for PvP — the user's choice, 2026-08-10.</summary>
    private static readonly Dictionary<string, int> DefaultAlertTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["[PvE World Boss]"] = 3066993,
        ["[PvP World Boss]"] = 15158332
    };

    /// <summary>
    /// Role pinged when an alert publishes, as a snowflake string — snowflakes overflow JSON
    /// readers that guess int, same as <see cref="ChannelId"/>. Empty (the default) means alerts
    /// publish without pinging anyone, which is what every existing deployment gets on upgrade.
    /// </summary>
    public string? AlertRoleId { get; set; }

    /// <summary>
    /// The role id actually used, or null when unset OR not a usable snowflake.
    ///
    /// This is a correctness check rather than tidiness, and it bounds the VALUE and not merely the
    /// alphabet. The mention is built by interpolating this into the message content and repeating
    /// it in <c>allowed_mentions.roles</c>, so a value carrying anything else — a stray "&lt;@&amp;"
    /// wrapper pasted from Discord, a typo — would either post visible junk on every alert or
    /// smuggle arbitrary text into a field the relay authors.
    ///
    /// The range check is the one that matters most, because its failure is not a missing ping.
    /// Snowflakes are unsigned 64-bit, and Discord rejects an out-of-range id in
    /// <c>allowed_mentions</c> with a 400 — a payload it will NEVER accept, so the alert is parked,
    /// retried and finally DROPPED by the outbox, losing the very lines the feed exists to deliver.
    /// Rejecting the value here degrades it to "no ping" instead, which is the same as the feature
    /// being off.
    /// </summary>
    public string? ResolvedAlertRoleId => IsSnowflake(AlertRoleId) ? AlertRoleId : null;

    /// <summary>
    /// True for a bare, in-range Discord snowflake. Both halves are needed:
    /// <see cref="ulong.TryParse(string?, out ulong)"/> alone would accept " +123 " (it permits
    /// sign and surrounding whitespace), and the digit scan alone would accept a number too large
    /// to be a snowflake. See <see cref="ResolvedAlertRoleId"/> for why the range half matters.
    /// </summary>
    public static bool IsSnowflake(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit) && ulong.TryParse(value, out _);

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
    /// The alert feed is live: enabled AND the webhook is usable. Deliberately NOT dependent on the
    /// bot token — alerts arrive from the game and go out through the same webhook as chat, so none
    /// of the bot-side configuration is involved.
    /// </summary>
    public bool IsAlertsConfigured => AlertsEnabled && IsConfigured;

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
