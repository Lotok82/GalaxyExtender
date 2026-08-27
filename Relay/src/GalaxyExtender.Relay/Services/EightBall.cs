namespace GalaxyExtender.Relay.Services;

/// <summary>
/// The magic eight ball: what the bot says to a mention that is not one of the real commands.
///
/// The pick is a hash of the message's snowflake rather than <see cref="Random"/>, for two reasons.
/// Replay safety: the scan is at-most-once by cursor, but a reply that failed to POST leaves no
/// record of what it would have said, and a deterministic pick means the answer to a given question
/// is a fact rather than a die roll — unit-testable without seeding tricks. And distribution: the
/// low bits of a snowflake are worker/increment fields that barely vary in a quiet channel, so the
/// id is mixed (SplitMix64) before the modulo instead of used raw. Asking again still "shakes the
/// ball", because asking again is a new message with a new id.
/// </summary>
public static class EightBall
{
    /// <summary>
    /// The answer for <paramref name="messageId"/> — the question's own snowflake, so the same
    /// question message always maps to the same phrase and a re-ask maps to a fresh one.
    /// </summary>
    public static string Reply(ulong messageId)
    {
        // SplitMix64 finaliser: spreads snowflakes (whose low bits barely vary) across the pool.
        var z = messageId + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;

        return Phrases[(int)(z % (ulong)Phrases.Count)];
    }

    /// <summary>
    /// The pool. Answer-shaped and question-agnostic on purpose — every phrase has to survive being
    /// the reply to a question nobody predicted. Nothing in here names the bot, the guild, or any
    /// person, and none of it is markdown that could ping or link (the reply carries
    /// <c>allowed_mentions.parse: []</c> anyway, but the pool should not need it).
    /// </summary>
    public static readonly IReadOnlyList<string> Phrases =
    [
        // The classics, because a magic eight ball that never says "signs point to yes" isn't one.
        "It is certain.",
        "It is decidedly so.",
        "Without a doubt.",
        "Yes — definitely.",
        "You may rely on it.",
        "As I see it, yes.",
        "Most likely.",
        "Outlook good.",
        "Signs point to yes.",
        "Reply hazy, try again.",
        "Ask again later.",
        "Better not tell you now.",
        "Cannot predict now.",
        "Concentrate and ask again.",
        "Don't count on it.",
        "My reply is no.",
        "My sources say no.",
        "Outlook not so good.",
        "Very doubtful.",
        "Yes. Obviously. Next question.",

        // House specials.
        "No, and I'm frankly surprised you asked.",
        "Absolutely — what could possibly go wrong?",
        "Yes, but you're not going to like it.",
        "Ask your guild leader. Then do the opposite.",
        "That sounds like a hardware problem.",
        "Have you tried turning it off and on again?",
        "Only on Tuesdays.",
        "If you have to ask, you already know.",
        "Sure, why not? I'm just a relay.",
        "My lawyer has advised me not to answer that.",
        "Error 404: answer not found.",
        "Let me consult the server hamsters... they say yes.",
        "The server hamsters have voted no.",
        "42.",
        "That information is classified.",
        "In this economy?",
        "Bold of you to assume I know.",
        "The odds are exactly 50/50: it either happens or it doesn't.",
        "I've seen worse ideas. Not many, but some.",
        "Yes, but don't quote me on that.",
        "No. And I'll be pretending you never asked.",
        "Definitely. Probably. Possibly not.",
        "I'm going to say yes just to see what happens.",
        "Hold on, I'm buffering... ... no.",
        "The prophecy is unclear on this point.",
        "My crystal ball is in for repairs, so let's say yes.",
        "Whatever you do, don't panic.",
        "Yes, and you can tell everyone I said so.",
        "No, and you should probably delete the evidence that you asked.",
        "Consult a professional. I'm a chat relay.",
        "History suggests no. History has been wrong before.",
        "If it compiles, ship it.",
        "Absolutely not. Unless...?",
        "That's between you and the loot table.",
        "The RNG gods demand a sacrifice first.",
        "Roll for it.",
        "Not until the servers come back up.",
        "Yes, but only if nobody's watching.",
        "The odds improve slightly every time you stop asking.",
        "Somewhere, a developer just laughed. Take that as a no.",
        "It worked on my machine.",
        "Sleep on it, then ask someone smarter than me.",
        "Every simulation I ran says yes. I ran one.",
        "Signs point to yes, but the signs were bought secondhand.",
        "Undoubtedly. Wait — that answer was for the previous question.",
        "Ask the magic eight ball. Oh. Right. ...No.",
        "Yes. This answer was approved by absolutely nobody.",
        "Trust your gut. Mine is just capacitors.",
        "That question has been forwarded to the complaints department.",
        "The committee has reviewed your question and gone to lunch.",

        // Local colour, for a bot that lives next to a galaxy far, far away.
        "The Force is unusually quiet on this one.",
        "Search your feelings. You already know the answer.",
        "I find your lack of faith disturbing... but yes.",
        "Never tell me the odds. (They're not great.)",
        "Do. Or do not. There is no try.",
        "These aren't the answers you're looking for.",
        "A Jedi would say yes. A Sith would also say yes. Suspicious.",
        "It's a trap.",
        "The holocron says yes.",
        "The holocron says no, and it seemed pretty smug about it.",
        "Only a Sith deals in absolutes, so... probably.",
        "I have a bad feeling about this.",
        "The council has denied you this answer.",
        "Ask again once your shuttle arrives. So, in about ten minutes.",
        "Yes, for roughly the price of a decent landspeeder.",
        "Not even a protocol droid could say for sure.",
        "The spice must flow, and the answer is yes.",
        "Outlook cloudier than Dagobah.",
        "Clear skies over Tatooine — I'll call that a yes.",
        "The twin suns say yes. Twice.",
        "Not even a Jawa would take that deal.",
        "Difficult to see. Always in motion, the future is.",
        "You're asking a droid brain bolted to a chat relay. Yes.",
        "Many Bothans died to bring you this answer: maybe.",
        "About as certain as a Jawa's asking price.",
        "The Sarlacc says give it a thousand years and check back.",
        "Mind tricks don't work on me. ...Fine, yes.",
        "Impressive. Most impressive. Still no.",
        "That is why you fail.",
        "Punch it. We'll find out together."
    ];
}
