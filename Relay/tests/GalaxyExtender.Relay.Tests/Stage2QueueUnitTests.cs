using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The two pure pieces of R7: recognising a relayed line as a bridged-message echo, and matching
/// an ack body against a claim (exact first, then mask-tolerant — the profanity-filter case).
/// </summary>
public sealed class Stage2QueueUnitTests
{
    // --- TryExtractMarkedBody ---

    [Theory]
    [InlineData("[GuildChat] Kaelen: [Discord] Bob: hi", "[Discord] Bob: hi")]
    [InlineData("Kaelen: [Discord] Bob: hi", "[Discord] Bob: hi")]
    [InlineData("[GuildChat] Kae len: [Discord] Bob: hi", "[Discord] Bob: hi")]
    [InlineData("[Discord] Bob: hi", "[Discord] Bob: hi")]
    [InlineData("[GuildChat] [Discord] Bob: hi", "[Discord] Bob: hi")]
    public void Marked_lines_yield_the_body_from_the_marker(string line, string expectedBody)
    {
        Assert.True(Stage2Queue.TryExtractMarkedBody(line, out var body));
        Assert.Equal(expectedBody, body);
    }

    [Theory]
    [InlineData("[GuildChat] Kaelen: check this [Discord] thing")]   // marker mid-sentence
    [InlineData("[GuildChat] Kaelen: see: [Discord] x")]             // second colon in prefix
    [InlineData("[GuildChat] Kaelen: hello everyone")]               // no marker at all
    [InlineData("[GuildChat] AbsurdlyLongSenderNameNobodyCouldActuallyHaveInTheGame: [Discord] x")]
    [InlineData("")]
    public void Ordinary_guild_lines_are_not_marked(string line) =>
        Assert.False(Stage2Queue.TryExtractMarkedBody(line, out _));

    // --- BodiesMatch (R7 decision: exact, then mask-tolerant) ---

    [Fact]
    public void Exact_body_matches() =>
        Assert.True(Stage2Queue.BodiesMatch("[Discord] Bob: hi", "[Discord] Bob: hi"));

    [Fact]
    public void Profanity_masked_body_matches_when_lengths_agree() =>
        Assert.True(Stage2Queue.BodiesMatch("[Discord] Bob: heck", "[Discord] Bob: ****"));

    [Fact]
    public void Masked_author_and_text_still_match() =>
        Assert.True(Stage2Queue.BodiesMatch("[Discord] Bob: heck", "[Discord] ***: ****"));

    [Fact]
    public void Masked_body_with_different_length_does_not_match() =>
        Assert.False(Stage2Queue.BodiesMatch("[Discord] Bob: heck", "[Discord] Bob: *****"));

    [Fact]
    public void Different_clean_text_does_not_match() =>
        Assert.False(Stage2Queue.BodiesMatch("[Discord] Bob: hi", "[Discord] Bob: ho"));

    [Fact]
    public void Mask_characters_never_match_a_longer_claim() =>
        Assert.False(Stage2Queue.BodiesMatch("[Discord] Bob: hi there", "[Discord] Bob: **"));
}
