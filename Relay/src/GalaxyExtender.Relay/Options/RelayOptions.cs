namespace GalaxyExtender.Relay.Options;

/// <summary>
/// Relay behaviour. Bound from the "Relay" configuration section.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// Reject non-HTTPS requests. Defaults to <c>false</c> so the Phase 0 deploy spike cannot
    /// lock us out before we know what scheme IIS actually reports to the app. Flip to true
    /// once <c>/api/v1/health</c> confirms <c>isHttps</c> is reported correctly on the host.
    /// </summary>
    public bool RequireHttps { get; set; }

    /// <summary>
    /// How long a message hash is remembered for de-duplication. Long enough to cover the
    /// spread between guild members' clients posting the same line, short enough that it does
    /// not interfere with the occurrence counter's job.
    /// </summary>
    public int DedupeWindowSeconds { get; set; } = 15;

    /// <summary>How long a batchId is remembered, for retry idempotency.</summary>
    public int BatchIdWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Where the durable state document lives. Defaults to App_Data/relay-state.json under the
    /// content root; overridable mainly so tests can isolate state per test host.
    /// </summary>
    public string? StateFilePath { get; set; }

    /// <summary>Undelivered webhook payloads kept at most; beyond this the oldest is dropped.</summary>
    public int OutboxMaxEntries { get; set; } = 200;

    /// <summary>Delivery attempts before an outbox entry is dropped (with an error log).</summary>
    public int OutboxMaxAttempts { get; set; } = 10;

    public int MaxLinesPerBatch { get; set; } = 50;

    public int MaxLineLength { get; set; } = 512;

    /// <summary>
    /// Requests allowed per minute per key (or per IP when no key is presented). Abuse mitigation,
    /// not accounting — the window resets on an app-pool recycle, which is acceptable.
    ///
    /// Sized for the SHARED-key setup below: the partition is the key, so every guild member
    /// draws from the same bucket. One client at the ~1.5 s batch cadence is ~40 requests/minute;
    /// the default covers ~a dozen simultaneously chatty clients plus retries and the Stage 2
    /// poll. If the guild outgrows it, raise this rather than assuming per-client budgets.
    /// </summary>
    public int RateLimitPermitsPerMinute { get; set; } = 600;

    // ------------------------------------------------------------------
    // Stage 2 (Discord -> game) tunables. Code defaults are the contract
    // values pinned in README.md; config-overridable mainly for tests.
    // ------------------------------------------------------------------

    /// <summary>Seconds an unacked claim stays invisible before redelivery to the next poller.</summary>
    public int Stage2RedeliveryTimeoutSeconds { get; set; } = 60;

    /// <summary>Total claims per message: 1 initial delivery + 2 redeliveries, then dropped.</summary>
    public int Stage2MaxDeliveries { get; set; } = 3;

    /// <summary>Pending messages older than this are dropped (counted), not injected stale.</summary>
    public int Stage2TtlSeconds { get; set; } = 300;

    /// <summary>Pending queue size cap; beyond it the oldest entries are dropped (counted).</summary>
    public int Stage2MaxPending { get; set; } = 50;

    /// <summary>Messages handed to one poll — sized so a claimant injecting at ~1 line/s
    /// finishes well inside the redelivery timeout.</summary>
    public int Stage2MaxPerPoll { get; set; } = 5;

    /// <summary>
    /// How long a Discord fetch result is considered fresh. Polls inside the window skip the
    /// Discord call entirely, keeping the Discord-facing request rate independent of player
    /// count. In-memory only — a recycle just causes one early fetch.
    /// </summary>
    public double Stage2FetchCacheSeconds { get; set; } = 2.5;

    /// <summary>
    /// How long a stored server nickname is reused before Discord is asked about that person again
    /// (<c>Discord:NicknamesEnabled</c>). A day, because renaming yourself in a Discord server is
    /// something people do a handful of times a year — so this is the interval at which the whole
    /// feature costs anything at all: one member read per person who SPOKE since their entry aged
    /// out, and nothing whatsoever for anyone who did not.
    ///
    /// The names themselves live in the state file (<see cref="RelayState.Nicknames"/>), so the
    /// window survives recycles and the answer is not re-bought every cold start. A rename shows
    /// up in the guild room on that person's first message after their entry expires.
    /// </summary>
    public int NicknameRefreshHours { get; set; } = 24;

    /// <summary>
    /// Minimum gap between world boss alert role pings (<c>Discord:AlertRoleId</c>). Alerts inside
    /// the window still publish, just without notifying anyone — the limit is on the ping, never on
    /// the alert.
    ///
    /// A real ceiling rather than a smoothing average: the failure this guards against is a boss
    /// chain, or the same broadcast repeating, turning an opt-in role into something people mute.
    /// <c>0</c> disables it and every alert pings.
    /// </summary>
    public int AlertPingIntervalMinutes { get; set; } = 15;

    // ------------------------------------------------------------------
    // Channel-history cleanup (R10) tunables. Config-overridable mainly
    // for tests; the code defaults are the intended behaviour.
    // ------------------------------------------------------------------

    /// <summary>Bridge-channel messages older than this are deleted (pinned ones preserved).</summary>
    public int CleanupMaxAgeHours { get; set; } = 5;

    /// <summary>
    /// Minimum time between sweeps. The sweep piggybacks on request traffic (no background
    /// timers on this host), so this is a floor, not a schedule: with nobody online the channel
    /// simply stays untouched until the next authenticated request.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Per-message DELETE calls allowed per sweep, for the over-14-day tail that bulk-delete
    /// rejects. Bounded like the outbox drain so no single request pays for a long backlog;
    /// only ever relevant on a first run against an old channel.
    /// </summary>
    public int CleanupMaxSingleDeletesPerSweep { get; set; } = 5;

    // ------------------------------------------------------------------
    // Presence and bot commands (R11). Presence is what the status
    // command reports; the scan is how the command is heard at all.
    // ------------------------------------------------------------------

    /// <summary>
    /// How recently a client must have checked in to count as online. The extension pings presence
    /// every 60 s, so this tolerates two missed pings before someone is called offline — sized to
    /// avoid a hiccup reading as "the bridge is down" rather than to be maximally current.
    /// </summary>
    public int PresenceOnlineWindowSeconds { get; set; } = 180;

    /// <summary>
    /// Minimum time between durable writes of one client's presence stamp. The state document is
    /// rewritten in full on every mutation and a Stage 2 poll arrives every 5 s per client, so
    /// without this throttle idle polling alone would keep the disk busy. A client whose character
    /// or galaxy label changed is written immediately regardless.
    /// </summary>
    public int PresenceWriteIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How long a silent client still counts as "known" (the connected-count denominator). Someone
    /// who has not launched the game in a week is not usefully part of "who has this running".
    ///
    /// A week rather than a month because of what an extension rollout does to the roster: an
    /// upgraded install arrives under a new client id, and where
    /// <see cref="Services.PresenceTracker"/> cannot tell which old entry it replaced, the stale
    /// one can only age out. This is the bound on how long that overcount lasts.
    /// </summary>
    public int PresenceRetentionDays { get; set; } = 7;

    /// <summary>
    /// Hard cap on the presence roster. Guards the state document against a client whose
    /// self-reported id varies per launch, which would otherwise grow the file without limit.
    /// </summary>
    public int PresenceMaxClients { get; set; } = 200;

    /// <summary>
    /// Minimum time between bot-command scans. Like the cleanup sweep this is a floor, not a
    /// schedule: the scan piggybacks request traffic, so with nobody online the heartbeat pinger's
    /// cadence is what actually decides how quickly a "status" mention is answered.
    ///
    /// One exception, deliberate: a mention the Stage 2 reader has already fetched (and suppressed
    /// from the guild room) triggers the next scan regardless of this interval — see
    /// <see cref="Services.BotCommandScanner.NoteAddressedMention"/>. The interval bounds how often
    /// the scan goes LOOKING for work; it was never meant to sit on work already found.
    /// </summary>
    public double CommandScanIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Mentions older than this get no reply. A status line about a moment that has passed —
    /// after a recycle, or a night with nobody online — is worse than silence.
    /// </summary>
    public int CommandMaxAgeSeconds { get; set; } = 300;

    /// <summary>
    /// Replies posted per scan. Bounds the channel noise (and the Discord call count) if several
    /// people mention the bot at once; the excess is dropped rather than deferred.
    /// </summary>
    public int CommandMaxRepliesPerScan { get; set; } = 3;

    /// <summary>
    /// Minimum time between unprompted "nobody is online to receive this" notices. One notice tells
    /// everyone in the channel what they need to know, so a conversation held while the guild is
    /// offline must not be annotated line by line — that would make the bot the noisiest member of
    /// its own channel.
    /// </summary>
    public int DeliveryNoticeIntervalMinutes { get; set; } = 15;

    // ------------------------------------------------------------------
    // Background ticker (R12). The only work in the relay that does not
    // wait to be asked. See Services/BackgroundTicker.cs.
    // ------------------------------------------------------------------

    /// <summary>
    /// Seconds between background ticks. A tick runs the outbox drain, the cleanup sweep and the
    /// bot-command scan — exactly what <c>POST /heartbeat</c> runs — so that they still happen when
    /// there is no request traffic at all, which is to say when nobody is in game.
    ///
    /// <c>0</c> disables the ticker, restoring the purely request-driven behaviour that shipped
    /// before it. Values are clamped to 1 s–1 h.
    ///
    /// 60 s is chosen against <see cref="CommandMaxAgeSeconds"/>, not against how fresh anything
    /// feels: a mention older than that gets no reply, so a tick slower than 5 minutes would leave
    /// the bot answering nothing at exactly the times this exists for.
    ///
    /// Cost at the shipped defaults, since a shared host is the constraint: each piece keeps its own
    /// interval stamp, so a tick arriving inside a piece's window costs a couple of in-memory
    /// reads. The cleanup sweep is genuinely in that position — its window is
    /// <see cref="CleanupIntervalMinutes"/>, far longer than a tick. The command scan is NOT: at 60 s
    /// per tick against a <see cref="CommandScanIntervalSeconds"/> of 15 the scan is due on every
    /// tick, so the real steady state with the guild empty is ONE channel read per tick — 60/hour.
    /// (The scan claim used to be a durable stamp, which added a state-file write per tick; it is
    /// in-memory now.) That is the floor this feature costs; raising the tick interval lowers it,
    /// at the price of the bot's response time. Only a tick faster than
    /// <see cref="CommandScanIntervalSeconds"/> gets the free ride.
    /// </summary>
    public double BackgroundTickSeconds { get; set; } = 60;

    /// <summary>
    /// Absolute URL the ticker GETs once per tick, or empty for none. Intended value is the
    /// relay's own public health document — <c>https://host/relay/api/v1/health</c> — which is
    /// unauthenticated and does no outbound work.
    ///
    /// This is a workaround for one specific host behaviour and nothing else: IIS idle-stops a
    /// worker process that has gone <see cref="!:idleTimeout"/> without a REQUEST, and background
    /// CPU activity does not count, so on such a host the ticker is killed by the very quiet
    /// period it was added for. An inbound request is the only thing that resets that timer.
    ///
    /// Off by default because it is not free — it is an outbound call per tick, and on a host
    /// that does not idle-stop (or where the pool is set to <c>idleTimeout=0</c>) it buys nothing.
    /// Turn it on only after <c>/health</c> shows the pool actually stopping: read
    /// <c>process.uptimeSeconds</c> resetting, or <c>backgroundTicker.ticks</c> starting over.
    /// </summary>
    public string? SelfPingUrl { get; set; }

    /// <summary>
    /// The set of currently valid API keys, as <c>label -> secret</c>. The label is for the
    /// operator's benefit only (it names the key in logs); it is NOT matched against the client id
    /// in the request body, and clients never send it.
    ///
    /// Authentication is "does the presented <c>X-Relay-Key</c> equal any secret in here" — so the
    /// normal setup is a SINGLE shared entry handed to everyone who should be allowed to relay:
    /// <code>Relay__ApiKeys__guild = &lt;generated GUID&gt;</code>
    /// No per-user entry is needed, and adding a guild member requires no config change.
    ///
    /// It is a collection rather than one string so keys can be ROTATED without a flag day: add
    /// the replacement, distribute it while both are accepted, then delete the old one. That
    /// matters because this secret lives in plaintext in DiscordBridge.ini on other people's
    /// machines and will eventually leak. The same mechanism allows optional per-person keys later
    /// if an individual needs revoking.
    ///
    /// Never place real keys in appsettings.json — see ConfigurationHygieneTests. Use user-secrets
    /// locally and environment variables (or a git-ignored appsettings.Production.json) on the host.
    /// </summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
