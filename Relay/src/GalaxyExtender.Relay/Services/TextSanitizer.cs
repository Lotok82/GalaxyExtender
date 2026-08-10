using System.Text;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Text preparation for de-duplication and for Discord. Two distinct outputs:
///
/// <see cref="Normalize"/> produces the DEDUPE form — SWG escapes stripped, control characters
/// mapped to spaces, trimmed. It must stay byte-stable for identical input across clients, because
/// its hash is the cross-client dedupe key; keep every transformation deterministic and do NOT add
/// presentation concerns to it.
///
/// <see cref="ForDiscord"/> takes a normalised line to the DISPLAY form — mass-mentions
/// neutralised, Discord markdown escaped, clamped. Presentation only; never feed it to the hash.
/// It needs to know which kind of message the text is bound for: see <see cref="DiscordTarget"/>.
/// </summary>
public static class TextSanitizer
{
    /// <summary>Discord's hard limit on an embed description.</summary>
    public const int MaxDescriptionLength = 4096;

    /// <summary>Discord's hard limit on a plain message's <c>content</c> — far below an embed's.</summary>
    public const int MaxContentLength = 2000;

    /// <summary>
    /// Mirrors the extension's cleanChatText: strip <c>\#RRGGBB</c>, <c>\#.</c> and <c>\>NNN</c>,
    /// map C0/DEL to spaces, trim. The extension already did this — doing it again here is
    /// defensive, and keeps the dedupe key well-defined for any future client.
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];

                if (next == '#')
                {
                    if (i + 2 < text.Length && text[i + 2] == '.')
                    {
                        i += 3; // \#. — colour reset
                        continue;
                    }

                    if (i + 7 < text.Length && IsHex(text, i + 2, 6))
                    {
                        i += 8; // \#RRGGBB
                        continue;
                    }
                }
                else if (next == '>' && i + 4 < text.Length &&
                         char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3]) &&
                         char.IsAsciiDigit(text[i + 4]))
                {
                    i += 5; // \>NNN — indent
                    continue;
                }
            }

            builder.Append(c < 0x20 || c == 0x7F ? ' ' : c);
            i++;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Presentation pass, in the order the plan specifies:
    /// 1. neutralise <c>@everyone</c>/<c>@here</c> with a zero-width joiner (belt) — the webhook
    ///    payload also carries <c>allowed_mentions: {parse: []}</c> (braces);
    /// 2. escape Discord markdown so player text renders literally;
    /// 3. clamp to <paramref name="maxLength"/> characters.
    ///
    /// <paramref name="target"/> is not a style preference — it decides whether bracket escaping
    /// happens, and the two destinations made different safety/legibility trade-offs (see
    /// <see cref="DiscordTarget"/>). There is no default: a call site that has not thought about
    /// it is exactly the one that gets it wrong.
    /// </summary>
    public static string ForDiscord(string normalized, int maxLength, DiscordTarget target)
    {
        var text = normalized
            .Replace("@everyone", "@\u200Deveryone", StringComparison.OrdinalIgnoreCase)
            .Replace("@here", "@\u200Dhere", StringComparison.OrdinalIgnoreCase);

        var builder = new StringBuilder(text.Length + 16);
        var atLineStart = true;

        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '_':
                case '~':
                case '|':
                    builder.Append('\\').Append(c);
                    break;

                // [text](url) renders as a masked hyperlink in an embed description AND — because
                // the sender is a webhook, not a human — in plain message content too. Escaping is
                // kept mandatory for embeds and deliberately skipped for plain messages: see the
                // DiscordTarget doc for the accepted risk and why.
                case '[' or ']' when target == DiscordTarget.Embed:
                    builder.Append('\\').Append(c);
                    break;

                case '>' when atLineStart:
                    builder.Append('\\').Append(c); // block-quote only bites at line start
                    break;

                default:
                    builder.Append(c);
                    break;
            }

            atLineStart = c == '\n';
        }

        if (builder.Length > maxLength)
        {
            builder.Length = maxLength;

            // Never end on the escaping backslash we just added.
            if (builder[^1] == '\\')
            {
                builder.Length--;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Joins display lines with newlines and splits the result into chunks that each fit in one
    /// Discord message. Lines are never split across chunks (each line is clamped well below the
    /// limit). Returns each chunk with the number of chat lines it carries, so the caller can
    /// report accepted/queued counts per webhook POST.
    ///
    /// <paramref name="maxChunkLength"/> is the destination's ceiling — <see cref="MaxContentLength"/>
    /// for a plain message, <see cref="MaxDescriptionLength"/> for an embed description.
    /// </summary>
    public static List<(string Text, int LineCount)> BuildChunks(
        IReadOnlyList<string> displayLines, int maxChunkLength)
    {
        var chunks = new List<(string, int)>();
        var current = new StringBuilder();
        var count = 0;

        foreach (var line in displayLines)
        {
            var needed = line.Length + (count > 0 ? 1 : 0);

            if (count > 0 && current.Length + needed > maxChunkLength)
            {
                chunks.Add((current.ToString(), count));
                current.Clear();
                count = 0;
            }

            if (count > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
            count++;
        }

        if (count > 0)
        {
            chunks.Add((current.ToString(), count));
        }

        return chunks;
    }

    private static bool IsHex(string text, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (!char.IsAsciiHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Which kind of Discord message sanitised text is bound for; it selects the bracket policy.
///
/// An embed description renders <c>[text](url)</c> as a masked hyperlink, so the embed path
/// escapes brackets unconditionally — a disguised link there would publish under the relay's own
/// name, and escaping costs nothing visible (Discord consumes the backslashes).
///
/// IMPORTANT — plain messages are NOT the safe case they look like: masked links are rendered in
/// <c>content</c> too when the author is a webhook or bot (only human-typed messages show the
/// brackets literally). Leaving brackets unescaped therefore lets a player's <c>[text](url)</c>
/// render as a clickable masked link in guild chat. This is an ACCEPTED RISK, decided 2026-08-10:
/// the channel is a small private guild of trusted members, and escaping would put visible
/// backslashes around the game-supplied "[GuildChat]" prefix on every line. Revisit before ever
/// pointing this relay at a public or untrusted channel.
/// </summary>
public enum DiscordTarget
{
    /// <summary>An embed description. Brackets MUST be escaped.</summary>
    Embed,

    /// <summary>A message's <c>content</c>. Brackets left alone — see the accepted risk above.</summary>
    PlainMessage
}
