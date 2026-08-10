using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The world boss alert feed: lines beginning with a configured tag publish as a coloured embed
/// while ordinary guild chat publishes as a plain message, so a boxed message means something.
/// See Documentation/world-boss-alert-plan.md.
/// </summary>
public sealed class AlertTests
{
    private const int Green = 3066993;
    private const int Red = 15158332;

    private static ConfiguredRelayTestApp AppWithAlerts(params (string Key, string? Value)[] extra)
    {
        var config = new Dictionary<string, string?> { ["Discord:AlertsEnabled"] = "true" };

        foreach (var (key, value) in extra)
        {
            config[key] = value;
        }

        return new ConfiguredRelayTestApp(config);
    }

    private static object Batch(string clientId, params object[] lines) => new
    {
        batchId = Guid.NewGuid().ToString(),
        client = new { id = clientId, character = clientId, galaxy = "Basilisk" },
        lines
    };

    private static JsonElement Single(RelayTestApp app)
    {
        var body = Assert.Single(app.Discord.RequestBodies);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Note the escaped brackets in the expected description. That is the embed path's mandatory
    /// masked-link escaping applied to the tag itself; Discord consumes the backslashes and renders
    /// "[PvE World Boss] ...". The plain-text path deliberately does the opposite — see
    /// TextSanitizerTests for both halves of that rule.
    /// </summary>
    [Theory]
    [InlineData("[PvE World Boss] a Krayt Dragon has spawned!", Green, @"\[PvE World Boss\]")]
    [InlineData("[PvP World Boss] Bloodfin has spawned!", Red, @"\[PvP World Boss\]")]
    public async Task An_alert_publishes_as_an_embed_coloured_by_its_tag(
        string line, int expectedColor, string expectedTag)
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", Batch("kaelen", ChatBatches.Line(line)));

        var embed = Single(app).GetProperty("embeds")[0];
        var description = embed.GetProperty("description").GetString()!;

        Assert.Equal(expectedColor, embed.GetProperty("color").GetInt32());
        Assert.StartsWith(expectedTag, description, StringComparison.Ordinal);
        Assert.EndsWith("has spawned!", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The casing the server actually broadcasts is unverified until the first live alert, so a
    /// case mismatch must not silently drop every alert.
    /// </summary>
    [Theory]
    [InlineData("[PVP WORLD BOSS] Bloodfin has spawned!")]
    [InlineData("[pvp world boss] Bloodfin has spawned!")]
    public async Task Tag_matching_ignores_case(string line)
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", Batch("kaelen", ChatBatches.Line(line)));

        Assert.Equal(Red, Single(app).GetProperty("embeds")[0].GetProperty("color").GetInt32());
    }

    /// <summary>
    /// The anti-spoof rule, and the reason matching is anchored at the start. A server broadcast
    /// arrives with no sender prefix; anything a player types carries one, so it can never buy
    /// itself a red alert.
    /// </summary>
    [Fact]
    public async Task A_player_quoting_the_tag_is_ordinary_chat()
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[GuildChat] Kaelen: [PvP World Boss] gotcha")));

        var payload = Single(app);

        Assert.False(payload.TryGetProperty("embeds", out _));
        Assert.Contains("gotcha", payload.GetProperty("content").GetString()!);
    }

    /// <summary>Off by default: turning the feed on must be a deliberate config decision.</summary>
    [Fact]
    public async Task Alerts_are_ordinary_chat_until_the_switch_is_on()
    {
        using var app = new RelayTestApp();

        await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[PvE World Boss] a Krayt Dragon has spawned!")));

        Assert.False(Single(app).TryGetProperty("embeds", out _));
    }

