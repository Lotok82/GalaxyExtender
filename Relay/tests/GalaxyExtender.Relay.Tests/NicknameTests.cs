using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Server nicknames on injected lines: the guild recognises people by the name they set IN the
/// Discord server, which is not the name the message payload carries, so the relay reads it
/// separately. These tests are as much about what happens when that read does NOT work — every
/// failure has to land on the account name and let the line through, because a chat bridge that
/// drops messages over a cosmetic lookup is worse than one showing the wrong name.
///
/// <see cref="DiscordJson.User"/> gives message <c>101</c> the author id <c>9101</c>; that is the
/// id the member lookups here are scripted against.
/// </summary>
public sealed class NicknameTests
{
    private const string Poll = "/api/v1/messages?client=kaelen";
    private const string GuildId = "555000111";

    private static async Task<JsonDocument> PollAsync(HttpClient client)
    {
        var response = await client.GetAsync(Poll);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Single(JsonDocument body) =>
        Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());

    private static string? Author(JsonDocument body) => Single(body).GetProperty("author").GetString();

    private static string? Text(JsonDocument body) => Single(body).GetProperty("text").GetString();

    /// <summary>
    /// First poll stamps the cursor and queues nothing — and deliberately looks nothing up either,
    /// since no line is being queued to put a name on.
    /// </summary>
    private static async Task InitialiseAsync(Stage2TestApp app, HttpClient client)
    {
        app.Bot.ScriptMessages(DiscordJson.User("100", "Old", "history line"));

        using var body = await PollAsync(client);

        Assert.Empty(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Empty(app.Bot.NicknameRequests);
    }

    /// <summary>
    /// The queued message with this id. Claimed messages stay hidden for the claim timeout, so a
    /// second poll normally carries only the new line — but the test app's timeout is 1 s, and a
    /// slow run must fail on the NAME under test rather than on a redelivery arriving alongside it.
    /// </summary>
    private static JsonElement Message(JsonDocument body, string id) =>
        Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("id").GetString() == id));

    [Fact]
    public async Task The_author_is_the_speakers_server_nickname()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9101", "Kaelen Vos");

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "anyone on tonight?"));

        using var body = await PollAsync(client);

        Assert.Equal("Kaelen Vos", Author(body));

        // The guild is discovered from the bridge channel, then the member read is made against it.
        Assert.EndsWith("channels/111222333444555666", app.Bot.NicknameRequests[0].Uri, StringComparison.Ordinal);
        Assert.Contains($"guilds/{GuildId}/members/9101", app.Bot.NicknameRequests[1].Uri, StringComparison.Ordinal);
        Assert.Equal("9101", Assert.Single(app.Bot.MemberLookups));
    }

    [Fact]
    public async Task A_speaker_with_no_nickname_keeps_their_account_display_name()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9101", nick: null);

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "anyone on tonight?"));

        using var body = await PollAsync(client);

        Assert.Equal("Bob", Author(body));
    }

    /// <summary>
    /// The common case is a member with no nickname, so "no nickname" has to be stored as firmly as
    /// a nickname is — otherwise the feature would cost a lookup per message forever.
    /// </summary>
    [Fact]
    public async Task A_speakers_answer_is_reused_across_messages()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9101", nick: null);

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "first"));

        using (var first = await PollAsync(client))
        {
            Assert.Equal("Bob", Message(first, "101").GetProperty("author").GetString());
        }

        // Same author id, second message: the answer is already known.
        app.Bot.ScriptMessages(
            "{\"id\":\"102\",\"content\":\"second\"," +
            "\"author\":{\"id\":\"9101\",\"username\":\"Bob\",\"global_name\":\"Bob\"}," +
            "\"timestamp\":\"2026-08-06T12:00:00+00:00\"}");

        using var second = await PollAsync(client);

        Assert.Equal("Bob", Message(second, "102").GetProperty("author").GetString());
        Assert.Equal("9101", Assert.Single(app.Bot.MemberLookups));
    }

    /// <summary>
    /// A user who has left the guild answers 404 forever. That is an answer about THEM, not about
    /// the relay's access, so it is cached as "no nickname" rather than retried per message — and
    /// unlike a permission failure it must not pause lookups for everyone else.
    /// </summary>
    [Fact]
    public async Task A_user_who_is_not_a_member_is_not_asked_about_twice()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);   // 9101 left unscripted: a 404

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "first"));

        using (var first = await PollAsync(client))
        {
            Assert.Equal("Bob", Message(first, "101").GetProperty("author").GetString());
        }

        app.Bot.ScriptMessages(DiscordJson.User("102", "Zed", "second"));

        using var second = await PollAsync(client);

        Assert.Equal("Zed", Message(second, "102").GetProperty("author").GetString());
        Assert.Equal(["9101", "9102"], app.Bot.MemberLookups);
    }

    /// <summary>
    /// A bot invited without access to the guild fails EVERY lookup identically. One failure must
    /// therefore stop the rest — otherwise a busy minute becomes a burst of doomed calls and a
    /// warning per poll — and the chat must go on arriving throughout.
    /// </summary>
    [Fact]
    public async Task A_forbidden_lookup_pauses_the_feature_and_the_chat_still_arrives()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMemberStatus("9101", HttpStatusCode.Forbidden);
        app.Bot.ScriptMember("9102", "Kaelen Vos");

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "first"));

        using (var first = await PollAsync(client))
        {
            Assert.Equal("Bob", Message(first, "101").GetProperty("author").GetString());
        }

        // 9102 has a nickname scripted and still shows the account name: lookups are paused, not
        // merely failing one at a time.
        app.Bot.ScriptMessages(DiscordJson.User("102", "Zed", "second"));

        using var second = await PollAsync(client);

        Assert.Equal("Zed", Message(second, "102").GetProperty("author").GetString());
        Assert.Equal("9101", Assert.Single(app.Bot.MemberLookups));
    }

    /// <summary>
    /// No guild means no nicknames — and no way to ask for one. The bridge is unchanged from
    /// before the feature existed, which is what every other test in the suite relies on.
    /// </summary>
    [Fact]
    public async Task An_unreadable_channel_leaves_names_exactly_as_they_were()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "anyone on tonight?"));

        using var body = await PollAsync(client);

        Assert.Equal("Bob", Author(body));
        Assert.Empty(app.Bot.MemberLookups);
    }

    [Fact]
    public async Task The_guild_id_is_read_once_and_then_remembered()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9101", "Kaelen Vos");
        app.Bot.ScriptMember("9102", "Zed Ryn");

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "first"));

        using (var first = await PollAsync(client))
        {
            Assert.Equal("Kaelen Vos", Message(first, "101").GetProperty("author").GetString());
        }

        app.Bot.ScriptMessages(DiscordJson.User("102", "Vex", "second"));

        using var second = await PollAsync(client);

        Assert.Equal("Zed Ryn", Message(second, "102").GetProperty("author").GetString());
        Assert.Single(app.Bot.NicknameRequests.Where(r =>
            !r.Uri.Contains("/members/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_configured_guild_id_skips_the_channel_read_entirely()
    {
        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Discord:GuildId"] = GuildId
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptMember("9101", "Kaelen Vos");   // no ScriptGuild: the channel read would 404

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "anyone on tonight?"));

        using var body = await PollAsync(client);

        Assert.Equal("Kaelen Vos", Author(body));
        Assert.Equal("9101", Assert.Single(app.Bot.MemberLookups));
    }

    /// <summary>
    /// A mention inside the text names the same person as an author prefix does, so it has to read
    /// the same way — "@Kaelen Vos", not the account name they are not known by here.
    /// </summary>
    [Fact]
    public async Task Mentions_inside_a_message_read_as_server_nicknames_too()
    {
        using var app = new Stage2TestApp();
        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9101", nick: null);
        app.Bot.ScriptMember("777001", "Kaelen Vos");

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "<@777001> you around?",
            mentionsJson: "[{\"id\":\"777001\",\"username\":\"zed_the_user\",\"global_name\":\"Zed\"}]"));

        using var body = await PollAsync(client);

        Assert.Equal("Bob", Author(body));
        Assert.Equal("@Kaelen Vos you around?", Text(body));
    }

    /// <summary>
    /// The eight-ball exchange is queued by the command SCAN, not by the reader — a separate path
    /// to the same guild room, so it has to put the same names on people. Both halves are covered
    /// here: the asker (author id 9200) and the bot itself, which has a nickname in this server
    /// like anyone else and is mentioned by the question it is answering.
    /// </summary>
    [Fact]
    public async Task An_eight_ball_exchange_speaks_under_server_nicknames()
    {
        const string BotUserId = "424242";

        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Discord:CommandsEnabled"] = "true",
            ["Discord:BotUserId"] = BotUserId,
            ["Relay:CommandScanIntervalSeconds"] = "0"
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9200", "Kaelen Vos");
        app.Bot.ScriptMember(BotUserId, "Oracle");

        // First poll stamps both cursors — the reader's and the scan's.
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Poll)).StatusCode);

        var mention = DiscordJson.Mention("200", "Bob", "will we win tonight?", BotUserId);
        app.Bot.ScriptMessages(mention);                                          // reader read
        app.Bot.ScriptMessages(mention);                                          // scan read
        app.Bot.ScriptBody("{\"id\":\"250\",\"author\":{\"username\":\"ShinyBot\"}}"); // reply POST

        using var body = await PollAsync(client);

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("Kaelen Vos", messages[0].GetProperty("author").GetString());
        Assert.Equal("@Oracle will we win tonight?", messages[0].GetProperty("text").GetString());
        Assert.Equal("Oracle", messages[1].GetProperty("author").GetString());
    }

    /// <summary>
    /// The point of storing names in the state file rather than in memory: this app pool
    /// idle-stops, and a nickname is a thing people change a handful of times a year. A cold start
    /// that re-read every speaker would spend the feature's whole cost on an answer that had not
    /// changed. Two apps over one state file is what a recycle looks like from here.
    /// </summary>
    [Fact]
    public async Task Stored_names_survive_a_restart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"relay-nicknames-{Guid.NewGuid():N}.json");

        var config = new Dictionary<string, string?> { ["Relay:StateFilePath"] = statePath };

        try
        {
            using (var first = new Stage2TestApp(config))
            {
                var client = first.CreateAuthenticatedClient();

                first.Bot.ScriptGuild(GuildId);
                first.Bot.ScriptMember("9101", "Kaelen Vos");

                await InitialiseAsync(first, client);

                first.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "first"));

                using var body = await PollAsync(client);

                Assert.Equal("Kaelen Vos", Author(body));
            }

            using var second = new Stage2TestApp(config);
            var restarted = second.CreateAuthenticatedClient();

            // Nothing scripted on the new app: a lookup would 404 and the name would fall back to
            // the account name. The guild id is durable for the same reason, so that is not re-read
            // either — the restarted relay asks Discord nothing at all. Spelled out rather than
            // built by DiscordJson.User, which derives the author id from the message id; this has
            // to be the SAME speaker as before the restart.
            second.Bot.ScriptMessages(
                "{\"id\":\"102\",\"content\":\"second\"," +
                "\"author\":{\"id\":\"9101\",\"username\":\"Bob\",\"global_name\":\"Bob\"}," +
                "\"timestamp\":\"2026-08-06T12:00:00+00:00\"}");

            using var after = await PollAsync(restarted);

            Assert.Equal("Kaelen Vos", Author(after));
            Assert.Empty(second.Bot.NicknameRequests);
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    /// <summary>
    /// A rename has to show up eventually, and an entry past the refresh window is what makes it:
    /// the person's next message re-reads their name. A stamp in the FUTURE counts as past it too
    /// — a clock correction or a state file from another host would otherwise pin the old name in
    /// place for as long as the skew lasted.
    /// </summary>
    [Theory]
    [InlineData(-48)]   // stale: read two days ago, the window is one
    [InlineData(6)]     // skewed: stamped in the future
    public async Task An_entry_outside_the_refresh_window_is_read_again(int hoursFromNow)
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"relay-nickrefresh-{Guid.NewGuid():N}.json");
        var stamp = DateTimeOffset.UtcNow.AddHours(hoursFromNow);

        await File.WriteAllTextAsync(statePath,
            $$"""
              {"GuildId":"{{GuildId}}",
               "Nicknames":[{"UserId":"9101","Nick":"Old Name","FetchedUtc":"{{stamp:O}}"}]}
              """);

        try
        {
            using var app = new Stage2TestApp(new Dictionary<string, string?>
            {
                ["Relay:StateFilePath"] = statePath
            });

            var client = app.CreateAuthenticatedClient();

            app.Bot.ScriptMember("9101", "Kaelen Vos");

            await InitialiseAsync(app, client);

            app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "hello"));

            using var body = await PollAsync(client);

            Assert.Equal("Kaelen Vos", Author(body));
            Assert.Equal("9101", Assert.Single(app.Bot.MemberLookups));
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    /// <summary>The kill switch: off means the lookups do not happen at all, not merely unused.</summary>
    [Fact]
    public async Task The_switch_off_makes_no_lookups_at_all()
    {
        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Discord:NicknamesEnabled"] = "false"
        });

        var client = app.CreateAuthenticatedClient();

        app.Bot.ScriptGuild(GuildId);
        app.Bot.ScriptMember("9101", "Kaelen Vos");

        await InitialiseAsync(app, client);

        app.Bot.ScriptMessages(DiscordJson.User("101", "Bob", "anyone on tonight?"));

        using var body = await PollAsync(client);

        Assert.Equal("Bob", Author(body));
        Assert.Empty(app.Bot.NicknameRequests);
    }
}
