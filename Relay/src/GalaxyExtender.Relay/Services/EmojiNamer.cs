using System.Text;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Unicode emoji → readable game text. The SWG client font cannot render emoji, so before the
/// character-level pass in <see cref="Stage2Sanitizer"/> flattens them to <c>?</c>, this pass
/// rewrites each emoji cluster to something meaningful: a Discord-style shortcode for the common
/// ones (<c>😂</c> → <c>:joy:</c> — the same shape custom-server emoji already reduce to), a
/// <c>[flag]</c> marker for regional-indicator pairs, and <c>[emoji]</c> for anything unnamed.
///
/// Only emoji are touched. Other unrenderable text (CJK, Cyrillic, …) is deliberately left for
/// the <c>?</c> fold — labelling someone's actual words <c>[emoji]</c> would misdescribe them.
/// </summary>
public static class EmojiNamer
{
    /// <summary>
    /// Replaces every emoji cluster in <paramref name="text"/>. Identical adjacent clusters
    /// collapse to a single replacement (so <c>😂😂😂</c> reads <c>:joy:</c>, mirroring the
    /// sanitizer's one-<c>?</c>-per-run rule); distinct adjacent clusters are space-separated so
    /// their names do not run together.
    /// </summary>
    public static string Replace(string text)
    {
        // Fast path: emoji bases, their modifiers, and astral surrogates all sit at or above
        // U+200D, so a string with nothing that high cannot contain an emoji.
        var hasCandidate = false;

        foreach (var c in text)
        {
            if (c >= '\u200D')
            {
                hasCandidate = true;
                break;
            }
        }

        if (!hasCandidate)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        string? previousCluster = null;
        var i = 0;

        while (i < text.Length)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune))
            {
                builder.Append(text[i]);   // lone surrogate — pass through, MapChar folds it
                i++;
                previousCluster = null;
                continue;
            }

            if (!IsEmojiBase(rune.Value))
            {
                builder.Append(text, i, rune.Utf16SequenceLength);
                i += rune.Utf16SequenceLength;
                previousCluster = null;
                continue;
            }

            var start = i;
            i = ConsumeCluster(text, start);
            var cluster = text[start..i];

            if (cluster == previousCluster)
            {
                continue;   // identical run collapses to one name
            }

            if (previousCluster is not null)
            {
                builder.Append(' ');   // distinct neighbours: ":joy: :thumbsup:", not ":joy::thumbsup:"
            }

            builder.Append(NameFor(cluster));
            previousCluster = cluster;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The reverse direction (game → Discord): rewrites known <c>:name:</c> shortcodes to their
    /// Unicode emoji, so a player typing <c>:joy:</c> in guild chat — or quoting back what
    /// <see cref="Replace"/> produced — shows 😂 in Discord. Discord itself only converts
    /// shortcodes in its own input box, never in API-posted content, so the relay must do it.
    /// Unknown shortcodes pass through literally, exactly as Discord would show them.
    /// </summary>
    public static string RestoreEmoji(string text)
    {
        var builder = default(StringBuilder);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c != ':')
            {
                builder?.Append(c);
                i++;
                continue;
            }

            var close = text.IndexOf(':', i + 1);

            if (close > i + 1 && close - i - 1 <= MaxShortcodeLength &&
                IsShortcodeName(text, i + 1, close) &&
                EmojiByName.TryGetValue(text[(i + 1)..close], out var emoji))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, i);
                builder.Append(emoji);
                i = close + 1;
                continue;
            }

            builder?.Append(c);
            i++;
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// Generous bound over the longest name in the table; only exists so a colon-heavy line
    /// (timestamps, class:method notation) never scans far for a closing colon.
    /// </summary>
    private const int MaxShortcodeLength = 32;

    private static bool IsShortcodeName(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsAsciiLetterOrDigit(text[i]) && text[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Consumes one emoji cluster starting at <paramref name="start"/> (which the caller has
    /// verified holds an emoji base rune): the base plus any skin tones, variation selectors,
    /// keycap marks, and ZWJ-joined continuations. Regional-indicator letters pair up into flags.
    /// Returns the index just past the cluster.
    /// </summary>
    private static int ConsumeCluster(string text, int start)
    {
        Rune.TryGetRuneAt(text, start, out var baseRune);
        var i = start + baseRune.Utf16SequenceLength;

        if (IsRegionalIndicator(baseRune.Value) &&
            Rune.TryGetRuneAt(text, i, out var second) && IsRegionalIndicator(second.Value))
        {
            return i + second.Utf16SequenceLength;
        }

        while (i < text.Length && Rune.TryGetRuneAt(text, i, out var next))
        {
            if (IsClusterExtension(next.Value))
            {
                i += next.Utf16SequenceLength;
                continue;
            }

            if (next.Value == 0x200D && Rune.TryGetRuneAt(text, i + 1, out var joined) &&
                IsEmojiBase(joined.Value))
            {
                i += 1 + joined.Utf16SequenceLength;   // ZWJ is one UTF-16 unit
                continue;
            }

            break;
        }

        return i;
    }

    /// <summary>
    /// The replacement for one cluster: named form first (exact match after stripping variation
    /// selectors and skin tones, then the base rune alone so <c>👍🏽</c> and <c>🤷‍♂️</c> fold to
    /// their plain names), then the generic markers.
    /// </summary>
    private static string NameFor(string cluster)
    {
        var normalized = StripPresentationRunes(cluster);

        if (Names.TryGetValue(normalized, out var name))
        {
            return $":{name}:";
        }

        // A tag sequence (base + U+E0020..E007F spelling out a region) is a flag by construction
        // — the UK subdivision flags are the named ones above; anything else at least says what
        // it was instead of leaking invisible tag runes into the '?' fold.
        if (ContainsTagCharacter(normalized))
        {
            return "[flag]";
        }

        if (Rune.TryGetRuneAt(normalized, 0, out var baseRune))
        {
            if (Names.TryGetValue(baseRune.ToString(), out name))
            {
                return $":{name}:";
            }

            if (IsRegionalIndicator(baseRune.Value))
            {
                return "[flag]";
            }
        }

        return "[emoji]";
    }

    private static string StripPresentationRunes(string cluster)
    {
        var builder = new StringBuilder(cluster.Length);

        foreach (var rune in cluster.EnumerateRunes())
        {
            if (rune.Value is not (0xFE0F or 0x20E3) && !IsSkinTone(rune.Value))
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Runes that start an emoji cluster. Ranges are drawn a little generously inside the emoji
    /// planes (an unnamed pictograph becomes <c>[emoji]</c>, which is at worst harmless), but stop
    /// short of blocks that carry ordinary text or technical symbols — those must keep falling to
    /// <c>?</c> rather than be mislabelled. The blocks below are mixed, so
    /// <see cref="IsTextSymbol"/> carves the text glyphs back out of them.
    /// </summary>
    private static bool IsEmojiBase(int value) => !IsTextSymbol(value) && value is
        (>= 0x1F000 and <= 0x1FAFF) or   // pictographs, smileys, transport, flags, extended-A
        (>= 0x2600 and <= 0x27BF) or     // misc symbols and dingbats (☀ ⚔ ✅ ❌ …)
        (>= 0x2B00 and <= 0x2BFF) or     // arrows and shapes incl. ⭐
        (>= 0x23E9 and <= 0x23FA) or     // media-control symbols (⏩ … ⏺)
        (>= 0x2194 and <= 0x2199) or     // emoji-presentation arrows (↔ … ↙)
        0x21A9 or 0x21AA or              // ↩ ↪
        0x25B6 or 0x25C0 or              // ▶ ◀
        0x231A or 0x231B or              // ⌚ ⌛
        0x203C or 0x2049 or              // ‼ ⁉
        0x2122 or 0x2139 or              // ™ ℹ
        0x2934 or 0x2935 or              // ⤴ ⤵
        0x3030 or 0x303D or              // 〰 〽
        0x3297 or 0x3299;                // ㊗ ㊙

    /// <summary>
    /// Ordinary text glyphs living inside the mixed blocks above — someone's ✓ or ♪ is typed
    /// text, not a picture, and labelling it <c>[emoji]</c> would misdescribe it. These keep
    /// falling to the <c>?</c> fold like any other unrenderable text. Their emoji-presentation
    /// neighbours (✔ ✖ ➡ ➰ and the black card suits) stay emoji bases.
    /// </summary>
    private static bool IsTextSymbol(int value) => value is
        (>= 0x2654 and <= 0x265E) or                             // chess pieces
        0x2661 or 0x2662 or 0x2664 or 0x2667 or                  // white card suits ♡ ♢ ♤ ♧
        (>= 0x2669 and <= 0x266F) or                             // music notation ♩ ♪ ♫ ♬ ♭ ♮ ♯
        0x2713 or 0x2715 or 0x2717 or 0x2718 or                  // light check/ballot marks ✓ ✕ ✗ ✘
        (>= 0x275B and <= 0x2760) or                             // ornamental quotes and daggers
        ((>= 0x2794 and <= 0x27BE) and not (0x27A1 or 0x27B0));  // dingbat arrows (➤ …)

    private static bool IsClusterExtension(int value) =>
        value is 0xFE0F or 0x20E3 || IsSkinTone(value) || IsTagCharacter(value);

    /// <summary>U+E0020–E007F: the invisible spelling inside a tag-sequence flag (🏴󠁧󠁢󠁳󠁣󠁴󠁿 …).</summary>
    private static bool IsTagCharacter(int value) => value is >= 0xE0020 and <= 0xE007F;

    private static bool ContainsTagCharacter(string cluster)
    {
        foreach (var rune in cluster.EnumerateRunes())
        {
            if (IsTagCharacter(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSkinTone(int value) => value is >= 0x1F3FB and <= 0x1F3FF;

    private static bool IsRegionalIndicator(int value) => value is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>
    /// Discord shortcodes for the emoji guild members actually type, keyed by the cluster with
    /// variation selectors and skin tones stripped. Names match what Discord's own picker calls
    /// them, so the in-game <c>:joy:</c> reads the same as the Discord habit that produced it.
    /// Anything absent here still says <c>[emoji]</c> — extend freely, this is data, not logic.
    /// </summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // Smileys
        ["\U0001F600"] = "grinning",
        ["\U0001F601"] = "grin",
        ["\U0001F602"] = "joy",
        ["\U0001F923"] = "rofl",
        ["\U0001F603"] = "smiley",
        ["\U0001F604"] = "smile",
        ["\U0001F605"] = "sweat_smile",
        ["\U0001F606"] = "laughing",
        ["\U0001F609"] = "wink",
        ["\U0001F60A"] = "blush",
        ["\U0001F60B"] = "yum",
        ["\U0001F60E"] = "sunglasses",
        ["\U0001F60D"] = "heart_eyes",
        ["\U0001F618"] = "kissing_heart",
        ["\U0001F642"] = "slight_smile",
        ["\U0001F643"] = "upside_down",
        ["\U0001F610"] = "neutral_face",
        ["\U0001F611"] = "expressionless",
        ["\U0001F634"] = "sleeping",
        ["\U0001F62D"] = "sob",
        ["\U0001F622"] = "cry",
        ["\U0001F624"] = "triumph",
        ["\U0001F620"] = "angry",
        ["\U0001F621"] = "rage",
        ["\U0001F914"] = "thinking",
        ["\U0001F928"] = "raised_eyebrow",
        ["\U0001F60F"] = "smirk",
        ["\U0001F62C"] = "grimacing",
        ["\U0001F644"] = "rolling_eyes",
        ["\U0001F633"] = "flushed",
        ["\U0001F97A"] = "pleading_face",
        ["\U0001F631"] = "scream",
        ["\U0001F92F"] = "exploding_head",
        ["\U0001F973"] = "partying_face",
        ["\U0001F607"] = "innocent",
        ["\U0001F921"] = "clown",
        ["\U0001F480"] = "skull",
        ["☠"] = "skull_crossbones",
        ["\U0001F47B"] = "ghost",
        ["\U0001F916"] = "robot",
        ["\U0001F4A9"] = "poop",
        ["\U0001F922"] = "nauseated_face",
        ["\U0001F92E"] = "face_vomiting",
        ["\U0001F913"] = "nerd",
        ["\U0001F972"] = "smiling_face_with_tear",
        ["\U0001F61E"] = "disappointed",
        ["\U0001F614"] = "pensive",
        ["\U0001F629"] = "weary",
        ["\U0001F62B"] = "tired_face",
        ["\U0001F971"] = "yawning_face",
        ["\U0001F62E"] = "open_mouth",
        ["\U0001F632"] = "astonished",
        ["\U0001F61B"] = "stuck_out_tongue",
        ["\U0001F92A"] = "zany_face",
        ["\U0001F910"] = "zipper_mouth",
        ["\U0001F917"] = "hugging",
        ["\U0001FAE0"] = "melting_face",
        ["\U0001F637"] = "mask",
        ["\U0001F608"] = "smiling_imp",
        ["\U0001F47F"] = "imp",
        ["\U0001FAE1"] = "saluting_face",

        // People and hands
        ["\U0001F44D"] = "thumbsup",
        ["\U0001F44E"] = "thumbsdown",
        ["\U0001F44C"] = "ok_hand",
        ["✌"] = "v",
        ["\U0001F91E"] = "crossed_fingers",
        ["\U0001F44F"] = "clap",
        ["\U0001F64C"] = "raised_hands",
        ["\U0001F64F"] = "pray",
        ["\U0001F4AA"] = "muscle",
        ["\U0001F44B"] = "wave",
        ["✊"] = "fist",
        ["\U0001F44A"] = "punch",
        ["\U0001F595"] = "middle_finger",
        ["\U0001F449"] = "point_right",
        ["\U0001F448"] = "point_left",
        ["\U0001F446"] = "point_up",
        ["\U0001F447"] = "point_down",
        ["\U0001F91D"] = "handshake",
        ["\U0001F937"] = "shrug",
        ["\U0001F926"] = "facepalm",
        ["\U0001F440"] = "eyes",
        ["\U0001F9E0"] = "brain",

        // Hearts
        ["❤"] = "heart",
        ["\U0001F9E1"] = "orange_heart",
        ["\U0001F49B"] = "yellow_heart",
        ["\U0001F49A"] = "green_heart",
        ["\U0001F499"] = "blue_heart",
        ["\U0001F49C"] = "purple_heart",
        ["\U0001F5A4"] = "black_heart",
        ["\U0001F90D"] = "white_heart",
        ["\U0001F494"] = "broken_heart",
        ["\U0001F495"] = "two_hearts",
        ["\U0001F496"] = "sparkling_heart",

        // Symbols and objects
        ["\U0001F525"] = "fire",
        ["✨"] = "sparkles",
        ["⭐"] = "star",
        ["\U0001F31F"] = "star2",
        ["\U0001F4AF"] = "100",
        ["✅"] = "white_check_mark",
        ["❌"] = "x",
        ["⚠"] = "warning",
        ["❗"] = "exclamation",
        ["❓"] = "question",
        ["\U0001F4B0"] = "moneybag",
        ["\U0001F48E"] = "gem",
        ["\U0001F389"] = "tada",
        ["\U0001F38A"] = "confetti_ball",
        ["\U0001F382"] = "birthday",
        ["\U0001F370"] = "cake",
        ["\U0001F355"] = "pizza",
        ["\U0001F37A"] = "beer",
        ["\U0001F37B"] = "beers",
        ["☕"] = "coffee",
        ["\U0001F37F"] = "popcorn",
        ["\U0001F3AE"] = "video_game",
        ["\U0001F3B2"] = "game_die",
        ["\U0001F5E1"] = "dagger",
        ["⚔"] = "crossed_swords",
        ["\U0001F6E1"] = "shield",
        ["\U0001F3F9"] = "bow_and_arrow",
        ["\U0001F52B"] = "gun",
        ["\U0001F4A3"] = "bomb",
        ["\U0001F680"] = "rocket",
        ["\U0001F6F8"] = "flying_saucer",
        ["⏰"] = "alarm_clock",
        ["⌛"] = "hourglass",
        ["⏳"] = "hourglass_flowing_sand",
        ["\U0001F4E2"] = "loudspeaker",
        ["\U0001F514"] = "bell",
        ["\U0001F4CC"] = "pushpin",
        ["\U0001F4DD"] = "memo",
        ["☀"] = "sunny",
        ["\U0001F319"] = "crescent_moon",
        ["\U0001F308"] = "rainbow",
        ["⚡"] = "zap",
        ["❄"] = "snowflake",
        ["\U0001F4A4"] = "zzz",
        ["\U0001F3AF"] = "dart",
        ["\U0001F3C6"] = "trophy",
        ["\U0001F947"] = "first_place",
        ["\U0001F340"] = "four_leaf_clover",
        ["\U0001F3B6"] = "notes",
        ["\U0001F3B5"] = "musical_note",
        ["\U0001F4A5"] = "boom",
        ["\U0001F4A8"] = "dash",
        ["\U0001F4B8"] = "money_with_wings",
        ["\U0001F511"] = "key",
        ["\U0001F513"] = "unlock",
        ["\U0001F512"] = "lock",
        ["\U0001F6A8"] = "rotating_light",
        ["\U0001F9EA"] = "test_tube",
        ["\U0001F52E"] = "crystal_ball",

        // Animals (a light sprinkle)
        ["\U0001F436"] = "dog",
        ["\U0001F431"] = "cat",
        ["\U0001F410"] = "goat",
        ["\U0001F40D"] = "snake",
        ["\U0001F980"] = "crab",
        ["\U0001F41F"] = "fish",
        ["\U0001F984"] = "unicorn",
        ["\U0001F409"] = "dragon",

        // Arrows and BMP symbols with emoji presentation
        ["™"] = "tm",
        ["ℹ"] = "information_source",
        ["↔"] = "left_right_arrow",
        ["↕"] = "arrow_up_down",
        ["↖"] = "arrow_upper_left",
        ["↗"] = "arrow_upper_right",
        ["↘"] = "arrow_lower_right",
        ["↙"] = "arrow_lower_left",
        ["↩"] = "leftwards_arrow_with_hook",
        ["↪"] = "arrow_right_hook",
        ["➡"] = "arrow_right",
        ["⬅"] = "arrow_left",
        ["⬆"] = "arrow_up",
        ["⬇"] = "arrow_down",
        ["▶"] = "arrow_forward",
        ["◀"] = "arrow_backward",

        // Card suits (the black, emoji-presentation ones; the white suits are text and fold to ?)
        ["♠"] = "spades",
        ["♣"] = "clubs",
        ["♥"] = "hearts",
        ["♦"] = "diamonds",

        // Tag-sequence flags (base 🏴 + invisible region spelling). The guild is a UK crowd, so
        // the home nations get their Discord names; any other tag sequence says [flag].
        ["\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F"] = "scotland",
        ["\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F"] = "england",
        ["\U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F"] = "wales",

        // Coloured circles (raid callouts and the like)
        ["\U0001F534"] = "red_circle",
        ["\U0001F7E2"] = "green_circle",
        ["\U0001F7E1"] = "yellow_circle",
        ["\U0001F535"] = "blue_circle",
        ["\U0001F7E3"] = "purple_circle",
        ["⚫"] = "black_circle",
        ["⚪"] = "white_circle",
    };

    /// <summary>
    /// The table entries that default to TEXT presentation despite being astral-plane emoji:
    /// unlike the rest of the astral entries, these render as monochrome glyphs unless VS16
    /// follows them. Length alone cannot spot them, so they are listed; without this, restoring
    /// <c>:dagger:</c> or <c>:shield:</c> would break the exact round trip the reverse map
    /// promises. MUST be declared before <see cref="EmojiByName"/> (textual initializer order).
    /// </summary>
    private static readonly string[] DefaultTextAstral = ["\U0001F5E1" /* dagger */, "\U0001F6E1" /* shield */];

    /// <summary>
    /// <see cref="Names"/> inverted for <see cref="RestoreEmoji"/> — built from the same table so
    /// the two directions cannot drift apart. Case-insensitive because a player types by hand.
    /// Single-unit BMP entries, plus the astral stragglers in <see cref="DefaultTextAstral"/>,
    /// gain a variation selector so symbols like ❤ and ⚠ take their
    /// colour emoji presentation in Discord rather than the monochrome text glyph (and
    /// <see cref="Replace"/> strips it again, so the round trip stays exact). MUST be declared
    /// after <see cref="Names"/>: static initializers run in textual order.
    /// </summary>
    private static readonly Dictionary<string, string> EmojiByName = BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        var reverse = new Dictionary<string, string>(Names.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (emoji, name) in Names)
        {
            var needsPresentation = emoji.Length == 1 || DefaultTextAstral.Contains(emoji);
            reverse.TryAdd(name, needsPresentation ? emoji + '\uFE0F' : emoji);
        }

        return reverse;
    }
}