    /// <summary>
    /// An embed description renders [text](url) as a masked hyperlink, so this path must keep the
    /// bracket escaping that the plain-text path drops. Getting the two backwards is the one way to
    /// reintroduce the hole, so pin it from the outside.
    /// </summary>
    [Fact]
    public async Task An_alert_escapes_masked_link_syntax()
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line(
                "[PvE World Boss] [free loot](https://phishing.example/steal) awaits")));

        var description = Single(app).GetProperty("embeds")[0].GetProperty("description").GetString()!;

        Assert.Contains(@"\[free loot\]", description);
        Assert.DoesNotContain("[free loot](", description);
    }

    /// <summary>
    /// A batch carrying both kinds splits into two posts rather than reordering the guild's
    /// conversation around the alert.
    /// </summary>
    [Fact]
    public async Task A_mixed_batch_posts_chat_and_alert_separately_in_arrival_order()
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat", Batch("kaelen",
            ChatBatches.Line("[GuildChat] Kaelen: anyone about?"),
            ChatBatches.Line("[PvP World Boss] Bloodfin has spawned!"),
            ChatBatches.Line("[GuildChat] Tarn: on my way")));

        Assert.Equal(3, app.Discord.RequestBodies.Count);

        var first = JsonDocument.Parse(app.Discord.RequestBodies[0]).RootElement;
        var second = JsonDocument.Parse(app.Discord.RequestBodies[1]).RootElement;
        var third = JsonDocument.Parse(app.Discord.RequestBodies[2]).RootElement;

        Assert.Contains("anyone about?", first.GetProperty("content").GetString()!);
        Assert.Equal(Red, second.GetProperty("embeds")[0].GetProperty("color").GetInt32());
        Assert.Contains("on my way", third.GetProperty("content").GetString()!);
    }

    /// <summary>Consecutive chat lines still share one post — an alert splits a batch, nothing else does.</summary>
    [Fact]
    public async Task Consecutive_chat_lines_still_share_one_post()
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat", Batch("kaelen",
            ChatBatches.Line("[GuildChat] Kaelen: one"),
            ChatBatches.Line("[GuildChat] Tarn: two")));

        var content = Single(app).GetProperty("content").GetString()!;

        Assert.Contains("one", content);
        Assert.Contains("two", content);
    }

    /// <summary>
    /// Configuring tags REPLACES the built-in set rather than merging, so an operator can retire a
    /// tag and not merely add one. .NET's configuration binder merges into a pre-populated
    /// dictionary, which is why the option starts null.
    /// </summary>
    [Fact]
    public async Task Configured_tags_replace_the_built_in_set()
    {
        using var app = AppWithAlerts(("Discord:AlertTags:[Server]", "255"));
        var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[Server] restarting in 5 minutes")));
        await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[PvE World Boss] no longer configured")));

        Assert.Equal(2, app.Discord.RequestBodies.Count);

        var configured = JsonDocument.Parse(app.Discord.RequestBodies[0]).RootElement;
        var retired = JsonDocument.Parse(app.Discord.RequestBodies[1]).RootElement;

        Assert.Equal(255, configured.GetProperty("embeds")[0].GetProperty("color").GetInt32());
        Assert.False(retired.TryGetProperty("embeds", out _));
    }

    /// <summary>
    /// When one configured tag is a prefix of another, the longest match owns the line. Without
    /// the explicit ordering this was dictionary enumeration order — nondeterministic, so the
    /// same alert could change colour between deploys.
    /// </summary>
    [Fact]
    public async Task Overlapping_tags_resolve_to_the_longest_match()
    {
        using var app = AppWithAlerts(
            ("Discord:AlertTags:[Boss]", "255"),
            ("Discord:AlertTags:[Boss Elite]", "16711680"));
        var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[Boss Elite] Bloodfin has spawned!")));
        await client.PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[Boss] a lesser boss has spawned!")));

        Assert.Equal(2, app.Discord.RequestBodies.Count);

        var elite = JsonDocument.Parse(app.Discord.RequestBodies[0]).RootElement;
        var plain = JsonDocument.Parse(app.Discord.RequestBodies[1]).RootElement;

        Assert.Equal(16711680, elite.GetProperty("embeds")[0].GetProperty("color").GetInt32());
        Assert.Equal(255, plain.GetProperty("embeds")[0].GetProperty("color").GetInt32());
    }

    /// <summary>
    /// A tag a client gates on but the relay does not know publishes as ordinary chat. Unstyled is
    /// the intended degradation; dropped would not be.
    /// </summary>
    [Fact]
    public async Task An_unknown_tag_publishes_as_chat_rather_than_being_dropped()
    {
        using var app = AppWithAlerts();

        await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat",
            Batch("kaelen", ChatBatches.Line("[PvX World Boss] something new has spawned")));

        var payload = Single(app);

        Assert.False(payload.TryGetProperty("embeds", out _));
        Assert.Contains("something new", payload.GetProperty("content").GetString()!);
    }

    /// <summary>Cross-client dedupe applies to alerts too: every in-world client sees the broadcast.</summary>
    [Fact]
    public async Task The_same_alert_from_two_clients_posts_once()
    {
        using var app = AppWithAlerts();
        var client = app.CreateAuthenticatedClient();
        var line = "[PvE World Boss] a Krayt Dragon has spawned!";

        await client.PostAsJsonAsync("/api/v1/chat", Batch("kaelen", ChatBatches.Line(line)));

        var second = await client.PostAsJsonAsync("/api/v1/chat", Batch("tarn", ChatBatches.Line(line)));
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("deduped").GetInt32());
        Assert.Equal(1, app.Discord.RequestCount);
    }

    [Fact]
    public async Task Health_reports_whether_the_alert_feed_is_configured()
    {
        using var off = new RelayTestApp();
        var offBody = await off.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        Assert.False(offBody.GetProperty("config").GetProperty("alertsConfigured").GetBoolean());

        using var on = AppWithAlerts();
        var onBody = await on.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        Assert.True(onBody.GetProperty("config").GetProperty("alertsConfigured").GetBoolean());
    }
}
