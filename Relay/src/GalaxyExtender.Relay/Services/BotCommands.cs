using System.Text;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Recognises what the bridge bot answers to when someone mentions it in the bridge channel —
/// <c>@GalaxyExtender status</c> and friends (R11).
///
/// The real commands are matched by word; every other mention is the magic eight ball
/// (<see cref="BotCommand.EightBall"/>), a deliberate toy: mentioning the bot is a conversational
/// act, and a stock one-liner back is friendlier than silence. That makes ANY mention bot
/// conversation rather than guild-bound chat, which is why the Stage 2 reader suppresses every
/// parsed command from injection, this one included. A bare mention with no words is someone
/// asking what the bot does, so it gets the help line rather than a fortune.
/// </summary>
public static class BotCommands
{
    public enum BotCommand
    {
        /// <summary>Not addressed to us at all. No reply.</summary>
        None,

        /// <summary>Is anyone running the extension, and how many.</summary>
        Status,

        /// <summary>What can I ask you.</summary>
        Help,

        /// <summary>Anything else the bot is asked: one of a hundred stock answers.</summary>
        EightBall
    }

    /// <summary>
    /// True when <paramref name="message"/> addresses the bot directly: Discord's own
    /// <c>mentions</c> array names it (which also covers a reply-with-mention), or its
    /// <c>&lt;@id&gt;</c> / <c>&lt;@!id&gt;</c> token appears in the content. Both are checked
    /// because the mentions array is the reliable signal but is not guaranteed to be present on
    /// every payload shape.
    /// </summary>
    public static bool Mentions(DiscordMessage message, string botUserId)
    {
        if (string.IsNullOrEmpty(botUserId))
        {
            return false;
        }

        if (message.MentionNames.ContainsKey(botUserId))
        {
            return true;
        }

        var content = message.Content;

        return content is not null &&
               (content.Contains($"<@{botUserId}>", StringComparison.Ordinal) ||
                content.Contains($"<@!{botUserId}>", StringComparison.Ordinal));
    }

    /// <summary>
    /// Maps the words around the mention to a command. Word-anywhere matching rather than
    /// "first word must be the verb", because <c>@bot status</c>, <c>@bot what's the status?</c> and
    /// <c>hey @bot status</c> are all the same question and Discord clients put the mention wherever
    /// the typist did. Never returns <see cref="BotCommand.None"/>: a mention that matches nothing
    /// real is a question for the eight ball.
    /// </summary>
    public static BotCommand Parse(string? content)
    {
        var words = Words(content ?? string.Empty);

        if (words.Count == 0)
        {
            return BotCommand.Help;
        }

        if (words.Contains("status") || words.Contains("online") || words.Contains("who"))
        {
            return BotCommand.Status;
        }

        return words.Contains("help") || words.Contains("commands")
            ? BotCommand.Help
            : BotCommand.EightBall;
    }

    /// <summary>
    /// Lower-cased alphanumeric words, with Discord's angle-bracket tokens
    /// (<c>&lt;@id&gt;</c>, <c>&lt;#id&gt;</c>, <c>&lt;:emoji:id&gt;</c>) removed whole so their
    /// innards never look like words.
    /// </summary>
    private static HashSet<string> Words(string content)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        var i = 0;

        while (i < content.Length)
        {
            if (content[i] == '<')
            {
                var close = content.IndexOf('>', i + 1);

                // Tokens are short; a far-away '>' means this '<' is just punctuation.
                if (close >= 0 && close - i <= 64)
                {
                    i = close + 1;
                    continue;
                }
            }

            if (char.IsLetterOrDigit(content[i]))
            {
                word.Append(char.ToLowerInvariant(content[i]));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }

            i++;
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
