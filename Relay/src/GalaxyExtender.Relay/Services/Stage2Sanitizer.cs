using System.Text;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Discord → game text preparation (R5). Discord message content is untrusted input that will be
/// rendered by every guild member's client, so everything here is deny-by-default: resolve what
/// can be made readable, strip what the game cannot render, and above all remove SWG escape
/// sequences — the Core3 server does not strip <c>\#</c> colour codes itself (S4 finding), so a
/// Discord user could otherwise inject colour/format codes into the room.
///
/// Output is what the extension injects VERBATIM as <c>[Discord] &lt;author&gt;: &lt;text&gt;</c>;
/// the clamps here (author ≤ 32, text ≤ 200) keep that full line ≤ 244 chars, per the pinned
/// contract in README.md.
/// </summary>
public static class Stage2Sanitizer
{
    /// <summary>Contract clamp, not a tunable: README pins author ≤ 32.</summary>
    public const int MaxAuthorLength = 32;

    /// <summary>Contract clamp, not a tunable: README pins text ≤ 200.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Sentinel from <see cref="MapChar"/>: character vanishes without a trace.</summary>
    private const char Drop = '￿';

    /// <summary>Sentinel from <see cref="MapChar"/>: character becomes one '?' per run.</summary>
    private const char Unrenderable = '\0';

    /// <summary>
    /// The injected author prefix. Discord's display name (<c>global_name</c>) preferred,
    /// username fallback (R2 finding); sanitized like text, plus <c>:</c>/<c>[</c>/<c>]</c>
    /// removed so the author cannot make the injected body look like a different sender or a
    /// nested marker. Never empty — an author that sanitizes away becomes "discord".
    /// </summary>
    public static string SanitizeAuthor(string? globalName, string? username)
    {
        var name = !string.IsNullOrWhiteSpace(globalName) ? globalName
            : !string.IsNullOrWhiteSpace(username) ? username
            : string.Empty;

        var cleaned = Clean(name, MaxAuthorLength, stripSenderLookalikes: true);

        return cleaned.Length == 0 ? "discord" : cleaned;
    }

    /// <summary>
    /// The injected message text. <paramref name="mentionNames"/> maps user id → display name
    /// from the message's own <c>mentions</c> array, so <c>&lt;@id&gt;</c> tokens resolve without
    /// extra API calls. The marker flags append <c>[attachment]</c>/<c>[embed]</c>/<c>[sticker]</c>
    /// so an image-only message still says something in game. Empty result = nothing worth
    /// injecting; the caller skips the message.
    /// </summary>
    public static string SanitizeText(
        string? content,
        IReadOnlyDictionary<string, string> mentionNames,
        bool hasAttachments,
        bool hasEmbeds,
        bool hasStickers)
    {
        var resolved = ResolveTokens(content ?? string.Empty, mentionNames);

        if (hasAttachments)
        {
            resolved += " [attachment]";
        }

        if (hasEmbeds)
        {
            resolved += " [embed]";
        }

        if (hasStickers)
        {
            resolved += " [sticker]";
        }

        return Clean(resolved, MaxTextLength, stripSenderLookalikes: false);
    }

