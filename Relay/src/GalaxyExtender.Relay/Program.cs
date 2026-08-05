using GalaxyExtender.Relay.Endpoints;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.AspNetCore.Builder;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging. Console output is invisible under IIS, so the file sink is the only
// way to see anything on the host. shared: true because a web garden would have
// two worker processes writing the same file.
// ---------------------------------------------------------------------------
var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("pid", Environment.ProcessId)
    .WriteTo.Console();

try
{
    var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "logs");
    Directory.CreateDirectory(logDirectory);
    loggerConfiguration.WriteTo.File(
        Path.Combine(logDirectory, "relay-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true);
}
catch (Exception ex)
{
    // A read-only App_Data must not stop the app from starting — /api/v1/health is what
    // reports the problem, and it cannot report anything if we throw here.
    Console.Error.WriteLine($"File logging disabled, App_Data not writable: {ex.Message}");
}

Log.Logger = loggerConfiguration.CreateLogger();
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
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RelayOptions>>()
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

// Plain-text root so that a browser hitting the site during the deploy spike gets an
// unambiguous "the app is running" rather than a 404 that could mean anything.
app.MapGet("/", () => Results.Text(
    "GalaxyExtender Discord relay. See /api/v1/health", "text/plain"));

app.MapHealthEndpoints();

Log.Information("Relay starting. pid={Pid} env={Environment} contentRoot={ContentRoot}",
    Environment.ProcessId, app.Environment.EnvironmentName, app.Environment.ContentRootPath);

app.Run();

/// <summary>
/// Top-level statements generate an internal Program class, which WebApplicationFactory&lt;Program&gt;
/// cannot reference from the test project. Declaring the partial here makes the entry point public.
/// </summary>
public partial class Program;
