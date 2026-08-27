using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The bridge bot answering <c>@GalaxyExtender status</c> (R11). There is no gateway on this host,
/// so "the bot listens" means the relay reads the channel on the back of request traffic — the
/// heartbeat included, which is what makes the question answerable when nobody is online.
/// </summary>
public sealed class BotCommandTests
{
    private const string BotUserId = "424242";

    /// <summary>
    /// Commands on, bot identity pinned (so tests do not pay a <c>users/@me</c> read), and no scan
    /// interval so every request scans. Cleanup stays off: its Discord calls would interleave with
    /// the ones under test here.
    /// </summary>
    private static Stage2TestApp CommandApp(Dictionary<string, string?>? extra = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Discord:CommandsEnabled"] = "true",
            ["Discord:BotUserId"] = BotUserId,
            ["Relay:CommandScanIntervalSeconds"] = "0"
        };

        foreach (var (key, value) in extra ?? [])
        {
            overrides[key] = value;
        }

        return new Stage2TestApp(overrides);
    }

    private static async Task HeartbeatAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PresenceAsync(
        HttpClient client, string id, string? character = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/presence",
            new PresenceRequest { Client = new ChatClient { Id = id, Character = character } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The content of the reply the bot posted, or null if it posted nothing.</summary>
    private static string? PostedReply(Stage2TestApp app)
    {
        var post = app.Bot.Requests.LastOrDefault(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal));

        if (post?.Body is null)
        {
            return null;
        }

        using var body = JsonDocument.Parse(post.Body);
        return body.RootElement.GetProperty("content").GetString();
    }

    /// <summary>
    /// Consumes the first-run scan, which stamps the cursor at the channel's newest message and
    /// deliberately answers nothing. Every reply test starts from here.
    /// </summary>
    private static async Task StampCursorAsync(Stage2TestApp app, HttpClient client)
    {
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        await HeartbeatAsync(client);

        Assert.Equal(1, app.Bot.RequestCount);
        Assert.Contains("limit=1", app.Bot.RequestUris[0]);
        Assert.Null(PostedReply(app));
    }

    [Fact]
    public async Task Status_reports_the_clients_that_are_online()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);
        await PresenceAsync(client, "kaelen-pc");
        await PresenceAsync(client, "tarn-pc");

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("online", reply);
        Assert.Contains("2 clients connected", reply);
    }

    [Fact]
    public async Task Status_reports_when_the_last_world_boss_alert_was_sent()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:AlertsEnabled"] = "true"
        });

        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        // An alert passes through the relay; the age below is minutes at most, so hours read 0.
        var chat = await client.PostAsJsonAsync("/api/v1/chat", new
        {
            batchId = Guid.NewGuid().ToString(),
            client = new { id = "kaelen-pc", character = "Kaelen", galaxy = "Basilisk" },
            lines = new[] { ChatBatches.Line("[PvP World Boss] Bloodfin has spawned!") }
        });

        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("Last World Boss Alert: 0 hours and 00 minutes ago.", reply);
    }

    [Fact]
    public async Task Status_stays_silent_about_alerts_when_none_has_ever_been_sent()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:AlertsEnabled"] = "true"
        });

        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.DoesNotContain("Last World Boss Alert", reply);
    }

    [Fact]
    public async Task Status_says_offline_when_nobody_has_checked_in()
    {
        // The case the command exists for: nobody is in game, so no player traffic reaches the
        // relay at all and only the heartbeat can carry the scan.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("offline", reply);
        Assert.Contains("has ever checked in", reply);
    }

    [Fact]
    public async Task Status_says_offline_but_names_the_installed_count_once_clients_are_known()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Relay:PresenceOnlineWindowSeconds"] = "0"
        });

        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);
        await PresenceAsync(client, "kaelen-pc");
        await PresenceAsync(client, "tarn-pc");

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("offline", reply);
        Assert.Contains("2 clients seen recently", reply);
        Assert.Contains("last seen", reply);
    }

    [Fact]
    public async Task The_reply_quotes_the_command_and_can_never_ping_anyone()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var post = Assert.Single(app.Bot.Requests, r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(post.Body!);

        Assert.Empty(body.RootElement.GetProperty("allowed_mentions").GetProperty("parse")
            .EnumerateArray());

        var reference = body.RootElement.GetProperty("message_reference");
        Assert.Equal("200", reference.GetProperty("message_id").GetString());
        Assert.False(reference.GetProperty("fail_if_not_exists").GetBoolean());
    }

    [Fact]
    public async Task Nothing_a_client_reports_about_itself_reaches_the_reply()
    {
        // The reply is a count, deliberately: the labels come from an ini file on someone else's
        // machine, and this is a message the relay's own bot authors. Reporting a number is what
        // makes "could a client id smuggle markdown or a mass-ping in here?" unanswerable.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);
        await PresenceAsync(client, "spoofer-pc", "**@everyone** [x](http://evil)");

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app)!;

        Assert.Contains("1 client connected", reply);
        Assert.DoesNotContain("everyone", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("spoofer-pc", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bare_mention_gets_the_help_line()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", string.Empty, BotUserId));
        await HeartbeatAsync(client);

        Assert.Contains("Mention me followed by `status`", PostedReply(app));
    }

    [Fact]
    public async Task A_mention_carrying_no_command_is_answered_by_the_eight_ball()
    {
        // Any mention that is not a real command is a question for the magic eight ball, answered
        // from the fixed pool. A client is online so the delivery notice cannot fire — this is
        // about the command path only.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);
        await PresenceAsync(client, "kaelen-pc");

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "will we win tonight?", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains(reply, EightBall.Phrases);
    }

    [Fact]
    public async Task An_eight_ball_question_gets_a_fortune_not_a_delivery_notice_when_offline()
    {
        // Before the eight ball, an unrecognised mention was ordinary guild-bound chat and earned
        // a delivery notice when undeliverable. Now every mention is bot conversation: answered in
        // Discord, never guild-bound, so there is no delivery to apologise for.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "is a good bot", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains(reply, EightBall.Phrases);
        Assert.DoesNotContain("waiting, not lost", reply);
    }

    [Fact]
    public async Task An_eight_ball_exchange_is_injected_into_the_game_while_somebody_is_online()
    {
        // The guild room sees both halves of the conversation — the question (with the mention
        // resolved to @BotName) and then the answer — queued by the scan itself, since the reader
        // suppresses the mention and the echo filter drops the bot's reply.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        // First poll: the command scan stamps its cursor, then the Stage 2 reader stamps its own.
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));

        var first = await client.GetAsync("/api/v1/messages?client=kaelen");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second poll: kaelen is online (the poll itself checks in before the scan runs), the
        // scan answers and queues the exchange, the reader suppresses the same mention, and the
        // claim at the end of the very same poll hands both halves out — question first.
        var mention = DiscordJson.Mention("200", "Bob", "will we win tonight?", BotUserId);
        app.Bot.ScriptMessages(mention);
        app.Bot.ScriptMessages(mention);

        var second = await client.GetAsync("/api/v1/messages?client=kaelen");
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        var reply = PostedReply(app);
        Assert.Contains(reply, EightBall.Phrases);

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("Bob", messages[0].GetProperty("author").GetString());
        Assert.Equal("@GalaxyExtender will we win tonight?", messages[0].GetProperty("text").GetString());
        Assert.Equal("Magic 8-Ball", messages[1].GetProperty("author").GetString());
        Assert.Equal(
            Stage2Sanitizer.SanitizeText(reply!, new Dictionary<string, string>(), false, false, false),
            messages[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_eight_ball_exchange_is_not_queued_while_nobody_is_online()
    {
        // Offline, the fortune still answers in Discord, but nothing is queued for later: a
        // fortune injected into the guild room hours after it was asked is noise, not
        // conversation. (Ordinary chat's "waiting, not lost" promise is unaffected — the
        // exchange was never guild-bound.)
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "should I buy it?", BotUserId));
        await HeartbeatAsync(client);

        Assert.Contains(PostedReply(app), EightBall.Phrases);

        // The first client to come online afterwards gets nothing: the exchange was never queued.
        var poll = await client.GetAsync("/api/v1/messages?client=kaelen");
        using var body = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());

        Assert.Empty(body.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task A_status_message_that_does_not_mention_the_bot_is_ignored()
    {
        // Somebody online, so the only thing that could produce a reply here is the command path
        // mistaking plain text for a mention.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);
        await PresenceAsync(client, "kaelen-pc");

        app.Bot.ScriptMessages(DiscordJson.User("200", "Bob", "status", timestamp: DateTimeOffset.UtcNow));
        await HeartbeatAsync(client);

        Assert.Null(PostedReply(app));
    }

    [Fact]
    public async Task Existing_mentions_are_never_answered_on_the_first_scan()
    {
        // Switching the feature on must not make the bot reply to everything already in the channel.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptMessages(DiscordJson.Mention("100", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        Assert.Null(PostedReply(app));
    }

    [Fact]
    public async Task A_stale_command_gets_no_reply()
    {
        // After a recycle or a quiet night, a status line about a moment that has passed is worse
        // than no line at all.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId,
            timestamp: DateTimeOffset.UtcNow.AddHours(-2)));

        await HeartbeatAsync(client);

        Assert.Null(PostedReply(app));
    }

    [Fact]
    public async Task A_burst_of_commands_is_capped_per_scan()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(Enumerable.Range(200, 6)
            .Select(id => DiscordJson.Mention(id.ToString(), "Bob", "status", BotUserId))
            .ToArray());

        await HeartbeatAsync(client);

        // Default CommandMaxRepliesPerScan; the rest are dropped, not deferred — the cursor has
        // already moved past them, so nobody gets an answer to yesterday's question twice.
        Assert.Equal(3, app.Bot.Requests.Count(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_answered_command_is_read_once_and_never_again()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var readUris = app.Bot.RequestUris.Where(uri => uri.Contains("after=")).ToList();
        Assert.Single(readUris);
        Assert.Contains("after=100", readUris[0]);

        // The cursor advanced past it, so a second scan asks about later messages only.
        await HeartbeatAsync(client);

        Assert.Contains("after=200", app.Bot.RequestUris.Last(uri => uri.Contains("after=")));
        Assert.Equal(1, app.Bot.Requests.Count(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_scan_runs_at_most_once_per_interval()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Relay:CommandScanIntervalSeconds"] = "900"
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        await HeartbeatAsync(client);
        Assert.Equal(1, app.Bot.RequestCount);

        await HeartbeatAsync(client);
        Assert.Equal(1, app.Bot.RequestCount);
    }

    [Fact]
    public async Task Commands_left_disabled_never_touch_discord()
    {
        // Stage2TestApp has the bot fully credentialed; CommandsEnabled keeps its default of off.
        // The relay must not start posting messages of its own authorship because of a redeploy.
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await HeartbeatAsync(client);

        Assert.Equal(0, app.Bot.RequestCount);
    }

    [Fact]
    public async Task Status_answers_while_the_stage2_read_path_is_switched_off()
    {
        // Asking whether the bridge works is most useful when it does not, so the command path is
        // deliberately independent of Stage2Enabled.
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:Stage2Enabled"] = "false"
        });

        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("Discord → game delivery is switched off", reply);
    }

    [Fact]
    public async Task The_bot_user_id_is_discovered_once_and_cached()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:BotUserId"] = null   // force discovery
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptBody($"{{\"id\":\"{BotUserId}\",\"username\":\"GalaxyExtender\"}}");
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        await HeartbeatAsync(client);

        Assert.Contains("users/@me", app.Bot.RequestUris[0]);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);

        // Discovered once: the second scan goes straight to the channel read.
        Assert.Single(app.Bot.RequestUris, uri => uri.Contains("users/@me"));
        Assert.NotNull(PostedReply(app));
    }

    [Fact]
    public async Task A_failed_channel_read_consumes_the_interval_quietly()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Relay:CommandScanIntervalSeconds"] = "900"
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptStatus(HttpStatusCode.Unauthorized);

        await HeartbeatAsync(client);          // still 200 to the caller
        Assert.Equal(1, app.Bot.RequestCount);

        await HeartbeatAsync(client);          // and no retry storm inside the interval
        Assert.Equal(1, app.Bot.RequestCount);
    }

    [Fact]
    public async Task Ordinary_chat_with_nobody_online_is_told_it_is_waiting_not_lost()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("200", "Bob", "anyone up for a Krayt run?",
            timestamp: DateTimeOffset.UtcNow));

        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("offline", reply);
        Assert.Contains("waiting, not lost", reply);
        Assert.Contains("first client to come online", reply);
    }

    [Fact]
    public async Task Ordinary_chat_is_told_plainly_when_the_read_path_is_off()
    {
        // Not "later" — never. Nothing is queued anywhere, and saying otherwise would be a lie.
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:Stage2Enabled"] = "false"
        });

        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("200", "Bob", "evening all",
            timestamp: DateTimeOffset.UtcNow));

        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("will not appear in the guild room, now or later", reply);
        Assert.DoesNotContain("waiting", reply);
    }

    [Fact]
    public async Task The_notice_names_the_tidy_up_deadline_when_the_sweep_is_on()
    {
        // The one thing that can still lose a waiting message, so the promise is qualified.
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:CleanupEnabled"] = "true"
        });

        var client = app.CreateAuthenticatedClient();

        // Cleanup shares the request, so its page fetch precedes each scan read.
        app.Bot.ScriptMessages();                                        // cleanup sweep: nothing old
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello")); // scan: cursor stamp
        await HeartbeatAsync(client);

        app.Bot.ScriptMessages(DiscordJson.User("200", "Bob", "anyone about?",
            timestamp: DateTimeOffset.UtcNow));

        await HeartbeatAsync(client);

        var reply = PostedReply(app);

        Assert.NotNull(reply);
        Assert.Contains("waiting, not lost", reply);
        Assert.Contains("within about 5 h", reply);
        Assert.Contains("removes it undelivered", reply);
    }

    [Fact]
    public async Task Ordinary_chat_is_not_annotated_while_somebody_is_online()
    {
        // The common case by far: the bot must not narrate traffic that is being delivered.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);
        await PresenceAsync(client, "kaelen-pc");

        app.Bot.ScriptMessages(DiscordJson.User("200", "Bob", "evening all",
            timestamp: DateTimeOffset.UtcNow));

        await HeartbeatAsync(client);

        Assert.Null(PostedReply(app));
    }

    [Fact]
    public async Task A_conversation_held_while_offline_gets_one_notice_not_one_per_line()
    {
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(Enumerable.Range(200, 5)
            .Select(id => DiscordJson.User(id.ToString(), "Bob", $"line {id}",
                timestamp: DateTimeOffset.UtcNow))
            .ToArray());

        await HeartbeatAsync(client);

        Assert.Equal(1, app.Bot.Requests.Count(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal)));

        // And the quiet holds across scans, because the stamp is durable.
        app.Bot.ScriptMessages(DiscordJson.User("300", "Bob", "still nobody?",
            timestamp: DateTimeOffset.UtcNow));

        await HeartbeatAsync(client);

        Assert.Equal(1, app.Bot.Requests.Count(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_notice_interval_is_configurable_and_zero_means_every_message()
    {
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Relay:DeliveryNoticeIntervalMinutes"] = "0"
        });

        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(
            DiscordJson.User("200", "Bob", "one", timestamp: DateTimeOffset.UtcNow),
            DiscordJson.User("201", "Bob", "two", timestamp: DateTimeOffset.UtcNow));

        await HeartbeatAsync(client);

        Assert.Equal(2, app.Bot.Requests.Count(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_status_command_does_not_consume_the_notice_interval()
    {
        // Asking for status and being told about an undelivered message are different events; one
        // must not silence the other.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        await HeartbeatAsync(client);
        Assert.Contains("offline", PostedReply(app));

        app.Bot.ScriptMessages(DiscordJson.User("201", "Bob", "anyone about?",
            timestamp: DateTimeOffset.UtcNow));
        await HeartbeatAsync(client);

        Assert.Contains("waiting, not lost", PostedReply(app));
    }

    [Fact]
    public async Task A_stale_backlog_is_not_answered_with_a_pile_of_notices()
    {
        // After a recycle or an overnight gap the scan can see hours-old chat. Nobody is still
        // waiting on an answer to it.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(Enumerable.Range(200, 3)
            .Select(id => DiscordJson.User(id.ToString(), "Bob", $"old line {id}",
                timestamp: DateTimeOffset.UtcNow.AddHours(-3)))
            .ToArray());

        await HeartbeatAsync(client);

        Assert.Null(PostedReply(app));
    }

    [Fact]
    public async Task A_command_is_answered_in_discord_and_never_injected_into_the_game()
    {
        // Half a conversation with a bot has no business appearing in the guild room.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        // First poll: the command scan stamps its cursor, then the Stage 2 reader stamps its own.
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));

        var first = await client.GetAsync("/api/v1/messages?client=kaelen");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second poll: the same mention reaches both paths.
        var mention = DiscordJson.Mention("200", "Bob", "status", BotUserId);
        app.Bot.ScriptMessages(mention);
        app.Bot.ScriptMessages(mention);

        var second = await client.GetAsync("/api/v1/messages?client=kaelen");
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.NotNull(PostedReply(app));                                    // answered in Discord
        Assert.Empty(body.RootElement.GetProperty("messages").EnumerateArray()); // not sent in game

        // An ordinary message on the same path still arrives, so the skip is the command, not the
        // channel.
        var chat = DiscordJson.User("300", "Bob", "anyone on tonight?");
        app.Bot.ScriptMessages(chat);
        app.Bot.ScriptMessages(chat);

        var third = await client.GetAsync("/api/v1/messages?client=kaelen");
        using var delivered = JsonDocument.Parse(await third.Content.ReadAsStringAsync());

        var message = Assert.Single(delivered.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("anyone on tonight?", message.GetProperty("text").GetString());
    }

    [Fact]
    public async Task With_commands_off_a_mention_reaches_the_guild_room_like_any_other_chat()
    {
        // The reader suppresses commands because the bot answers them in Discord instead. With the
        // command path off nothing answers, so suppressing as well would swallow the message
        // entirely — and the discovered bot id is durable, so it outlives the switch going back off.
        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Discord:BotUserId"] = BotUserId   // known, but CommandsEnabled stays off
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        var first = await client.GetAsync("/api/v1/messages?client=kaelen");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        app.Bot.ScriptMessages(DiscordJson.Mention("200", "Bob", "status", BotUserId));
        var second = await client.GetAsync("/api/v1/messages?client=kaelen");

        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.Null(PostedReply(app));
        var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Contains("status", message.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_blank_configured_bot_id_does_not_get_a_command_both_answered_and_injected()
    {
        // The scanner falls through a blank override to discovery and answers; the reader has to
        // resolve the SAME identity or it suppresses nothing, and the command is answered in
        // Discord and posted into the guild room.
        using var app = CommandApp(new Dictionary<string, string?>
        {
            ["Discord:BotUserId"] = ""
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptBody($"{{\"id\":\"{BotUserId}\"}}");                // discovery
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello")); // scan cursor
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello")); // reader cursor

        var first = await client.GetAsync("/api/v1/messages?client=kaelen");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var mention = DiscordJson.Mention("200", "Bob", "status", BotUserId);
        app.Bot.ScriptMessages(mention);
        app.Bot.ScriptMessages(mention);

        var second = await client.GetAsync("/api/v1/messages?client=kaelen");
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.NotNull(PostedReply(app));
        Assert.Empty(body.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task The_reply_cap_bounds_attempts_so_a_failing_discord_is_not_a_post_storm()
    {
        // A 403 on the channel fails every reply identically. Counting successes would let one
        // scan try the whole fetched page — up to 50 POSTs — and repeat that every interval.
        using var app = CommandApp();
        var client = app.CreateAuthenticatedClient();

        await StampCursorAsync(app, client);

        app.Bot.ScriptMessages(Enumerable.Range(200, 6)
            .Select(id => DiscordJson.Mention(id.ToString(), "Bob", "status", BotUserId))
            .ToArray());

        for (var i = 0; i < 6; i++)
        {
            app.Bot.ScriptStatus(HttpStatusCode.Forbidden);
        }

        await HeartbeatAsync(client);

        Assert.Equal(3, app.Bot.Requests.Count(r =>
            r.Method == "POST" && r.Uri.EndsWith("/messages", StringComparison.Ordinal)));
    }
}