    /// <summary>
    /// Rewrites Discord's angle-bracket tokens to plain text: <c>&lt;@id&gt;</c>/<c>&lt;@!id&gt;</c>
    /// to <c>@name</c>, <c>&lt;@&amp;id&gt;</c> to <c>@role</c>, <c>&lt;#id&gt;</c> to
    /// <c>#channel</c>, <c>&lt;:name:id&gt;</c>/<c>&lt;a:name:id&gt;</c> to <c>:name:</c>, and
    /// <c>&lt;t:…&gt;</c> to <c>[time]</c>. Anything unrecognised passes through literally.
    /// </summary>
    private static string ResolveTokens(string content, IReadOnlyDictionary<string, string> mentionNames)
    {
        var builder = new StringBuilder(content.Length);
        var i = 0;

        while (i < content.Length)
        {
            if (content[i] != '<')
            {
                builder.Append(content[i]);
                i++;
                continue;
            }

            var close = content.IndexOf('>', i + 1);

            // Tokens are short; a far-away '>' means this '<' is just text.
            if (close < 0 || close - i > 64)
            {
                builder.Append(content[i]);
                i++;
                continue;
            }

            var token = content.Substring(i + 1, close - i - 1);

            if (TryResolveToken(token, mentionNames, out var replacement))
            {
                builder.Append(replacement);
                i = close + 1;
                continue;
            }

            builder.Append(content[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool TryResolveToken(
        string token, IReadOnlyDictionary<string, string> mentionNames, out string replacement)
    {
        replacement = string.Empty;

        if (token.Length == 0)
        {
            return false;
        }

        if (token[0] == '@')
        {
            if (token.Length > 1 && token[1] == '&' && AllDigits(token, 2))
            {
                replacement = "@role";
                return true;
            }

            var idStart = token.Length > 1 && token[1] == '!' ? 2 : 1;

            if (AllDigits(token, idStart))
            {
                var id = token[idStart..];
                replacement = mentionNames.TryGetValue(id, out var name) ? $"@{name}" : "@someone";
                return true;
            }

            return false;
        }

        if (token[0] == '#' && AllDigits(token, 1))
        {
            replacement = "#channel";
            return true;
        }

        if (token.StartsWith("t:", StringComparison.Ordinal))
        {
            replacement = "[time]";
            return true;
        }

        // Custom emoji: ":name:id" or animated "a:name:id".
        var emoji = token;

        if (emoji.StartsWith("a:", StringComparison.Ordinal))
        {
            emoji = emoji[2..];
        }
        else if (emoji.Length > 0 && emoji[0] == ':')
        {
            emoji = emoji[1..];
        }
        else
        {
            return false;
        }

        var lastColon = emoji.LastIndexOf(':');

        if (lastColon > 0 && lastColon + 1 < emoji.Length && AllDigits(emoji, lastColon + 1))
        {
            var name = emoji[..lastColon];

            if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                replacement = $":{name}:";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The character-level pass: SWG escapes out (via <see cref="TextSanitizer.Normalize"/> —
    /// same rules as Stage 1, so nothing the game treats as markup survives), newlines and
    /// control characters collapsed into single spaces, characters the game font cannot render
    /// folded to ASCII lookalikes or <c>?</c>, and the result clamped with a visible ellipsis.
    /// </summary>
    private static string Clean(string text, int maxLength, bool stripSenderLookalikes)
    {
        var stripped = TextSanitizer.Normalize(text);

        var builder = new StringBuilder(stripped.Length);
        var lastWasSpace = true;   // leading spaces collapse away
        var lastWasReplacement = false;

        foreach (var c in stripped)
        {
            var mapped = MapChar(c);

            if (mapped == Drop)
            {
                continue;
            }

            if (stripSenderLookalikes && mapped is ':' or '[' or ']')
            {
                continue;
            }

            if (mapped == ' ')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                lastWasReplacement = false;
                continue;
            }

            if (mapped == Unrenderable)
            {
                // One '?' per RUN, so a string of emoji reads "?" rather than "??????".
                if (!lastWasReplacement)
                {
                    builder.Append('?');
                    lastWasReplacement = true;
                    lastWasSpace = false;
                }

                continue;
            }

            builder.Append(mapped);
            lastWasSpace = false;
            lastWasReplacement = false;
        }

        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        if (builder.Length > maxLength)
        {
            builder.Length = maxLength - 3;

            // Never end on a dangling space before the ellipsis.
            while (builder.Length > 0 && builder[^1] == ' ')
            {
                builder.Length--;
            }

            builder.Append("...");
        }

        return builder.ToString();
    }

    /// <summary>
    /// ' ' = whitespace, <see cref="Unrenderable"/> = becomes '?', <see cref="Drop"/> = vanishes,
    /// otherwise the character to keep. ASCII and Latin-1 pass (the client fonts cover them);
    /// common typography folds to ASCII; zero-width and combining characters vanish rather than
    /// becoming '?' noise in copy-pasted text.
    /// </summary>
    private static char MapChar(char c)
    {
        switch (c)
        {
            case '‘' or '’' or '‚' or '′':   // ‘ ’ ‚ ′
                return '\'';
            case '“' or '”' or '„' or '″':   // “ ” „ ″
                return '"';
            case '–' or '—' or '―':               // – — ―
                return '-';
            case '…':                                       // … (runs of dots read fine)
                return '.';
            case ' ' or ' ' or ' ' or '　':   // no-break/figure/narrow/ideographic space
                return ' ';
        }

        if (c >= ' ' && c <= ' ')   // en/em/thin/hair spaces
        {
            return ' ';
        }

        if (c is '​' or '‌' or '‍' or '‎' or '‏' or '﻿')
        {
            return Drop;   // zero-width and directional marks
        }

        if (c >= '̀' && c <= 'ͯ')
        {
            return Drop;   // combining marks: the base letter already carries the meaning
        }

        if (c >= 0x20 && c <= 0x7E)
        {
            return c;
        }

        // Latin-1 supplement (é, ü, ¿ …) — the client font covers these.
        if (c >= 0xA1 && c <= 0xFF)
        {
            return c;
        }

        return Unrenderable;
    }

    private static bool AllDigits(string text, int start)
    {
        if (start >= text.Length)
        {
            return false;
        }

        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
