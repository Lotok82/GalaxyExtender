using System.Reflection;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Unauthenticated by design: it exposes no secrets and is the endpoint an external
        // pinger would hit. Everything reported here is either public or a boolean.
        app.MapGet("/api/v1/health", (
            HostProbe probe,
            IOptions<RelayOptions> relayOptions,
            IOptions<DiscordOptions> discordOptions,
            IStateStore stateStore,
            PresenceTracker presenceTracker,
            BackgroundTicker ticker,
            BotCommandScanner commands,
            HttpContext http) =>
        {
            var now = DateTimeOffset.UtcNow;
            var appData = probe.CheckAppData();

            var (outboxDepth, dedupeEntries, lastForwardUtc, stage2Pending, stage2Cursor, lastCleanupUtc,
                    botUserIdKnown, lastAlertPingUtc) =
                stateStore.Read(state => (
                    state.Outbox.Count,
                    state.Dedupe.Count,
                    state.LastForwardUtc,
                    state.Stage2Pending.Count,
                    state.Stage2Cursor is not null,
                    state.LastCleanupUtc,
                    state.BotUserId is not null,
                    state.LastAlertPingUtc));

            var presence = presenceTracker.Snapshot();

            return Results.Ok(new
            {
                status = "ok",
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                utcNow = now,

                // Process identity — the app-pool behaviour probe. Call this twice a few minutes
                // apart: a changed processId means a recycle or a web garden, and a reset uptime
                // means the pool is idle-stopping.
                process = new
                {
                    id = HostProbe.ProcessId,
                    startedUtc = HostProbe.StartedUtc,
                    uptimeSeconds = (long)(now - HostProbe.StartedUtc).TotalSeconds,
                    processStartedUtc = HostProbe.ProcessStartedUtc,
                    framework = probe.Framework,
                    os = probe.OperatingSystem
                },

                // What the host tells the app about the request. Needed before RequireHttps can
                // safely be turned on — behind some proxy setups isHttps reads false on a
                // genuinely TLS-terminated request, which would 403 every client.
                request = new
                {
                    scheme = http.Request.Scheme,
                    isHttps = http.Request.IsHttps,
                    host = http.Request.Host.Value,
                    forwardedProto = http.Request.Headers["X-Forwarded-Proto"].ToString(),
                    forwardedFor = string.IsNullOrEmpty(http.Request.Headers["X-Forwarded-For"].ToString())
                        ? null
                        : "present"
                },

                storage = new
                {
                    appDataWritable = appData.Writable,
                    appDataReadBackOk = appData.ReadBackOk,
                    appDataError = appData.Error,
                    fileLoggingError = HostProbe.FileLoggingError
                },

                config = new
                {
                    requireHttps = relayOptions.Value.RequireHttps,
                    dedupeWindowSeconds = relayOptions.Value.DedupeWindowSeconds,
                    apiKeyCount = relayOptions.Value.ApiKeys.Count,
                    discordConfigured = discordOptions.Value.IsConfigured,
                    stage2Configured = discordOptions.Value.IsStage2Configured,
                    cleanupConfigured = discordOptions.Value.IsCleanupConfigured,
                    commandsConfigured = discordOptions.Value.IsCommandsConfigured,
                    alertsConfigured = discordOptions.Value.IsAlertsConfigured,
                    // Whether a role is set, not which — same reasoning as selfPing below: this
                    // document is unauthenticated. Paired with relay.lastAlertPingUtc it answers
                    // the only question an operator asks here, "why did that alert not ping?".
                    alertRoleConfigured = discordOptions.Value.ResolvedAlertRoleId is not null,
                    alertPingIntervalMinutes = relayOptions.Value.AlertPingIntervalMinutes,
                    presenceOnlineWindowSeconds = relayOptions.Value.PresenceOnlineWindowSeconds
                },

                // Forwarding state. outboxDepth > 0 means undelivered lines are waiting for the
                // next authenticated request (or heartbeat) to drain them.
                relay = new
                {
                    outboxDepth,
                    dedupeEntries,
                    lastForwardUtc,
                    stage2Pending,
                    stage2CursorInitialised = stage2Cursor,
                    lastCleanupUtc,
                    // In-memory since the scan claim stopped being durable: like the ticker's
                    // readings, read it against process.startedUtc — a reset means a recycle.
                    lastCommandScanUtc = commands.LastScanUtc,
                    botUserIdKnown,
                    lastAlertPingUtc
                },

                // Is the timer that carries the outbox, the cleanup and the bot when nobody is in
                // game actually running? This is the reading the whole feature turns on: `ticks`
                // climbing while `presence.online` is 0 means it works on this host. `ticks` back
                // at a low number with a fresh `process.uptimeSeconds` means the pool idle-stopped
                // and killed it — the case Relay:SelfPingUrl exists for.
                backgroundTicker = new
                {
                    enabled = ticker.Enabled,
                    intervalSeconds = relayOptions.Value.BackgroundTickSeconds,
                    ticks = ticker.Ticks,
                    lastTickUtc = ticker.LastTickUtc,
                    lastError = ticker.LastError,
                    // Whether, not where: the URL is the operator's business and this document is
                    // unauthenticated.
                    selfPing = !string.IsNullOrWhiteSpace(relayOptions.Value.SelfPingUrl),
                    // Configured is not the same as working, and only this says which. A wrong
                    // SelfPingUrl otherwise fails silently for ever — the ticker keeps reporting
                    // healthy right up until the idle timer it was meant to defeat kills it. The
                    // detail can name the host it tried, which is the relay's own public address
                    // and no more sensitive than `storage.appDataError` already published here.
                    selfPingError = ticker.SelfPingError
                },

                // Who the relay believes is running the extension — the same figures the Discord
                // "@bot status" command reports, so a disagreement between them is a bug in the
                // reply wording rather than in what the relay knows.
                presence = new
                {
                    online = presence.Online,
                    known = presence.Known,
                    lastSeenUtc = presence.LastSeenUtc
                },

                // Not run automatically — it is an outbound network call, and it requires the
                // relay key. GET /api/v1/health/outbound with X-Relay-Key.
                outboundProbe = "/api/v1/health/outbound"
            });
        });

        // Separate endpoint so the cheap health check stays cheap and we do not hit Discord on
        // every uptime ping. Requires the relay key (enforced by ApiKeyAuthenticationMiddleware,
        // which only exempts the base health document): every hit makes the shared host's IP call
        // discord.com, and an anonymous hammering could get that IP rate-limited or banned by
        // Discord — punishing every tenant and breaking Phase 3 forwarding.
        app.MapGet("/api/v1/health/outbound", async (HostProbe probe, CancellationToken cancellationToken) =>
        {
            var discord = await probe.CheckDiscordReachableAsync(cancellationToken);
            return Results.Ok(new
            {
                discord = new
                {
                    reachable = discord.Reachable,
                    statusCode = discord.StatusCode,
                    elapsedMs = discord.ElapsedMs,
                    error = discord.Error
                }
            });
        });
    }
}
