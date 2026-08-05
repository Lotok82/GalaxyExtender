using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Test host with deterministic configuration. Explicit in-memory settings rather than whatever
/// appsettings.Development.json happens to contain, so the tests do not change meaning when that
/// file does.
/// </summary>
public class RelayTestApp : WebApplicationFactory<Program>
{
    public const string ValidKey = "integration-test-key-not-a-secret";
    public const string KeyLabel = "test";

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
                // High enough that the suite cannot trip the limiter by accident; the limiter has
                // its own dedicated test that sets it low.
                ["Relay:RateLimitPermitsPerMinute"] = "10000",
                ["Discord:WebhookUrl"] = string.Empty
            });
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Relay-Key", ValidKey);
        return client;
    }
}
