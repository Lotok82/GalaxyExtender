using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The live Stage 2 read path (R3-R7) through the real endpoints: fetch-on-poll with cursor
/// semantics, the echo filter, claim/redelivery/drop, and marker acks arriving via /chat.
/// The test app's redelivery timeout is 1 s (production 60 s), so "wait out a claim" is a
/// 1.2 s sleep here.
/// </summary>
public sealed class Stage2EndpointTests
{
    private const string Poll = "/api/v1/messages?client=kaelen";
    private const string PollOther = "/api/v1/messages?client=vex";

    private static readonly TimeSpan ClaimExpiry = TimeSpan.FromMilliseconds(1200);

    private static async Task<(JsonDocument Body, string Header)> PollAsync(
        HttpClient client, string uri = Poll)
    {
        var response = await client.GetAsync(uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()),
            Assert.Single(response.Headers.GetValues("X-Relay-Stage2")));
    }

    private static JsonElement Messages(JsonDocument body) => body.RootElement.GetProperty("messages");

    private static int Dropped(JsonDocument body) => body.RootElement.GetProperty("dropped").GetInt32();

    /// <summary>First poll: cursor initialised from the newest message, history NOT queued.</summary>
    private static async Task InitialiseCursorAsync(Stage2TestApp app, HttpClient client)
    {
        app.Bot.ScriptMessages(DiscordJson.User("100", "Old", "history line"));

        var (body, header) = await PollAsync(client);

        Assert.Equal("enabled", header);
        Assert.Empty(Messages(body).EnumerateArray());
        Assert.Contains("limit=1", app.Bot.RequestUris[^1]);
    }

    [Fact]
    public async Task First_poll_initialises_the_cursor_without_queueing_history()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        // The next fetch reads AFTER the stamped cursor rather than re-reading history.
        var (body, _) = await PollAsync(client);

        Assert.Empty(Messages(body).EnumerateArray());
        Assert.Contains("after=100", app.Bot.RequestUris[^1]);
    }

    [Fact]
    public async Task Fetched_message_reaches_the_poller_with_the_pinned_wire_shape()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "anyone on tonight?"));

        var (body, header) = await PollAsync(client);

        Assert.Equal("enabled", header);

        var message = Assert.Single(Messages(body).EnumerateArray());

        // The C++ extension parses these exact property names.
        Assert.Equal("101", message.GetProperty("id").GetString());
        Assert.Equal("Bob", message.GetProperty("author").GetString());
        Assert.Equal("anyone on tonight?", message.GetProperty("text").GetString());
        Assert.True(message.TryGetProperty("timestampUtc", out _));
        Assert.Equal(0, Dropped(body));
    }

    [Fact]
    public async Task A_claim_hides_the_message_from_other_pollers()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "claimed once"));

        var (first, _) = await PollAsync(client);
        Assert.Single(Messages(first).EnumerateArray());

        var (second, _) = await PollAsync(client, PollOther);
        Assert.Empty(Messages(second).EnumerateArray());
    }

    [Fact]
    public async Task An_unacked_claim_is_redelivered_after_the_timeout()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "redeliver me"));
        var (first, _) = await PollAsync(client);
        Assert.Single(Messages(first).EnumerateArray());

        await Task.Delay(ClaimExpiry);

        var (second, _) = await PollAsync(client, PollOther);
        var redelivered = Assert.Single(Messages(second).EnumerateArray());

        Assert.Equal("101", redelivered.GetProperty("id").GetString());
    }

    [Fact]
    public async Task After_the_delivery_cap_the_message_is_dropped_and_reported_once()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "never acked"));

        for (var delivery = 0; delivery < 3; delivery++)
        {
            var (body, _) = await PollAsync(client);
            Assert.Single(Messages(body).EnumerateArray());
            await Task.Delay(ClaimExpiry);
        }

        var (afterCap, _) = await PollAsync(client);

        Assert.Empty(Messages(afterCap).EnumerateArray());
        Assert.Equal(1, Dropped(afterCap));

        // Report-once: the next poll must not repeat the loss.
        var (next, _) = await PollAsync(client);
        Assert.Equal(0, Dropped(next));
    }

    [Fact]
    public async Task Webhook_and_bot_messages_are_filtered_but_advance_the_cursor()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(
            DiscordJson.Webhook("101"),
            DiscordJson.Bot("102", "beep"),
            DiscordJson.User("103", "Bob", "real person"));

        var (body, _) = await PollAsync(client);

        var message = Assert.Single(Messages(body).EnumerateArray());
        Assert.Equal("103", message.GetProperty("id").GetString());

        // Cursor sits past the filtered ids too — they are never re-examined.
        var (_, _) = await PollAsync(client);
        Assert.Contains("after=103", app.Bot.RequestUris[^1]);
    }

    [Fact]
    public async Task Attachment_only_message_arrives_as_a_marker()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "", attachments: true));

        var (body, _) = await PollAsync(client);

        var message = Assert.Single(Messages(body).EnumerateArray());
        Assert.Equal("[attachment]", message.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Fetched_text_is_sanitized_including_swg_escapes_and_mentions()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User(
            "101", "Bob", "\\#FF0000red <@42> ok",
            mentionsJson: "[{\"id\":\"42\",\"username\":\"zed\",\"global_name\":\"Zed\"}]"));

        var (body, _) = await PollAsync(client);

        var message = Assert.Single(Messages(body).EnumerateArray());
        Assert.Equal("red @Zed ok", message.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Queue_cap_drops_oldest_and_reports_them()
    {
        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Relay:Stage2MaxPending"] = "2"
        });
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(
            DiscordJson.User("101", "Bob", "first"),
            DiscordJson.User("102", "Bob", "second"),
            DiscordJson.User("103", "Bob", "third"));

        var (body, _) = await PollAsync(client);

        var texts = Messages(body).EnumerateArray()
            .Select(m => m.GetProperty("text").GetString())
            .ToList();

        Assert.Equal(["second", "third"], texts);
        Assert.Equal(1, Dropped(body));
    }

    [Fact]
    public async Task Stale_pending_messages_expire_by_ttl()
    {
        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Relay:Stage2TtlSeconds"] = "1"
        });
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "goes stale"));
        var (first, _) = await PollAsync(client);
        Assert.Single(Messages(first).EnumerateArray());

        await Task.Delay(ClaimExpiry);

        var (second, _) = await PollAsync(client);

        Assert.Empty(Messages(second).EnumerateArray());
        Assert.Equal(1, Dropped(second));
    }

    [Fact]
    public async Task Discord_fetch_failure_degrades_to_an_empty_poll()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseCursorAsync(app, client);

        app.Bot.ScriptStatus(HttpStatusCode.InternalServerError);

        var (body, header) = await PollAsync(client);

        Assert.Equal("enabled", header);
        Assert.Empty(Messages(body).EnumerateArray());
    }

    // ------------------------------------------------------------------
    // Marker acks arriving through /chat (R7)
    // ------------------------------------------------------------------

    private static object MarkedBatch(string line) =>
        ChatBatches.WithLines([ChatBatches.Line(line)]);

    private static async Task ClaimOneAsync(
        Stage2TestApp app, HttpClient client, string id, string author, string text)
    {
        await InitialiseCursorAsync(app, client);
        app.Bot.ScriptMessages(DiscordJson.User(id, author, text));

        var (body, _) = await PollAsync(client);
        Assert.Single(Messages(body).EnumerateArray());
    }

    [Fact]
    public async Task Marked_line_acks_the_claim_and_never_reaches_discord()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await ClaimOneAsync(app, client, "101", "Bob", "hi there");

        var response = await client.PostAsJsonAsync("/api/v1/chat",
            MarkedBatch("[GuildChat] Kaelen: [Discord] Bob: hi there"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var chatBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, chatBody.RootElement.GetProperty("accepted").GetInt32());

        // Never forwarded to the webhook…
        Assert.Equal(0, app.Discord.RequestCount);

        // …and the claim is complete: waiting out the timeout produces no redelivery.
        await Task.Delay(ClaimExpiry);
        var (afterAck, _) = await PollAsync(client, PollOther);

        Assert.Empty(Messages(afterAck).EnumerateArray());
        Assert.Equal(0, Dropped(afterAck));
    }

    [Fact]
    public async Task Profanity_masked_ack_still_completes_the_claim()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await ClaimOneAsync(app, client, "101", "Bob", "heck");

        // A receiving client's profanity filter masked the text; same length, stars.
        var response = await client.PostAsJsonAsync("/api/v1/chat",
            MarkedBatch("[GuildChat] Kaelen: [Discord] Bob: ****"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, app.Discord.RequestCount);

        await Task.Delay(ClaimExpiry);
        var (afterAck, _) = await PollAsync(client, PollOther);

        Assert.Empty(Messages(afterAck).EnumerateArray());
        Assert.Equal(0, Dropped(afterAck));
    }

    [Fact]
    public async Task Wrong_length_masked_line_does_not_ack_and_the_claim_redelivers()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await ClaimOneAsync(app, client, "101", "Bob", "heck");

        await client.PostAsJsonAsync("/api/v1/chat",
            MarkedBatch("[GuildChat] Kaelen: [Discord] Bob: *****"));

        await Task.Delay(ClaimExpiry);
        var (redelivered, _) = await PollAsync(client, PollOther);

        Assert.Single(Messages(redelivered).EnumerateArray());
    }

    [Fact]
    public async Task Spoofed_marked_line_is_swallowed_not_forwarded()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/chat",
            MarkedBatch("[GuildChat] Kaelen: [Discord] Bob: never claimed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, app.Discord.RequestCount);
    }

    [Fact]
    public async Task Line_mentioning_the_marker_mid_sentence_forwards_normally()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/chat",
            MarkedBatch("[GuildChat] Kaelen: check this [Discord] thing out"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, app.Discord.RequestCount);
    }

    [Fact]
    public async Task Second_relaying_clients_copy_of_the_ack_is_a_harmless_no_op()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await ClaimOneAsync(app, client, "101", "Bob", "hi there");

        var ackLine = "[GuildChat] Kaelen: [Discord] Bob: hi there";

        await client.PostAsJsonAsync("/api/v1/chat", MarkedBatch(ackLine));
        var second = await client.PostAsJsonAsync("/api/v1/chat", MarkedBatch(ackLine));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(0, app.Discord.RequestCount);
    }
}
