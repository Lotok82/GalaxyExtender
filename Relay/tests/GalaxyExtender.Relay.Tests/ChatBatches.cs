namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Builders for request payloads. Deliberately anonymous objects serialised as JSON rather than the
/// production DTOs — the tests then exercise the same wire format the C++ extension will send,
/// including field naming, instead of round-tripping through types that cannot be wrong.
/// </summary>
public static class ChatBatches
{
    public static object Valid(params string[] texts)
    {
        var lines = (texts.Length == 0 ? ["Kaelen: anyone up for a Krayt run?"] : texts)
            .Select((text, index) => new
            {
                text,
                occurrence = 1,
                clientSeq = 400 + index
            })
            .ToArray();

        return new
        {
            batchId = Guid.NewGuid().ToString(),
            client = new { id = "kaelen", character = "Kaelen", galaxy = "Basilisk" },
            lines
        };
    }

    public static object WithLines(object[] lines) => new
    {
        batchId = Guid.NewGuid().ToString(),
        client = new { id = "kaelen", character = "Kaelen", galaxy = "Basilisk" },
        lines
    };

    public static object Line(string text, int occurrence = 1, long clientSeq = 1) => new
    {
        text,
        occurrence,
        clientSeq
    };
}
