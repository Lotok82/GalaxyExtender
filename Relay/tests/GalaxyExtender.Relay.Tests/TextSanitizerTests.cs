using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Tests;

public sealed class TextSanitizerTests
{
    [Fact]
    public void Normalize_strips_swg_escapes()
    {
        var input = @"\#00ff00GalaxyExtender\>032: \#ffffffhello there\#.";

        Assert.Equal("GalaxyExtender: hello there", TextSanitizer.Normalize(input));
    }

    [Fact]
    public void Normalize_keeps_backslashes_that_are_not_escapes()
    {
        Assert.Equal(@"C:\Users\kaelen and \#zz stays", TextSanitizer.Normalize(@"C:\Users\kaelen and \#zz stays"));
    }

    [Fact]
    public void Normalize_maps_control_characters_to_spaces_and_trims()
    {
        Assert.Equal("a  b", TextSanitizer.Normalize(" a\u0001\u0009b\u007F "));
    }

    [Theory]
    [InlineData(DiscordTarget.Embed)]
    [InlineData(DiscordTarget.PlainMessage)]
    public void ForDiscord_neutralises_mass_mentions(DiscordTarget target)
    {
        var result = TextSanitizer.ForDiscord("hey @everyone and @HERE", 512, target);

        Assert.DoesNotContain("@everyone", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@here", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@\u200Deveryone", result);
    }

    [Theory]
    [InlineData(DiscordTarget.Embed)]
    [InlineData(DiscordTarget.PlainMessage)]
    public void ForDiscord_escapes_markdown(DiscordTarget target)
    {
        var result = TextSanitizer.ForDiscord(
            "`code` *bold* _under_ ~strike~ |spoil| back\\slash", 512, target);

        Assert.Equal(@"\`code\` \*bold\* \_under\_ \~strike\~ \|spoil\| back\\slash", result);
    }

    /// <summary>
    /// Embed descriptions (unlike plain messages) render [text](url) as a masked hyperlink —
    /// unescaped brackets would let a player publish a link whose visible text hides the target,
    /// authored by the relay.
    /// </summary>
    [Fact]
    public void ForDiscord_escapes_masked_link_syntax_for_an_embed()
    {
        var result = TextSanitizer.ForDiscord(
            "[Guild bank payout](https://phishing.example/steal)", 512, DiscordTarget.Embed);

        Assert.Equal(@"\[Guild bank payout\](https://phishing.example/steal)", result);
    }

    /// <summary>
    /// The other half of that rule. A plain message renders brackets literally, so escaping them
    /// buys no safety and costs legibility: guild lines arrive from the game already carrying a
    /// "[GuildChat] " prefix, which would otherwise publish as "\[GuildChat\] ".
    /// </summary>
    [Fact]
    public void ForDiscord_leaves_brackets_alone_in_a_plain_message()
    {
        var result = TextSanitizer.ForDiscord(
            "[GuildChat] carnor: yo bud", 512, DiscordTarget.PlainMessage);

        Assert.Equal("[GuildChat] carnor: yo bud", result);
    }

    [Theory]
    [InlineData(DiscordTarget.Embed)]
    [InlineData(DiscordTarget.PlainMessage)]
    public void ForDiscord_escapes_blockquote_only_at_line_start(DiscordTarget target)
    {
        Assert.Equal(@"\> quoted a > b", TextSanitizer.ForDiscord("> quoted a > b", 512, target));
    }

    [Theory]
    [InlineData(DiscordTarget.Embed)]
    [InlineData(DiscordTarget.PlainMessage)]
    public void ForDiscord_clamps_and_never_ends_on_a_lone_backslash(DiscordTarget target)
    {
        var result = TextSanitizer.ForDiscord(new string('*', 300), 512, target);

        Assert.True(result.Length <= 512);
        Assert.False(result.EndsWith('\\') && !result.EndsWith(@"\\") && !result.EndsWith(@"\*"),
            "clamped text must not end with a dangling escape backslash");
    }

    /// <summary>The plan's named case: a 5000-char payload survives, split across messages.</summary>
    [Theory]
    [InlineData(TextSanitizer.MaxDescriptionLength)]
    [InlineData(TextSanitizer.MaxContentLength)]
    public void BuildChunks_splits_at_the_limit_without_splitting_lines(int limit)
    {
        var lines = Enumerable.Range(0, 12).Select(i => new string((char)('a' + i), 500)).ToList();

        var chunks = TextSanitizer.BuildChunks(lines, limit);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= limit));
        Assert.Equal(lines.Count, chunks.Sum(chunk => chunk.LineCount));

        // No line was cut: every chunk is whole lines joined by newlines.
        Assert.All(chunks, chunk =>
            Assert.All(chunk.Text.Split('\n'), line => Assert.Equal(500, line.Length)));
    }

    /// <summary>
    /// The tighter plain-message ceiling has to actually bite — the same batch that fits one embed
    /// description must split into more than one message.
    /// </summary>
    [Fact]
    public void BuildChunks_splits_more_finely_for_a_plain_message_than_for_an_embed()
    {
        var lines = Enumerable.Range(0, 6).Select(i => new string((char)('a' + i), 500)).ToList();

        var asEmbed = TextSanitizer.BuildChunks(lines, TextSanitizer.MaxDescriptionLength);
        var asContent = TextSanitizer.BuildChunks(lines, TextSanitizer.MaxContentLength);

        Assert.Single(asEmbed);
        Assert.True(asContent.Count > 1);
        Assert.Equal(lines.Count, asContent.Sum(chunk => chunk.LineCount));
    }

    [Fact]
    public void BuildChunks_keeps_a_short_batch_in_one_chunk()
    {
        var chunks = TextSanitizer.BuildChunks(["one", "two"], TextSanitizer.MaxContentLength);

        var chunk = Assert.Single(chunks);
        Assert.Equal("one\ntwo", chunk.Text);
        Assert.Equal(2, chunk.LineCount);
    }

    [Fact]
    public void Dedupe_key_is_stable_and_occurrence_sensitive()
    {
        var a = DedupeService.Key("hello", 1);
        var b = DedupeService.Key("hello", 1);
        var c = DedupeService.Key("hello", 2);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
