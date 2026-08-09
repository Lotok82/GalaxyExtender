using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// The one part of the relay that does not wait to be asked (R12).
///
/// Everything else here is request-driven, because shared IIS hosting gives no guarantee that a
/// process is alive between requests. That design has one hole, and it is exactly the hole that
/// matters: with nobody in game there are no requests at all, so the outbox does not drain, the
/// channel is not swept, and — most visibly — the bot neither answers <c>@bot status</c> nor tells
/// anyone their message is not reaching the guild room. "Is the bridge up?" is asked precisely when
/// the answer is no, which is precisely when nothing was listening.
///
/// So this timer runs the same three pieces of work a request would have carried. It adds no new
/// behaviour and no new Discord traffic beyond what an equivalent request would have caused: each
/// piece keeps its own durable interval stamp, so a tick that arrives inside the interval is a
/// couple of in-memory reads and nothing more.
///
/// It does NOT replace the request-piggybacked calls, for a reason that outlives this file: IIS
/// idle-stops a worker process that has not had a REQUEST for a while, and CPU activity does not
/// count. A background thread cannot keep itself alive on a host that has decided the site is idle,
/// so the ticker is best-effort by nature. <see cref="RelayOptions.SelfPingUrl"/> exists for that
/// case, and <c>/health</c> reports enough to tell whether it is needed.
/// </summary>
public sealed class BackgroundTicker(
    Outbox outbox,
    ChannelCleaner cleaner,
    BotCommandScanner commands,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<RelayOptions> options,
    ILogger<BackgroundTicker> logger) : BackgroundService
{
    public const string HttpClientName = "self-ping";

    /// <summary>
    /// Floor on the configured interval. A mistyped value must not turn the relay into something
    /// that hammers its own state file and Discord's rate limiter; nothing the tick does is
    /// meaningful at sub-second resolution anyway.
    /// </summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling on the configured interval. An hour is already far past useless — a mention goes
    /// stale after <see cref="RelayOptions.CommandMaxAgeSeconds"/> — and clamping here means an
    /// absurd value cannot overflow <see cref="TimeSpan.FromSeconds"/> inside the loop.
    /// </summary>
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);

    private long _ticks;
    private long _lastTickTicksUtc;
    private volatile string? _lastError;
    private volatile string? _selfPingError;
    private volatile bool _running;
    private bool _selfPingLogged;

    /// <summary>
    /// Whether the loop is actually running — deliberately the loop's own state, not a re-read of
    /// the configured interval. The two can disagree: switching the interval to 0 at runtime stops
    /// the loop for good (<see cref="BackgroundService"/> never restarts it), so a config that said
    /// "enabled" would report a ticker that no longer exists — and a frozen <see cref="Ticks"/>
    /// alongside it reads exactly like the pool having idle-stopped, sending the operator after the
    /// wrong problem. Restoring a non-zero interval therefore needs an app restart, which on IIS is
    /// what editing appsettings or an environment variable does anyway.
    /// </summary>
    public bool Enabled => _running;

    /// <summary>Completed ticks since app start. In-memory: a recycle resets it, which is useful —
    /// compared against process uptime it says whether the ticker survived the quiet hours.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>When the last tick finished, or null if none has. Read against
    /// <c>process.startedUtc</c>: a gap means the pool stopped, not that the ticker wedged.</summary>
    public DateTimeOffset? LastTickUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastTickTicksUtc);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>The last tick's failure, or null if the last tick was clean.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// The last self-ping's failure, or null if it succeeded or none is configured. Reported
    /// separately from <see cref="LastError"/> because the two mean opposite things: a tick error
    /// says the relay's own work is failing, a self-ping error says the KEEP-ALIVE is failing and
    /// the ticker may be about to be idle-stopped out of existence. Without this the setting an
    /// operator reaches for when the ticker is dying — and the one most likely to be mistyped —
    /// has no reading anywhere but a log file that is awkward to reach on shared hosting.
    /// </summary>
    public string? SelfPingError => _selfPingError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Interval() is not { } interval)
        {
            logger.LogInformation(
                "Background ticker disabled (Relay:BackgroundTickSeconds = {Configured}); " +
                "outbox, cleanup and bot commands run on request traffic only",
                options.CurrentValue.BackgroundTickSeconds);
            return;
        }

        logger.LogInformation("Background ticker started at {Interval:0.##} s intervals",
            interval.TotalSeconds);

        // Set before the first await, so it is already true when StartAsync returns and /health
        // cannot catch a started host reporting a ticker that has not begun.
        _running = true;

        try
        {
            // Delay BEFORE the first tick, not after: a recycle under load must not add a burst of
            // Discord calls to whatever caused it, and the request that woke the app has already
            // carried this work.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                await TickAsync(stoppingToken);

                if (Interval() is not { } current)
                {
                    // Switched off under us by a config reload. Stopping the loop rather than idling
                    // in it means the "disabled" state is one thing, not two.
                    logger.LogInformation("Background ticker stopping: interval set to 0");
                    return;
                }

                interval = current;
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// One tick's work — deliberately the same three calls, in the same order, that
    /// <c>POST /heartbeat</c> makes. If the two ever diverge, one of them is wrong.
    /// </summary>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await outbox.DrainAsync(cancellationToken);
            await cleaner.SweepIfDueAsync(cancellationToken);
            await commands.ScanIfDueAsync(cancellationToken);
            _lastError = null;
        }
        catch (Exception ex)
        {
            // An exception escaping ExecuteAsync stops the entire host by default
            // (BackgroundServiceExceptionBehavior.StopHost), taking the request path down with it.
            // Nothing a tick does is worth the relay going dark, so everything is caught here and
            // reported through /health instead.
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Background tick failed");
        }

        // Counted before the self-ping, not after. `ticks` is the one reading that separates "the
        // host killed the ticker" from "the ticker is alive and something inside it is failing";
        // an optional keep-alive must not be able to blur that distinction by freezing the counter.
        Interlocked.Increment(ref _ticks);
        Interlocked.Exchange(ref _lastTickTicksUtc, DateTimeOffset.UtcNow.UtcTicks);

        // Outside the block above so a Discord failure never costs us the thing that keeps the
        // pool alive — which, if it stops, costs us every future tick rather than this one.
        // SelfPingAsync swallows everything itself; see the catch there for why it must.
        await SelfPingAsync(cancellationToken);
    }

    /// <summary>
    /// GETs the relay's own public health document, when configured. Sole purpose: an inbound
    /// request is the only thing that resets an IIS idle timer, so on a host that idle-stops the
    /// pool this is what keeps the ticker (and the outbox, and the bot) alive through a night with
    /// nobody playing. Off by default — it is a workaround for a host behaviour, and
    /// <c>/health</c>'s <c>process.uptimeSeconds</c> is how you find out whether you have it.
    /// </summary>
    private async Task SelfPingAsync(CancellationToken cancellationToken)
    {
        if (options.CurrentValue.SelfPingUrl is not { } url || string.IsNullOrWhiteSpace(url))
        {
            // Cleared, not left standing: a URL removed after a failure must not leave a stale
            // error on /health describing a ping that is no longer even attempted.
            _selfPingError = null;
            _selfPingLogged = false;
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                ReportSelfPing($"HTTP {(int)response.StatusCode}");
                return;
            }

            if (_selfPingLogged)
            {
                logger.LogInformation("Self-ping recovered");
                _selfPingLogged = false;
            }

            _selfPingError = null;
        }
        catch (Exception ex)
        {
            // Shutting down. Not a failure, and reporting it would leave a misleading error
            // sitting on /health for whatever reads it during the stop.
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Deliberately unfiltered, and this is the whole reason the method has its own handler.
            // Anything escaping here escapes ExecuteAsync too, and .NET's default
            // BackgroundServiceExceptionBehavior.StopHost would then take the ENTIRE relay down —
            // request path included — over an optional workaround for one host's idle timer. A
            // listed set of exception types is not good enough: HttpClient answers an absolute URL
            // with an unsupported scheme (`htp://…`, a plausible typo) with NotSupportedException,
            // which no such list would have thought to include. The cost of being wrong here is
            // total, so nothing gets through.
            ReportSelfPing($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Records a self-ping failure for <c>/health</c> every time, but LOGS it only once per outage:
    /// a wrong URL would otherwise write a warning every interval, for ever, and drown the log the
    /// operator needs. The <c>/health</c> field carries no such risk and is what the README sends
    /// people to, so it always reflects the latest attempt.
    /// </summary>
    private void ReportSelfPing(string detail)
    {
        _selfPingError = detail;

        if (_selfPingLogged)
        {
            return;
        }

        _selfPingLogged = true;
        logger.LogWarning("Self-ping failed ({Detail}); the app pool may idle-stop when nobody is " +
                          "playing. Check Relay:SelfPingUrl", detail);
    }

    /// <summary>The configured interval, clamped, or null when the ticker is switched off.</summary>
    private TimeSpan? Interval() => ClampInterval(options.CurrentValue.BackgroundTickSeconds);

    /// <summary>
    /// Turns a configured <see cref="RelayOptions.BackgroundTickSeconds"/> into the interval the
    /// loop will actually use, or null for "switched off". Public because it is the guard between a
    /// mistyped config value and a timer that hammers the state file and Discord's rate limiter, and
    /// that guard deserves to be asserted directly rather than inferred from how fast a test ticks.
    /// </summary>
    public static TimeSpan? ClampInterval(double seconds)
    {
        // NaN first: every comparison against it is false, so a later `<` test would silently let
        // it through into TimeSpan.FromSeconds, which throws.
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return null;
        }

        // Also catches PositiveInfinity, which would otherwise overflow TimeSpan.FromSeconds.
        if (seconds >= MaximumInterval.TotalSeconds)
        {
            return MaximumInterval;
        }

        var interval = TimeSpan.FromSeconds(seconds);

        return interval < MinimumInterval ? MinimumInterval : interval;
    }
}
