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
    private bool _selfPingFailing;

    /// <summary>Whether the ticker is configured to run at all (0 or less disables it).</summary>
    public bool Enabled => Interval() is not null;

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

        // Outside the block above so a Discord failure never costs us the thing that keeps the
        // pool alive — which, if it stops, costs us every future tick rather than this one.
        await SelfPingAsync(cancellationToken);

        Interlocked.Increment(ref _ticks);
        Interlocked.Exchange(ref _lastTickTicksUtc, DateTimeOffset.UtcNow.UtcTicks);
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

            if (_selfPingFailing)
            {
                logger.LogInformation("Self-ping recovered");
                _selfPingFailing = false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException
                                       or InvalidOperationException)
        {
            ReportSelfPing($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a self-ping failure once per outage rather than once per tick. A wrong URL would
    /// otherwise write a warning every interval, for ever, and drown the log the operator needs.
    /// </summary>
    private void ReportSelfPing(string detail)
    {
        if (_selfPingFailing)
        {
            return;
        }

        _selfPingFailing = true;
        logger.LogWarning("Self-ping failed ({Detail}); the app pool may idle-stop when nobody is " +
                          "playing. Check Relay:SelfPingUrl", detail);
    }

    /// <summary>The configured interval, floored, or null when the ticker is switched off.</summary>
    private TimeSpan? Interval()
    {
        var seconds = options.CurrentValue.BackgroundTickSeconds;

        if (seconds <= 0 || double.IsNaN(seconds))
        {
            return null;
        }

        if (seconds >= MaximumInterval.TotalSeconds)
        {
            return MaximumInterval;
        }

        var interval = TimeSpan.FromSeconds(seconds);

        return interval < MinimumInterval ? MinimumInterval : interval;
    }
}
