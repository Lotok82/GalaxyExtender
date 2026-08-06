using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The channel-history cleanup sweep (R10): messages older than 5 h are deleted from the bridge
/// channel on the back of ordinary request traffic, pinned messages survive, the over-14-day
/// tail falls back to capped per-message deletes, and the whole thing is throttled by the
/// durable LastCleanupUtc stamp. Heartbeat is the trigger of convenience here — chat POSTs and
/// Stage 2 polls share the identical one-line hook.
/// </summary>
public sealed class CleanupTests
{
    private static Stage2TestApp CleanupApp(Dictionary<string, string?>? extra = null)
    {
        var overrides = new Dictionary<string, string?> { ["Discord:CleanupEnabled"] = "true" };

        foreach (var (key, value) in extra ?? [])
        {
            overrides[key] = value;
        }

        return new Stage2TestApp(overrides);
    }

    /// <summary>The snowflake a message posted <paramref name="age"/> ago would carry.</summary>
    private static ulong Snowflake(TimeSpan age) =>
        (ulong)(DateTimeOffset.UtcNow.Subtract(age).ToUnixTimeMilliseconds() - 1420070400000) << 22;

    private static string Message(ulong id, bool pinned = false) =>
        $"{{\"id\":\"{id}\",\"pinned\":{(pinned ? "true" : "false")}}}";

    private static async Task HeartbeatAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sweep_bulk_deletes_old_unpinned_messages_and_preserves_pinned()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        var oldA = Snowflake(TimeSpan.FromHours(6));
        var pinned = Snowflake(TimeSpan.FromHours(7));
        var oldB = Snowflake(TimeSpan.FromHours(8));

        app.Bot.ScriptMessages(Message(oldA), Message(pinned, pinned: true), Message(oldB));

        await HeartbeatAsync(client);

        var requests = app.Bot.Requests;
        Assert.Equal(2, requests.Count);

        Assert.Equal("GET", requests[0].Method);
        Assert.Contains("before=", requests[0].Uri);
        Assert.Contains("limit=100", requests[0].Uri);

        Assert.Equal("POST", requests[1].Method);
        Assert.EndsWith("/messages/bulk-delete", requests[1].Uri);

        using var body = JsonDocument.Parse(requests[1].Body!);
        var ids = body.RootElement.GetProperty("messages").EnumerateArray()
            .Select(id => id.GetString()).ToList();

        Assert.Equal([oldA.ToString(), oldB.ToString()], ids);
    }

    [Fact]
    public async Task Sweep_runs_once_per_interval()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        await HeartbeatAsync(client);
        Assert.Equal(1, app.Bot.RequestCount);   // the page fetch; nothing to delete

        await HeartbeatAsync(client);
        Assert.Equal(1, app.Bot.RequestCount);   // inside the 15 min interval: no new calls
    }

    [Fact]
    public async Task A_lone_candidate_is_deleted_individually_because_bulk_needs_two()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        var lone = Snowflake(TimeSpan.FromHours(6));
        app.Bot.ScriptMessages(Message(lone));

        await HeartbeatAsync(client);

        var delete = Assert.Single(app.Bot.Requests, r => r.Method == "DELETE");
        Assert.EndsWith($"/messages/{lone}", delete.Uri);
        Assert.DoesNotContain(app.Bot.Requests, r => r.Uri.EndsWith("/bulk-delete"));
    }

    [Fact]
    public async Task The_over_14_day_tail_uses_capped_single_deletes_alongside_the_bulk()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        var young = new[] { Snowflake(TimeSpan.FromHours(6)), Snowflake(TimeSpan.FromHours(7)) };
        var ancient = Enumerable.Range(0, 8)
            .Select(days => Snowflake(TimeSpan.FromDays(20 + days))).ToArray();

        app.Bot.ScriptMessages(young.Concat(ancient).Select(id => Message(id)).ToArray());

        await HeartbeatAsync(client);

        var bulk = Assert.Single(app.Bot.Requests, r => r.Uri.EndsWith("/bulk-delete"));

        using var body = JsonDocument.Parse(bulk.Body!);
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());

        // Default CleanupMaxSingleDeletesPerSweep: the ninth-plus ancient ids wait for the
        // next sweep rather than this request paying for them all.
        Assert.Equal(5, app.Bot.Requests.Count(r => r.Method == "DELETE"));
    }

    [Fact]
    public async Task Cleanup_left_disabled_never_touches_discord()
    {
        // Stage2TestApp has the bot fully credentialed, but CleanupEnabled keeps its
        // default: off. Deleting history must be an explicit config decision.
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await HeartbeatAsync(client);

        Assert.Equal(0, app.Bot.RequestCount);
    }

    [Fact]
    public async Task A_failed_page_fetch_consumes_the_interval_quietly()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptStatus(HttpStatusCode.TooManyRequests);

        await HeartbeatAsync(client);            // still 200 to the caller
        Assert.Equal(1, app.Bot.RequestCount);

        await HeartbeatAsync(client);            // and no retry storm inside the interval
        Assert.Equal(1, app.Bot.RequestCount);
    }

    [Fact]
    public async Task Fresh_messages_are_never_deleted_even_if_the_response_carries_them()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        // ?before= should make this impossible; the defensive guard is what's under test.
        app.Bot.ScriptMessages(Message(Snowflake(TimeSpan.FromMinutes(1))));

        await HeartbeatAsync(client);

        Assert.Equal(1, app.Bot.RequestCount);   // the fetch only — no delete calls
    }

    [Fact]
    public async Task A_stage2_poll_sweeps_before_its_discord_fetch()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/messages?client=kaelen");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var requests = app.Bot.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Contains("before=", requests[0].Uri);   // cleanup page fetch
        Assert.Contains("limit=1", requests[1].Uri);   // reader's first-run cursor stamp
    }

    [Fact]
    public async Task A_chat_batch_sweeps_too()
    {
        using var app = CleanupApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/chat",
            ChatBatches.WithLines([ChatBatches.Line("[GuildChat] Kaelen: evening all")]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sweep = Assert.Single(app.Bot.Requests);
        Assert.Contains("before=", sweep.Uri);
    }
}
