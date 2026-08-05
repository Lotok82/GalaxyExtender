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

// Rate limiting runs before authentication in the pipeline, so an unauthenticated flood is capped
// too. Partitioned on a hash of the presented key — never the key itself — falling back to the
// remote address when no key is supplied.
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
        var presented = httpContext.Request.Headers[ApiKeyValidator.HeaderName].ToString();

        var partition = string.IsNullOrEmpty(presented)
            ? $"ip:{httpContext.Connection.RemoteIpAddress}"
            : $"key:{ApiKeyValidator.Fingerprint(presented)}";

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
// IISServerOptions for in-process hosting on the host (web.config carries the IIS-level limit).
const long maxRequestBodyBytes = 32 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodyBytes);
builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = maxRequestBodyBytes);

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
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

Log.Information("Relay starting. pid={Pid} env={Environment} contentRoot={ContentRoot}",
    Environment.ProcessId, app.Environment.EnvironmentName, app.Environment.ContentRootPath);

app.Run();

/// <summary>
/// Top-level statements generate an internal Program class, which WebApplicationFactory&lt;Program&gt;
/// cannot reference from the test project. Declaring the partial here makes the entry point public.
/// </summary>
public partial class Program;
