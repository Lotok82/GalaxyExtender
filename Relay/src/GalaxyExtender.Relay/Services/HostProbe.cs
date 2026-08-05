using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Answers "what is this host actually doing to us?" — the questions that decide whether the
/// request-driven design in discord-relay-plan.md holds on IIS shared hosting.
///
/// Deliberately part of the product rather than a one-off script: app-pool behaviour can change
/// under us at any time (host reconfiguration, plan change), and these readings are the only way
/// to notice. See <c>ProcessId</c> and <c>StartedUtc</c> in particular.
/// </summary>
public sealed class HostProbe(IHttpClientFactory httpClientFactory, IHostEnvironment environment)
{
    /// <summary>
    /// Captured once per process load. Comparing this across two calls detects an app-pool
    /// recycle; if it keeps moving, the pool is idle-stopping aggressively.
    /// </summary>
    public static readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Differing process ids across successive calls mean a web garden (maxProcesses > 1),
    /// which is what forces the cross-process mutex on the state store. If this is stable,
    /// the state store can be simplified.
    /// </summary>
    public static int ProcessId => Environment.ProcessId;

    public string ContentRoot => environment.ContentRootPath;

    public string AppDataPath => Path.Combine(environment.ContentRootPath, "App_Data");

    public string Framework => RuntimeInformation.FrameworkDescription;

    public string OperatingSystem => RuntimeInformation.OSDescription;

    /// <summary>
    /// Verifies App_Data is genuinely writable by the app-pool identity. The state store and the
    /// outbox both depend on this; a read-only App_Data would silently break de-duplication and
    /// lose parked Discord posts.
    /// </summary>
    public AppDataStatus CheckAppData()
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            var probePath = Path.Combine(AppDataPath, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probePath, "probe");
            var readBack = File.ReadAllText(probePath);
            File.Delete(probePath);
            return new AppDataStatus(true, readBack == "probe", null);
        }
        catch (Exception ex)
        {
            return new AppDataStatus(false, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Confirms the app pool is allowed to make outbound HTTPS calls to Discord. Uses the
    /// unauthenticated gateway endpoint, so it proves connectivity and TLS without a credential.
    /// </summary>
    public async Task<OutboundStatus> CheckDiscordReachableAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = httpClientFactory.CreateClient("discord-probe");
            using var response = await client.GetAsync("https://discord.com/api/v10/gateway", cancellationToken);
            stopwatch.Stop();
            return new OutboundStatus(response.IsSuccessStatusCode, (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new OutboundStatus(false, null, stopwatch.ElapsedMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public sealed record AppDataStatus(bool Writable, bool ReadBackOk, string? Error);

    public sealed record OutboundStatus(bool Reachable, int? StatusCode, long ElapsedMs, string? Error);
}
