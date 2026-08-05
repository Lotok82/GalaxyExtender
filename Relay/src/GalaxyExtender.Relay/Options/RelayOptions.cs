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
