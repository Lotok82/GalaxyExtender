using System.Threading.RateLimiting;
using GalaxyExtender.Relay.Endpoints;
using GalaxyExtender.Relay.Middleware;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// Before anything else, so uptime measures app start rather than first request.
HostProbe.StampStartup();

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging. Console output is invisible under IIS, so the file sink is the only
// way to see anything on the host.
//
// Logging must never be the reason the app fails to start. An unwritable App_Data
// on shared hosting would otherwise produce an HTTP 500.30 with no way to
// diagnose it — /api/v1/health is what reports the problem, and it cannot report
// anything if we throw first. Note the failure can surface at CreateLogger()
// rather than at WriteTo.File(), because that is when the sink opens the file.
// ---------------------------------------------------------------------------
Serilog.Core.Logger BuildLogger(string? logDirectory)
{
    var configuration = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("pid", Environment.ProcessId)
        .WriteTo.Console();

    if (logDirectory is not null)
    {
        configuration = configuration.WriteTo.File(
            Path.Combine(logDirectory, "relay-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true);
    }

    return configuration.CreateLogger();
}

try
{
    var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "logs");
    Directory.CreateDirectory(logDirectory);
    Log.Logger = BuildLogger(logDirectory);
}
catch (Exception ex)
{
    // Console-only fallback. Surfaced through /api/v1/health, since by definition we
    // cannot write this to the log file.
    HostProbe.FileLoggingError = $"{ex.GetType().Name}: {ex.Message}";
    Log.Logger = BuildLogger(null);
}

builder.Host.UseSerilog();

// ---------------------------------------------------------------------------
// Configuration and services
// ---------------------------------------------------------------------------
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));

builder.Services.AddHttpClient("discord-probe", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GalaxyExtenderRelay/0.1 (+https://github.com/Lotok82)");
});

builder.Services.AddSingleton<HostProbe>();
builder.Services.AddSingleton<ApiKeyValidator>();

// Forwarding pipeline (Phases 2-4): durable state, dedupe, sanitize, publish, outbox.
builder.Services.AddSingleton<IStateStore, FileStateStore>();
builder.Services.AddSingleton<DedupeService>();
builder.Services.AddSingleton<DiscordPublisher>();
builder.Services.AddSingleton<Outbox>();

// Stage 2 read path (R3-R7): on-demand channel fetch and the claim/ack work queue.
builder.Services.AddSingleton<DiscordReader>();
builder.Services.AddSingleton<Stage2Queue>();

// Channel-history cleanup (R10): request-piggybacked sweep of the bridge channel.
builder.Services.AddSingleton<ChannelCleaner>();

// Presence and bot commands (R11): who is running the extension, and the bot that reports it.
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<BotCommandScanner>();

// The background ticker (R12): the same work a request carries, run on a timer so it still happens
// with nobody in game. Registered as a singleton AND as the hosted service so /health can report
// whether it is actually alive — the question shared hosting exists to make interesting.
builder.Services.AddSingleton<BackgroundTicker>();
builder.Services.AddHostedService(services => services.GetRequiredService<BackgroundTicker>());

builder.Services.AddHttpClient(BackgroundTicker.HttpClientName, client =>
{
    // Short: a self-ping that hangs would stall the tick it is attached to, and the response body
    // is discarded anyway. Its only job is to be an inbound request.
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GalaxyExtenderRelay/0.4 (+https://github.com/Lotok82)");
});

builder.Services.AddHttpClient(DiscordPublisher.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GalaxyExtenderRelay/0.2 (+https://github.com/Lotok82)");
});

builder.Services.AddHttpClient(DiscordReader.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://discord.com/api/v10/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GalaxyExtenderRelay/0.3 (+https://github.com/Lotok82)");
});

// Rate limiting runs before authentication in the pipeline, so an unauthenticated flood is capped
// too. Partitioned on a hash of the key — never the key itself — but ONLY once the key has
// validated: an unvalidated header would let an attacker rotate random keys and mint a fresh
// permit bucket per request, defeating the cap this exists to provide. Anything else — no key,
// unrecognised key — shares the caller's per-IP partition.
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    rateLimiter.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };

    rateLimiter.AddPolicy(ChatEndpoints.RateLimitPolicy, httpContext =>
    {
        var validation = httpContext.RequestServices
            .GetRequiredService<ApiKeyValidator>()
            .Validate(httpContext.Request);

        var partition = validation.IsValid
            ? $"key:{ApiKeyValidator.Fingerprint(httpContext.Request.Headers[ApiKeyValidator.HeaderName].ToString())}"
            : $"ip:{httpContext.Connection.RemoteIpAddress}";

        var permits = httpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<RelayOptions>>()
            .CurrentValue.RateLimitPermitsPerMinute;

        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

// Reject oversize bodies before they are parsed. Set in both places: Kestrel for local dev,
// IISServerOptions for in-process hosting on the host. Sized from the validation contract, not
// from the current extension: 50 lines x 512 UTF-16 chars is ~150 KB of UTF-8 JSON in the worst
// (non-ASCII) case, and a batch that passes every documented rule must never die here with an
// opaque 413. The extension still keeps its own bodies under 32 KB; this is the server ceiling.
// web.config's maxAllowedContentLength is a coarser backstop set HIGHER than this, because IIS
// request filtering rejects with an opaque 404.13 that a client cannot diagnose.
const long maxRequestBodyBytes = 256 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodyBytes);
builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = maxRequestBodyBytes);

// Everything from Build() onward runs inside a catch: a malformed appsettings.Production.json or
// a bad options value throws here, and without this the process dies before anything reaches the
// Serilog file sink — producing exactly the undiagnosable 500.30 the logging section above exists
// to prevent. HostAbortedException is the test host (WebApplicationFactory) taking over; not a
// failure.
try
{
    var app = builder.Build();

    // -----------------------------------------------------------------------
    // Pipeline
    // -----------------------------------------------------------------------
    app.UseSerilogRequestLogging();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    // Opt-in rather than always-on: see RelayOptions.RequireHttps for why.
    var requireHttps = app.Services
        .GetRequiredService<IOptions<RelayOptions>>()
        .Value.RequireHttps;

    if (requireHttps)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("HTTPS required.");
                return;
            }

            await next();
        });
    }

    // Order matters: rate limiting before authentication, so an unauthenticated flood is capped
    // before we spend anything on it; authentication before routing to endpoints, so untrusted JSON
    // is never deserialised for a caller without a valid key.
    app.UseRateLimiter();
    app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

    // Plain-text root so that a browser hitting the site during the deploy spike gets an
    // unambiguous "the app is running" rather than a 404 that could mean anything.
    app.MapGet("/", () => Results.Text(
        "GalaxyExtender Discord relay. See /api/v1/health", "text/plain"));

    app.MapHealthEndpoints();
    app.MapChatEndpoints();
    app.MapMessagesEndpoints();
    app.MapPresenceEndpoints();

    Log.Information("Relay starting. pid={Pid} env={Environment} contentRoot={ContentRoot}",
        Environment.ProcessId, app.Environment.EnvironmentName, app.Environment.ContentRootPath);

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Relay failed to start or terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Top-level statements generate an internal Program class, which WebApplicationFactory&lt;Program&gt;
/// cannot reference from the test project. Declaring the partial here makes the entry point public.
/// </summary>
public partial class Program;
