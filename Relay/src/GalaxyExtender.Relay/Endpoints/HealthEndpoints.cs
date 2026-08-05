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
            HttpContext http) =>
        {
            var now = DateTimeOffset.UtcNow;
            var appData = probe.CheckAppData();

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
                    discordConfigured = discordOptions.Value.IsConfigured
                },

                // Not run automatically — it is an outbound network call. GET /api/v1/health/outbound.
                outboundProbe = "/api/v1/health/outbound"
            });
        });

        // Separate endpoint so the cheap health check stays cheap and we do not hit Discord on
        // every uptime ping.
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
