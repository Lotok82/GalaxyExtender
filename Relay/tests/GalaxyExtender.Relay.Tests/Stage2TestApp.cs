using GalaxyExtender.Relay.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// <see cref="RelayTestApp"/> with Stage 2 configured: bot token + channel + enabled flag, the
/// bot REST endpoint replaced by <see cref="FakeDiscordBotHandler"/>, the fetch cache window at
/// zero (every poll fetches — deterministic scripting), and a 1 s redelivery timeout so the
/// redelivery/drop tests run in test-suite time rather than the production 60 s.
/// </summary>
public class Stage2TestApp(Dictionary<string, string?>? overrides = null) : RelayTestApp
{
    public FakeDiscordBotHandler Bot { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discord:BotToken"] = "test-bot-token-not-a-secret",
                ["Discord:ChannelId"] = "111222333444555666",
                ["Discord:Stage2Enabled"] = "true",
                ["Relay:Stage2FetchCacheSeconds"] = "0",
                ["Relay:Stage2RedeliveryTimeoutSeconds"] = "1"
            });

            if (overrides is not null)
            {
                configuration.AddInMemoryCollection(overrides);
            }
        });

        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(DiscordReader.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Bot);
        });
    }
}
