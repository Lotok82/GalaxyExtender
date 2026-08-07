using System.Text.Json;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// One Discord message as the relay cares about it. Shared by the Stage 2 read path
/// (<see cref="DiscordReader"/>) and the bot-command scan (<see cref="BotCommandScanner"/>), which
/// read the same channel from their own independent cursors.
/// </summary>
public sealed record DiscordMessage(
    string Id,
    ulong NumericId,
    string? Content,
    string? AuthorId,
    string? GlobalName,
    string? Username,
    bool FromBotOrWebhook,
    bool HasAttachments,
    bool HasEmbeds,
    bool HasStickers,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> MentionNames);

/// <summary>
/// Parses Discord's channel-messages array. Deliberately tolerant of missing or unexpected fields:
/// anything the relay cannot make sense of is skipped rather than throwing, because one odd message
/// in a page must not cost the whole fetch. A non-array root IS an error — that means the response
/// was not the payload we asked for at all.
/// </summary>
public static class DiscordMessageParser
{
    public static List<DiscordMessage> Parse(string json)
    {
        var messages = new List<DiscordMessage>();

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected a message array");
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idProperty.GetString()!;

            if (!ulong.TryParse(id, out var numericId))
            {
                continue;
            }

            var fromBotOrWebhook = element.TryGetProperty("webhook_id", out _);
            string? authorId = null;
            string? globalName = null;
            string? username = null;

            if (element.TryGetProperty("author", out var author) &&
                author.ValueKind == JsonValueKind.Object)
            {
                if (author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
                {
                    fromBotOrWebhook = true;
                }

                authorId = ReadString(author, "id");
                globalName = ReadString(author, "global_name");
                username = ReadString(author, "username");
            }

            var timestamp = DateTimeOffset.UtcNow;

            if (element.TryGetProperty("timestamp", out var ts) &&
                ts.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ts.GetString(), out var parsed))
            {
                timestamp = parsed;
            }

            messages.Add(new DiscordMessage(
                id,
                numericId,
                ReadString(element, "content"),
                authorId,
                globalName,
                username,
                fromBotOrWebhook,
                HasItems(element, "attachments"),
                HasItems(element, "embeds"),
                HasItems(element, "sticker_items"),
                timestamp,
                ParseMentions(element)));
        }

        return messages;
    }

    /// <summary>
    /// The message's own <c>mentions</c> array as id → display name. Doubles as the mention TEST
    /// for the command scan (does it contain the bot's id?) and as the token lookup the Stage 2
    /// sanitizer uses to resolve <c>&lt;@id&gt;</c> without extra API calls.
    /// </summary>
    private static Dictionary<string, string> ParseMentions(JsonElement element)
    {
        var mentionNames = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!element.TryGetProperty("mentions", out var mentions) ||
            mentions.ValueKind != JsonValueKind.Array)
        {
            return mentionNames;
        }

        foreach (var mention in mentions.EnumerateArray())
        {
            if (mention.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(mention, "id");

            if (id is null)
            {
                continue;
            }

            var globalName = ReadString(mention, "global_name");

            mentionNames[id] = !string.IsNullOrWhiteSpace(globalName)
                ? globalName
                : ReadString(mention, "username") ?? "someone";
        }

        return mentionNames;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasItems(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() > 0;
}
