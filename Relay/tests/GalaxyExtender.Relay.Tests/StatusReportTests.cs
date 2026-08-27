using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// What the bot actually says, and what it recognises as being asked (R11). Unit level: the wording
/// and the command matching are the parts most likely to be adjusted later, and neither needs a
/// fake Discord to pin down.
/// </summary>
public sealed class StatusReportTests
{
    private static string Status(PresenceSnapshot presence) =>
        StatusReport.Status(presence, 180, forwardingConfigured: true, stage2Enabled: true);

    private static string Status(int online, int known) =>
        Status(new PresenceSnapshot(online, known, DateTimeOffset.UtcNow));

    [Fact]
    public void One_client_online_reads_as_online()
    {
        var text = Status(online: 1, known: 1);

        Assert.StartsWith("**Guild chat bridge: online**", text);
        Assert.Contains("1 client connected", text);
        Assert.Contains("within the last 3 min", text);
    }

    [Fact]
    public void No_reply_names_the_bot_or_the_product()
    {
        // Whoever runs the relay names their Discord application whatever they like and can rename
        // it later, so a name baked in here would eventually contradict the name Discord shows on
        // the very same message. Discord renders the author name already; the text describes the
        // subject instead.
        string[] replies =
        [
            Status(online: 2, known: 5),
            Status(new PresenceSnapshot(0, 3, DateTimeOffset.UtcNow.AddHours(-1))),
            Status(new PresenceSnapshot(0, 0, null)),
            StatusReport.Help()
        ];

        Assert.All(replies, reply =>
        {
            Assert.DoesNotContain("GalaxyExtender", reply, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", reply, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Some_online_out_of_more_known_reads_as_a_fraction()
    {
        Assert.Contains("2 of 5 clients connected", Status(online: 2, known: 5));
    }

    [Fact]
    public void Nobody_online_but_clients_known_reports_how_long_it_has_been()
    {
        // The whole point of the offline answer: "and when was anyone last here?"
        var presence = new PresenceSnapshot(
            Online: 0,
            Known: 4,
            LastSeenUtc: DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-11));

        var text = Status(presence);

        Assert.StartsWith("**Guild chat bridge: offline**", text);
        Assert.Contains("4 clients seen recently", text);
        Assert.Contains("last seen 2 h 11 min ago", text);
    }

    [Fact]
    public void A_relay_nobody_has_ever_used_says_so_rather_than_naming_a_duration()
    {
        var text = Status(new PresenceSnapshot(0, 0, null));

        Assert.Contains("no client has ever checked in", text);
        Assert.DoesNotContain("last seen", text);
    }

    [Fact]
    public void The_switches_that_are_off_are_named_so_nobody_has_to_ask()
    {
        var text = StatusReport.Status(
            new PresenceSnapshot(1, 1, DateTimeOffset.UtcNow), 180,
            forwardingConfigured: false, stage2Enabled: false);

        Assert.Contains("Game → Discord forwarding is not configured", text);
        Assert.Contains("Discord → game delivery is switched off", text);
    }

    [Fact]
    public void The_last_alert_age_reads_as_hours_and_minutes()
    {
        var text = StatusReport.Status(
            new PresenceSnapshot(1, 1, DateTimeOffset.UtcNow), 180,
            forwardingConfigured: true, stage2Enabled: true,
            lastAlertUtc: DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(-7));

        Assert.Contains("Last World Boss Alert: 3 hours and 07 minutes ago.", text);
    }

    [Fact]
    public void A_last_alert_older_than_a_day_keeps_counting_hours()
    {
        // The guild reads this line to judge whether a boss window has come round again, and that
        // arithmetic is easier from hours than from a day rollover.
        var text = StatusReport.Status(
            new PresenceSnapshot(1, 1, DateTimeOffset.UtcNow), 180,
            forwardingConfigured: true, stage2Enabled: true,
            lastAlertUtc: DateTimeOffset.UtcNow.AddHours(-51).AddMinutes(-3));

        Assert.Contains("Last World Boss Alert: 51 hours and 03 minutes ago.", text);
    }

    [Fact]
    public void No_alert_on_record_means_no_alert_line()
    {
        Assert.DoesNotContain("Last World Boss Alert", Status(online: 1, known: 1));
    }

    [Fact]
    public void A_last_alert_stamp_from_the_future_reads_as_zero_rather_than_negative()
    {
        // Clock skew or a state file moved between hosts can put the stamp ahead of now.
        var text = StatusReport.Status(
            new PresenceSnapshot(1, 1, DateTimeOffset.UtcNow), 180,
            forwardingConfigured: true, stage2Enabled: true,
            lastAlertUtc: DateTimeOffset.UtcNow.AddMinutes(30));

        Assert.Contains("Last World Boss Alert: 0 hours and 00 minutes ago.", text);
    }

    [Fact]
    public void The_reply_always_fits_a_discord_message()
    {
        var text = Status(online: 5000, known: 5000);

        Assert.True(text.Length <= StatusReport.MaxMessageLength, $"length {text.Length}");
    }

    [Theory]
    [InlineData("<@424242> status", BotCommands.BotCommand.Status)]
    [InlineData("<@!424242> status", BotCommands.BotCommand.Status)]
    [InlineData("<@424242> STATUS", BotCommands.BotCommand.Status)]
    [InlineData("hey <@424242> what's the status?", BotCommands.BotCommand.Status)]
    [InlineData("<@424242> who is online", BotCommands.BotCommand.Status)]
    [InlineData("<@424242>", BotCommands.BotCommand.Help)]
    [InlineData("<@424242> help", BotCommands.BotCommand.Help)]
    [InlineData("<@424242> commands", BotCommands.BotCommand.Help)]
    [InlineData("<@424242> is a good bot", BotCommands.BotCommand.EightBall)]
    [InlineData("<@424242> statuses are fine", BotCommands.BotCommand.EightBall)]
    public void Commands_are_recognised_by_word_not_by_position(string content, BotCommands.BotCommand expected) =>
        Assert.Equal(expected, BotCommands.Parse(content));

    [Fact]
    public void An_id_inside_a_mention_token_is_never_read_as_a_word()
    {
        // An emoji or channel token whose NAME happens to be a command must not trigger one: the
        // token is stripped whole, so only what the typist actually wrote counts as words — and
        // "nice" is a question for the eight ball, not a status request.
        Assert.Equal(BotCommands.BotCommand.EightBall, BotCommands.Parse("<@424242> <:status:12345> nice"));
    }

    private static DiscordMessage Message(string? content, params string[] mentionIds) =>
        new("1", 1, content, "9", "Bob", "bob", false, false, false, false,
            DateTimeOffset.UtcNow,
            mentionIds.ToDictionary(id => id, _ => "GalaxyExtender"));

    [Fact]
    public void A_mention_is_recognised_from_the_mentions_array()
    {
        // Covers a reply-with-mention, where the content carries no <@id> token at all.
        Assert.True(BotCommands.Mentions(Message("status", "424242"), "424242"));
    }

    [Fact]
    public void A_mention_is_recognised_from_the_content_token_alone()
    {
        Assert.True(BotCommands.Mentions(Message("<@424242> status"), "424242"));
        Assert.True(BotCommands.Mentions(Message("<@!424242> status"), "424242"));
    }

    [Fact]
    public void Somebody_elses_mention_is_not_ours()
    {
        Assert.False(BotCommands.Mentions(Message("<@999> status", "999"), "424242"));
    }
}
