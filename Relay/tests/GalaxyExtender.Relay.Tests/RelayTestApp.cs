using GalaxyExtender.Relay.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Test host with deterministic configuration. Explicit in-memory settings rather than whatever
/// appsettings.Development.json happens to contain, so the tests do not change meaning when that
/// file does.
///
/// Discord is replaced by <see cref="FakeDiscordHandler"/> (default response 204), and durable
/// state goes to a per-instance temp file so no dedupe/outbox state leaks between test classes.
/// </summary>
public class RelayTestApp : WebApplicationFactory<Program>
{
    public const string ValidKey = "integration-test-key-not-a-secret";
    public const string KeyLabel = "test";

    private readonly string _statePath = Path.Combine(
        Path.GetTempPath(), $"relay-tests-state-{Guid.NewGuid():N}.json");

    public FakeDiscordHandler Discord { get; } = new();

    /// <summary>
    /// The far end of the background ticker's self-ping. Stubbed for every test host, not just the
    /// ones that switch the ping on, so that no test can reach the network by accident.
    /// </summary>
    public FakeSelfPingHandler SelfPing { get; } = new();

    /// <summary>
    /// Per-test settings, applied after the defaults so they win. A protected hook rather than a
    /// constructor parameter because several test classes take this host as an xUnit CLASS FIXTURE,
    /// and xUnit rejects a fixture type with more than one public constructor — see
    /// <see cref="ConfiguredRelayTestApp"/>.
    /// </summary>
    protected virtual Dictionary<string, string?>? ExtraConfiguration => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Relay:ApiKeys:{KeyLabel}"] = ValidKey,
                ["Relay:RequireHttps"] = "false",
                ["Relay:MaxLinesPerBatch"] = "50",
                ["Relay:MaxLineLength"] = "512",
                ["Relay:StateFilePath"] = _statePath,
                // Off unless a test asks for it. A timer firing mid-assertion would make every
                // "how many Discord calls did that request make" test in the suite racy, and the
                // ticker's own behaviour is worth testing deliberately rather than everywhere.
                ["Relay:BackgroundTickSeconds"] = "0",
                // High enough that the suite cannot trip the limiter by accident; the limiter has
                // its own dedicated test that sets it low.
                ["Relay:RateLimitPermitsPerMinute"] = "10000",
                // A plausible-but-fake URL: IsConfigured must pass, and the fake handler answers.
                ["Discord:WebhookUrl"] = "https://discord.test/api/webhooks/1/not-a-real-webhook"
            });

            // Added last so a test's own values win over the defaults above.
            if (ExtraConfiguration is { } extra)
            {
                configuration.AddInMemoryCollection(extra);
            }
        });

        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(DiscordPublisher.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Discord);

            services.AddHttpClient(BackgroundTicker.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => SelfPing);
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Relay-Key", ValidKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                File.Delete(_statePath);
            }
            catch (IOException)
            {
            }
        }
    }
}
