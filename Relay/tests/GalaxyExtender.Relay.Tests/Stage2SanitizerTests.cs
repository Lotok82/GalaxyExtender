using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// R5: Discord → game text preparation. Discord content is untrusted input into every guild
/// member's client — the SWG-escape stripping cases here are the security-sensitive ones, since
/// the Core3 server does not strip colour codes itself (S4 finding).
/// </summary>
public sealed class Stage2SanitizerTests
{
    private static readonly IReadOnlyDictionary<string, string> NoMentions =
        new Dictionary<string, string>();

    private static string Text(string content,
        IReadOnlyDictionary<string, string>? mentions = null,
        bool attachments = false, bool embeds = false, bool stickers = false) =>
        Stage2Sanitizer.SanitizeText(content, mentions ?? NoMentions, attachments, embeds, stickers);

    // --- SWG escapes (the reason this sanitizer exists) ---

    [Theory]
    [InlineData(@"\#FF0000red text", "red text")]
    [InlineData(@"hello \#.world", "hello world")]
    [InlineData(@"indent \>042 attack", "indent attack")]
    [InlineData(@"\#00ff00\#0000ff", "")]
    public void Swg_escape_sequences_are_stripped(string content, string expected) =>
        Assert.Equal(expected, Text(content));

    [Fact]
    public void Lone_backslash_hash_without_valid_form_passes_through_literally() =>
        Assert.Equal(@"\#zz fine", Text(@"\#zz fine"));

    // --- Discord tokens ---

    [Fact]
    public void User_mentions_resolve_from_the_message_mention_map()
    {
        var mentions = new Dictionary<string, string> { ["42"] = "Zed" };

        Assert.Equal("hi @Zed and @Zed", Text("hi <@42> and <@!42>", mentions));
    }

    [Fact]
    public void Unknown_user_mention_becomes_someone() =>
        Assert.Equal("hi @someone", Text("hi <@42>"));

    [Fact]
    public void Role_and_channel_references_become_generic() =>
        Assert.Equal("ping @role in #channel", Text("ping <@&123> in <#456>"));

    [Theory]
    [InlineData("<:krayt:12345>", ":krayt:")]
    [InlineData("<a:party_blob:9>", ":party_blob:")]
    public void Custom_emoji_reduce_to_their_name(string token, string expected) =>
        Assert.Equal(expected, Text(token));

    [Fact]
    public void Timestamp_tokens_become_a_time_marker() =>
        Assert.Equal("raid at [time]", Text("raid at <t:1754500000:R>"));

    [Fact]
    public void Unrecognised_angle_brackets_pass_through() =>
        Assert.Equal("a < b and <notatoken>", Text("a < b and <notatoken>"));

    // --- whitespace and unrenderable characters ---

    [Fact]
    public void Newlines_collapse_to_single_spaces() =>
        Assert.Equal("first second third", Text("first\nsecond\n\n\nthird"));

    [Fact]
    public void Typographic_quotes_and_dashes_fold_to_ascii() =>
        Assert.Equal("\"it's\" - fine", Text("“it’s” — fine"));

    [Fact]
    public void Emoji_runs_collapse_to_a_single_question_mark() =>
        Assert.Equal("nice ? work", Text("nice \U0001F600\U0001F600\U0001F600 work"));

    [Fact]
    public void Zero_width_characters_vanish_without_a_trace() =>
        Assert.Equal("word", Text("wo​‍rd"));

    [Fact]
    public void Latin1_letters_survive() =>
        Assert.Equal("café über ¿qué?", Text("café über ¿qué?"));

    // --- markers and clamps ---

    [Fact]
    public void Attachment_only_message_still_says_something() =>
        Assert.Equal("[attachment]", Text("", attachments: true));

    [Fact]
    public void Marker_flags_append_in_order() =>
        Assert.Equal("look [attachment] [embed] [sticker]",
            Text("look", attachments: true, embeds: true, stickers: true));

    [Fact]
    public void Long_text_clamps_to_200_with_ellipsis()
    {
        var result = Text(new string('x', 300));

        Assert.Equal(Stage2Sanitizer.MaxTextLength, result.Length);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void Empty_content_with_no_markers_sanitizes_to_empty() =>
        Assert.Equal(string.Empty, Text("  \n  "));

    // --- author ---

    [Fact]
    public void Author_prefers_global_name() =>
        Assert.Equal("Zed", Stage2Sanitizer.SanitizeAuthor("Zed", "zed_the_user"));

    [Fact]
    public void Author_falls_back_to_username() =>
        Assert.Equal("zed_the_user", Stage2Sanitizer.SanitizeAuthor(null, "zed_the_user"));

    [Fact]
    public void Author_that_sanitizes_away_becomes_discord() =>
        Assert.Equal("discord", Stage2Sanitizer.SanitizeAuthor("", null));

    [Fact]
    public void Emoji_only_author_becomes_a_question_mark() =>
        Assert.Equal("?", Stage2Sanitizer.SanitizeAuthor("\U0001F600", null));

    [Fact]
    public void Author_cannot_carry_sender_lookalike_characters() =>
        Assert.Equal("Kaelen Discord Bob", Stage2Sanitizer.SanitizeAuthor("Kaelen: [Discord] Bob:", null));

    [Fact]
    public void Author_clamps_to_32()
    {
        var result = Stage2Sanitizer.SanitizeAuthor(new string('n', 60), null);

        Assert.Equal(Stage2Sanitizer.MaxAuthorLength, result.Length);
    }

    [Fact]
    public void Composed_line_fits_the_pinned_244_char_bound()
    {
        var author = Stage2Sanitizer.SanitizeAuthor(new string('a', 100), null);
        var text = Text(new string('x', 500));

        Assert.True(Stage2Queue.ComposeInjectedBody(author, text).Length <= 244);
    }
}
