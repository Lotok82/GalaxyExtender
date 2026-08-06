using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace GalaxyExtender.Relay.Tests;

public sealed class RateLimitTests
{
    private const int PermitLimit = 3;

    private sealed class ThrottledApp : RelayTestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Relay:RateLimitPermitsPerMinute"] = PermitLimit.ToString()
                });
            });
        }
    }

    [Fact]
    public async Task Exceeding_the_limit_returns_429_with_retry_after()
    {
        using var app = new ThrottledApp();
        var client = app.CreateAuthenticatedClient();

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid($"line {i}"));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var throttled = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid("one too many"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    /// <summary>
    /// The limiter partitions on the key's fingerprint only once the key has VALIDATED. If it
    /// partitioned on the raw presented header, an attacker rotating random keys would mint a
    /// fresh permit bucket per request and the unauthenticated-flood cap would not exist.
    /// </summary>
    [Fact]
    public async Task Rotating_unrecognised_keys_share_one_partition_and_get_throttled()
    {
        using var app = new ThrottledApp();

        // Every request presents a different bogus key; they must all drain the same
        // per-caller partition.
        for (var i = 0; i < PermitLimit; i++)
        {
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Add("X-Relay-Key", $"bogus-key-{i}");

            var rejected = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var flooder = app.CreateClient();
        flooder.DefaultRequestHeaders.Add("X-Relay-Key", "bogus-key-final");

        var throttled = await flooder.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // The valid key lives in its own partition, so the flood cannot starve a legitimate
        // client out of its permits.
        var legitimate = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
    }

    /// <summary>
    /// The health endpoint must stay reachable when a client is being throttled — it is the
    /// diagnostic channel and an uptime-ping target, so it carries no rate-limit policy.
    /// </summary>
    [Fact]
    public async Task Health_is_not_rate_limited()
    {
        using var app = new ThrottledApp();
        var client = app.CreateAuthenticatedClient();

        for (var i = 0; i < PermitLimit + 2; i++)
        {
            await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid($"line {i}"));
        }

        var health = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
