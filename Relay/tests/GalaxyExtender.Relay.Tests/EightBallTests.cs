using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The magic eight ball's pool and pick. Pure functions, so these run without a fake Discord;
/// the wiring (a mention gets one of these posted back) is covered in <see cref="BotCommandTests"/>.
/// </summary>
public sealed class EightBallTests
{
    [Fact]
    public void The_pool_is_a_hundred_distinct_printable_answers()
    {
        // A hundred by design, not by accident — and every entry has to survive being posted
        // verbatim as a Discord reply the relay authored.
        Assert.Equal(100, EightBall.Phrases.Count);
        Assert.Equal(100, EightBall.Phrases.Distinct(StringComparer.Ordinal).Count());

        Assert.All(EightBall.Phrases, phrase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(phrase));
            Assert.Equal(phrase, phrase.Trim());
            Assert.True(phrase.Length <= StatusReport.MaxMessageLength);
            Assert.DoesNotContain('@', phrase);   // nothing that could ever read as a ping
            Assert.DoesNotContain('\n', phrase);
        });
    }

    [Fact]
    public void The_answer_is_always_one_of_the_pool()
    {
        for (ulong id = 0; id < 1000; id++)
        {
            Assert.Contains(EightBall.Reply(id), EightBall.Phrases);
        }
    }

    [Fact]
    public void The_same_question_message_always_gets_the_same_answer()
    {
        // The pick is a pure function of the snowflake: what the bot said (or failed to post)
        // for a given message is a fact, not a die roll.
        Assert.Equal(EightBall.Reply(1123581321345589144UL), EightBall.Reply(1123581321345589144UL));
    }

    [Fact]
    public void Quiet_channel_snowflakes_still_spread_across_the_pool()
    {
        // A quiet channel's message ids differ mostly in the timestamp bits: the low 22 bits are
        // worker/process/increment fields that barely vary. Model the worst case — low bits all
        // zero, timestamps a millisecond apart — and the mix must still reach most of the pool.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (ulong timestamp = 0; timestamp < 1000; timestamp++)
        {
            seen.Add(EightBall.Reply(timestamp << 22));
        }

        Assert.True(seen.Count > 90, $"only {seen.Count} of 100 phrases reachable");
    }
}
