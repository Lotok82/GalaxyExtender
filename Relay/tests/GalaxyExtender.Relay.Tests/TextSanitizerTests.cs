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

    [Fact]
    public void ForDiscord_neutralises_mass_mentions()
    {
        var result = TextSanitizer.ForDiscord("hey @everyone and @HERE", 512);

        Assert.DoesNotContain("@everyone", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@here", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@\u200Deveryone", result);
    }

    [Fact]
    public void ForDiscord_escapes_markdown()
    {
        var result = TextSanitizer.ForDiscord("`code` *bold* _under_ ~strike~ |spoil| back\\slash", 512);

        Assert.Equal(@"\`code\` \*bold\* \_under\_ \~strike\~ \|spoil\| back\\slash", result);
    }

    [Fact]
    public void ForDiscord_escapes_blockquote_only_at_line_start()
    {
        Assert.Equal(@"\> quoted a > b", TextSanitizer.ForDiscord("> quoted a > b", 512));
    }

    [Fact]
    public void ForDiscord_clamps_and_never_ends_on_a_lone_backslash()
    {
        var result = TextSanitizer.ForDiscord(new string('*', 300), 512);

        Assert.True(result.Length <= 512);
        Assert.False(result.EndsWith('\\') && !result.EndsWith(@"\\") && !result.EndsWith(@"\*"),
            "clamped text must not end with a dangling escape backslash");
    }

    /// <summary>The plan's named case: a 5000-char payload survives, split across embeds.</summary>
    [Fact]
    public void BuildDescriptions_splits_at_the_embed_limit_without_splitting_lines()
    {
        var lines = Enumerable.Range(0, 12).Select(i => new string((char)('a' + i), 500)).ToList();

        var chunks = TextSanitizer.BuildDescriptions(lines);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= TextSanitizer.MaxDescriptionLength));
        Assert.Equal(lines.Count, chunks.Sum(chunk => chunk.LineCount));

        // No line was cut: every chunk is whole lines joined by newlines.
        Assert.All(chunks, chunk =>
            Assert.All(chunk.Text.Split('\n'), line => Assert.Equal(500, line.Length)));
    }

    [Fact]
    public void BuildDescriptions_keeps_a_short_batch_in_one_chunk()
    {
        var chunks = TextSanitizer.BuildDescriptions(["one", "two"]);

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
