using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Phases 2-4 behaviour: de-duplication, batch idempotency, Discord publishing with the
/// allowed_mentions lockdown, and the durable outbox. Each test gets its own app instance —
/// dedupe state and the recorded webhook requests must not leak between tests.
/// </summary>
public sealed class ForwardingTests
{
    private static object Batch(string clientId, params object[] lines) => new
    {
        batchId = Guid.NewGuid().ToString(),
        client = new { id = clientId, character = clientId, galaxy = "Basilisk" },
        lines
    };

    [Fact]
    public async Task Two_clients_posting_the_same_line_produce_one_discord_post()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var first = await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[GuildChat] Kaelen: krayt run?")));
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, firstBody.GetProperty("accepted").GetInt32());
        Assert.Equal(0, firstBody.GetProperty("deduped").GetInt32());

        var second = await client.PostAsJsonAsync("/api/v1/chat",
            Batch("tarn", ChatBatches.Line("[GuildChat] Kaelen: krayt run?")));
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, secondBody.GetProperty("accepted").GetInt32());
        Assert.Equal(1, secondBody.GetProperty("deduped").GetInt32());

        Assert.Equal(1, app.Discord.RequestCount);
    }

    /// <summary>The case a naive time-window dedupe breaks: a genuine repeat inside the window.</summary>
    [Fact]
    public async Task Genuine_repeat_with_higher_occurrence_is_posted_twice()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("lol", occurrence: 1)));
        var second = await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("lol", occurrence: 2)));
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("deduped").GetInt32());
        Assert.Equal(2, app.Discord.RequestCount);
    }

    [Fact]
    public async Task Retried_batch_id_replays_the_response_and_does_not_post_again()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var payload = Batch("kaelen", ChatBatches.Line("only once"));

        var first = await client.PostAsJsonAsync("/api/v1/chat", payload);
        var retry = await client.PostAsJsonAsync("/api/v1/chat", payload);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, app.Discord.RequestCount);
    }

    [Fact]
    public async Task Discord_rate_limit_parks_lines_in_outbox_and_a_later_request_delivers_them()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        // Two scripted 429s: the first is short enough for the bounded in-request retry, the
        // second exhausts it, so the payload must be parked rather than lost.
        app.Discord.ScriptRateLimit(0.05);
        app.Discord.ScriptRateLimit(0.05);

        var response = await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("parked line")));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("queued").GetInt32());
        Assert.True(body.GetProperty("retryAfterMs").GetInt32() > 0);

        // Wait out the parked entry's notBefore, then let a heartbeat drain it (unscripted
        // requests get 204 from the fake).
        await Task.Delay(300);

        var heartbeat = await client.PostAsync("/api/v1/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        var heartbeatBody = await heartbeat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, heartbeatBody.GetProperty("outbox").GetInt32());

        var delivered = app.Discord.RequestBodies[^1];
        Assert.Contains("parked line", delivered);
    }

    [Fact]
    public async Task Webhook_payload_locks_down_mentions_and_escapes_markdown()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("hey @everyone check `this` *out*")));

        var body = Assert.Single(app.Discord.RequestBodies);
        using var document = JsonDocument.Parse(body);

        // The hard guarantee: even unsanitised text could not ping anyone.
        var mentions = document.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(0, mentions.GetArrayLength());

        var description = document.RootElement.GetProperty("embeds")[0]
            .GetProperty("description").GetString()!;

        // Ordinal on purpose: culture-aware comparison treats the zero-width joiner as
        // ignorable and would "find" @everyone even though the ping is neutralised.
        Assert.DoesNotContain("@everyone", description, StringComparison.Ordinal);
        Assert.Contains("@\u200Deveryone", description, StringComparison.Ordinal);
        Assert.Contains("\\`this\\`", description);
        Assert.Contains("\\*out\\*", description);
    }

    [Fact]
    public async Task Heartbeat_requires_a_key()
    {
        using var app = new RelayTestApp();

        var response = await app.CreateClient().PostAsync("/api/v1/heartbeat", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_forwarding_state()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/v1/chat", Batch("kaelen", ChatBatches.Line("for health")));

        var health = await client.GetFromJsonAsync<JsonElement>("/api/v1/health");
        var relay = health.GetProperty("relay");

        Assert.Equal(0, relay.GetProperty("outboxDepth").GetInt32());
        Assert.Equal(1, relay.GetProperty("dedupeEntries").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, relay.GetProperty("lastForwardUtc").ValueKind);
    }

    private sealed class UnconfiguredApp : RelayTestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Discord:WebhookUrl"] = string.Empty
                });
            });
        }
    }

    /// <summary>
    /// Contract: 503 when the webhook is not configured — and no state mutation, so nothing is
    /// eaten into a dedupe window that will never forward.
    /// </summary>
    [Fact]
    public async Task Unconfigured_webhook_returns_503_and_posts_nothing()
    {
        using var app = new UnconfiguredApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("goes nowhere")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, app.Discord.RequestCount);
    }
}
