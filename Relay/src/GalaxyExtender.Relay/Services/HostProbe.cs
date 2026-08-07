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
    private static DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// App start. Comparing this across two calls detects an app-pool recycle; if it keeps moving,
    /// the pool is idle-stopping aggressively.
    ///
    /// Stamped explicitly by <see cref="StampStartup"/> rather than by a static initialiser: a
    /// static field initialises lazily on first touch, which is the first request, so uptime would
    /// always read ~0 and the reading would be worthless for its one purpose.
    /// </summary>
    public static DateTimeOffset StartedUtc => _startedUtc;

    /// <summary>Call once at the top of Program.cs, before the host is built.</summary>
    public static void StampStartup() => _startedUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Start time of the OS process, where the host permits reading it. Distinguishes an app
    /// restart (this stays put, <see cref="StartedUtc"/> moves) from a full worker-process
    /// recycle (both move). Null if the hosting environment denies access.
    /// </summary>
    public static DateTimeOffset? ProcessStartedUtc
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                // Restricted shared hosting can deny this; it is a nice-to-have, not a blocker.
                return null;
            }
        }
    }

    /// <summary>
    /// Set during startup if the file log sink could not be initialised. The app deliberately
    /// continues without file logging, so this is the only channel that can report it.
    /// </summary>
    public static string? FileLoggingError { get; set; }

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

            // Unique PER CALL, not per process: two concurrent /health requests sharing one probe
            // path delete each other's file mid-check, and the loser reports App_Data as unwritable
            // — the one field on this document that reads as a hard blocker.
            var probePath = Path.Combine(
                AppDataPath, $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");

            try
            {
                File.WriteAllText(probePath, "probe");
                var readBack = File.ReadAllText(probePath);
                return new AppDataStatus(true, readBack == "probe", null);
            }
            finally
            {
                // Cleanup cannot live on the success path. Unique-per-call means a failure
                // between the write and the delete — an AV or the indexer holding the file
                // across ReadAllText — leaks a NEW file on every /health call rather than
                // reusing one path, so App_Data would grow without bound.
                try
                {
                    File.Delete(probePath);
                }
                catch (Exception)
                {
                }
            }
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
