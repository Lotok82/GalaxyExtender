using System.Net.Http.Json;
using System.Text.Json;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The background ticker (R12) — the answer to the one hole in a request-driven relay: with nobody
/// in game there are no requests, so before this the bot could not answer "is the bridge up?" at
/// the only times anyone asks.
///
/// Every test here makes its assertion with NO client request having been sent. That is the whole
/// claim, so the tests take care to start the host by touching <c>Services</c> rather than by
/// calling <c>CreateClient</c> — a request would carry the same work and prove nothing.
/// </summary>
public sealed class BackgroundTickerTests
{
    private const string BotUserId = "424242";

    /// <summary>Generous: this asserts the ticker eventually runs, never how fast a machine is.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Commands on, bot identity pinned (so no <c>users/@me</c> read), a 1 s tick (the floor), and
    /// no scan interval so consecutive ticks both scan. Cleanup stays off: its Discord calls would
    /// interleave with the ones under test.
    /// </summary>
    private static Stage2TestApp TickerApp(Dictionary<string, string?>? extra = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Discord:CommandsEnabled"] = "true",
            ["Discord:BotUserId"] = BotUserId,
            ["Relay:CommandScanIntervalSeconds"] = "0",
            ["Relay:BackgroundTickSeconds"] = "1"
        };

        foreach (var (key, value) in extra ?? [])
        {
            overrides[key] = value;
        }

        return new Stage2TestApp(overrides);
    }

    /// <summary>
    /// Starts the host — and with it the hosted services — without sending a request. Accessing
    /// <c>Services</c> is what forces WebApplicationFactory to build and start the host.
    /// </summary>
    private static BackgroundTicker Start(Stage2TestApp app) =>
        app.Services.GetRequiredService<BackgroundTicker>();

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
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
    /// The headline: somebody asks the bot for status with the guild empty, and gets an answer.
    /// Two scripted reads — the first stamps the cursor and deliberately answers nothing, the
    /// second carries the mention — so the assertion holds however the ticks land in time.
    /// </summary>
    [Fact]
    public async Task Status_mention_is_answered_with_no_client_traffic()
    {
        using var app = TickerApp();
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        app.Bot.ScriptMessages(DiscordJson.Mention("101", "Bob", "status", BotUserId));

        Start(app);

        Assert.True(await WaitUntil(() => PostedReply(app) is not null),
            "the ticker never posted a reply");

        Assert.Contains("Guild chat bridge", PostedReply(app));

        // Answered in Discord and not injected into the guild room: the webhook saw nothing.
        Assert.Equal(0, app.Discord.RequestCount);
    }

    /// <summary>
    /// The second half of the same hole: ordinary chat posted while the guild is empty is told so,
    /// unprompted. Nobody has pinged presence, so the relay knows nothing is online to receive it.
    /// </summary>
    [Fact]
    public async Task Undelivered_chat_gets_an_unprompted_notice_with_no_client_traffic()
    {
        using var app = TickerApp();
        app.Bot.ScriptMessages(DiscordJson.User("100", "Bob", "hello"));
        app.Bot.ScriptMessages(DiscordJson.User(
            "101", "Bob", "anyone up for a Krayt run?", timestamp: DateTimeOffset.UtcNow));

        Start(app);

        Assert.True(await WaitUntil(() => PostedReply(app) is not null),
            "the ticker never posted a delivery notice");

        Assert.Contains("offline", PostedReply(app));
    }

    /// <summary>
    /// A tick is worth nothing if a bad Discord response can end it. The scan swallows the failure
    /// and the loop carries on — losing one round of answers, not the feature.
    /// </summary>
    [Fact]
    public async Task Ticker_survives_a_failing_discord()
    {
        using var app = TickerApp();
        app.Bot.ScriptStatus(System.Net.HttpStatusCode.InternalServerError);

        var ticker = Start(app);

        Assert.True(await WaitUntil(() => ticker.Ticks >= 3), $"only {ticker.Ticks} tick(s) ran");
        Assert.Null(ticker.LastError);
    }

    /// <summary>
    /// The self-ping is insurance against the host, so it must never become a way for the host to
    /// break the ticker. Port 9 is the discard port: the connection fails immediately.
    /// </summary>
    [Fact]
    public async Task A_broken_self_ping_does_not_stop_the_ticker()
    {
        using var app = TickerApp(new Dictionary<string, string?>
        {
            ["Relay:SelfPingUrl"] = "http://127.0.0.1:9/api/v1/health"
        });

        var ticker = Start(app);

        Assert.True(await WaitUntil(() => ticker.Ticks >= 2), $"only {ticker.Ticks} tick(s) ran");
        Assert.Null(ticker.LastError);
    }

    /// <summary>
    /// Zero switches it off completely — the pre-R12 behaviour, and what the rest of the suite
    /// runs with. Asserted through /health because that is where an operator checks it.
    /// </summary>
    [Fact]
    public async Task Zero_disables_the_ticker_and_health_says_so()
    {
        using var app = new Stage2TestApp(new Dictionary<string, string?>
        {
            ["Discord:CommandsEnabled"] = "true",
            ["Discord:BotUserId"] = BotUserId,
            ["Relay:BackgroundTickSeconds"] = "0"
        });

        app.Bot.ScriptMessages(DiscordJson.Mention("101", "Bob", "status", BotUserId));

        var ticker = Start(app);
        await Task.Delay(TimeSpan.FromSeconds(2));

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        var reported = body.GetProperty("backgroundTicker");

        Assert.False(reported.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, reported.GetProperty("ticks").GetInt64());
        Assert.False(reported.GetProperty("selfPing").GetBoolean());
        Assert.Equal(0, ticker.Ticks);

        // Nothing reached Discord: the mention sat there unread, which is exactly the behaviour
        // the ticker exists to change.
        Assert.Equal(0, app.Bot.RequestCount);
    }

    /// <summary>
    /// /health has to carry enough to answer "did the ticker survive the quiet hours on this
    /// host?", which is the entire reason for deploying it.
    /// </summary>
    [Fact]
    public async Task Health_reports_a_running_ticker()
    {
        using var app = TickerApp();
        var ticker = Start(app);

        Assert.True(await WaitUntil(() => ticker.Ticks >= 1), "the ticker never ran");

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        var reported = body.GetProperty("backgroundTicker");

        Assert.True(reported.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, reported.GetProperty("intervalSeconds").GetDouble());
        Assert.True(reported.GetProperty("ticks").GetInt64() >= 1);
        Assert.True(reported.GetProperty("lastTickUtc").GetDateTimeOffset() > DateTimeOffset.MinValue);
    }
}
