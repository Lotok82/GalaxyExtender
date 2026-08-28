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
    /// The other half of that rule — an accepted risk, not a safe case. Webhook-authored content
    /// DOES render masked links, so an unescaped [text](url) in guild chat is clickable; that is
    /// tolerated (private guild, trusted members — decision of 2026-08-10) because escaping would
    /// publish visible backslashes around the game-supplied "[GuildChat] " prefix on every line.
    /// See the DiscordTarget doc.
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

    /// <summary>
    /// The emoji restore runs before the clamp, so an astral pair can straddle the limit; cutting
    /// between its surrogates would ship invalid UTF-16 that serialises as a replacement glyph.
    /// </summary>
    [Fact]
    public void ForDiscord_clamp_never_splits_a_surrogate_pair()
    {
        // 'a' x 511 leaves exactly one UTF-16 unit of room; the restored 😂 needs two.
        var result = TextSanitizer.ForDiscord(
            new string('a', 511) + ":joy:", 512, DiscordTarget.PlainMessage);

        Assert.True(result.Length <= 512);
        Assert.False(char.IsHighSurrogate(result[^1]), "clamped text must not end mid-surrogate-pair");
    }

    // --- emoji shortcodes (game → Discord; the reverse of Stage2Sanitizer's EmojiNamer pass) ---

    [Theory]
    [InlineData(DiscordTarget.Embed)]
    [InlineData(DiscordTarget.PlainMessage)]
    public void ForDiscord_restores_known_shortcodes_to_emoji(DiscordTarget target) =>
        Assert.Equal("gg \U0001F602 \U0001F44D",
            TextSanitizer.ForDiscord("gg :joy: :thumbsup:", 512, target));

    [Fact]
    public void ForDiscord_shortcodes_match_case_insensitively() =>
        Assert.Equal("\U0001F602", TextSanitizer.ForDiscord(":JOY:", 512, DiscordTarget.PlainMessage));

    /// <summary>
    /// BMP symbols get a variation selector so Discord shows the colour emoji, not the
    /// monochrome text glyph.
    /// </summary>
    [Fact]
    public void ForDiscord_gives_bmp_symbols_their_emoji_presentation() =>
        Assert.Equal("❤️", TextSanitizer.ForDiscord(":heart:", 512, DiscordTarget.PlainMessage));

    [Fact]
    public void ForDiscord_leaves_unknown_shortcodes_as_escaped_text() =>
        Assert.Equal(@":not\_a\_thing:",
            TextSanitizer.ForDiscord(":not_a_thing:", 512, DiscordTarget.PlainMessage));

    [Fact]
    public void ForDiscord_does_not_mistake_timestamps_for_shortcodes() =>
        Assert.Equal("raid at 12:30:45 tonight",
            TextSanitizer.ForDiscord("raid at 12:30:45 tonight", 512, DiscordTarget.PlainMessage));

    /// <summary>
    /// The rewrite is presentation-only: the dedupe hash must keep seeing the exact typed text,
    /// so Normalize never touches shortcodes.
    /// </summary>
    [Fact]
    public void Normalize_leaves_shortcodes_alone() =>
        Assert.Equal("gg :joy:", TextSanitizer.Normalize("gg :joy:"));

    /// <summary>
    /// Both directions read from the same table, so a Discord emoji that lands in game as a
    /// shortcode comes back to Discord as the identical emoji, and vice versa.
    /// </summary>
    [Theory]
    [InlineData("\U0001F602", ":joy:")]          // 😂 — astral plane
    [InlineData("❤️", ":heart:")]      // ❤️ — BMP symbol with VS16
    [InlineData("\U0001F5E1️", ":dagger:")] // 🗡️ — astral but text-presentation by default
    [InlineData("\U0001F6E1️", ":shield:")] // 🛡️ — same; VS16 must survive the round trip
    public void Emoji_round_trip_is_exact(string emoji, string shortcode)
    {
        Assert.Equal(shortcode, EmojiNamer.Replace(emoji));
        Assert.Equal(emoji, EmojiNamer.RestoreEmoji(shortcode));
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
