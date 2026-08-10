# World Boss Alerts → Discord — Investigation Plan

Status: **INVESTIGATION COMPLETE, NOTHING BUILT (2026-08-10).** Feasibility confirmed from source on both
sides: the alert lines already reach the existing capture hook and the entire downstream pipeline is
channel-agnostic, so the capture half is small and contained. Decisions taken by the user: alerts go to
**the same Discord channel as guild chat** (same webhook — no second webhook); **no other broadcast types**
are in scope; and the Discord **rendering changes for both feeds** — guild chat drops the embed and becomes
plain text, while world boss alerts **keep the embed box** with a tag-coloured bar (**PvE green, PvP red**),
so the box itself becomes the signal. See
[Discord rendering](#discord-rendering), which is now the larger half of the work and touches the
already-live Stage 1 output. One unknown remains and is expected rather than open: the alert is
**believed to be a `CT_systemMessage` (type 5)**, which the default configuration is built for — confirm
at the first live alert rather than blocking on it.
Last updated: 2026-08-10

Companion docs: [discord-bridge-plan.md](discord-bridge-plan.md) (Stage 1, as built),
[discord-bridge-research.md](discord-bridge-research.md) (binary/client-source findings),
[discord-relay-plan.md](discord-relay-plan.md) (relay), [discord-stage2-plan.md](discord-stage2-plan.md)
(Discord → game).

## The design in one paragraph

The world boss alert is a server broadcast whose text always begins with `[PvE World Boss]` or
`[PvP World Boss]`. The extension already sees it — `Tab::appendText` receives every chat line of every
channel type, and guild chat is merely the one type currently kept. So the extension gains a second,
narrow gate: on a small allow-list of channel types, a **cleaned line that starts with one of the
configured tags** is enqueued exactly like a guild line. Everything downstream — cross-client dedupe,
batching, the outbox, the `/chat` contract — is unchanged. The relay recognises the same literal tags and
splits a batch into two feeds at publish time: guild chat as **plain message text** (no embed, no box) and
alerts as an **embed** whose bar is green for PvE and red for PvP.

## Findings — why this is small

### The capture hook already sees these lines

The hook is `SwgCuiChatWindow::Tab::appendText` @ `0x0102DA80`
([SwgCuiChatWindowTab.h:10](../SWGCommandExtension/SwgCuiChatWindowTab.h#L10)) — every chat line reaching
any tab of any chat window passes through it. The channel filter is a single equality at
[DiscordBridge.cpp:2662](../SWGCommandExtension/DiscordBridge.cpp#L2662):

```cpp
bool interesting = (channelType == s_config.channelType);   // 9 = CT_guild
```

Everything below that line — `cleanChatText`, the per-frame multi-tab dedupe, `computeOccurrence`,
`enqueue`, the worker's batching and HTTP, and on the relay side `DedupeService`, `TextSanitizer`,
`DiscordPublisher`, `Outbox` — never asks which channel a line came from. Nothing there needs to change.

System messages are confirmed to arrive at the hook two independent ways:

- **Fork client source.** `SwgCuiChatWindow::onSystemMessageReceived` routes to
  `ChannelId(CT_systemMessage)` (type 5), or `ChannelId(CT_quest)` (type 11) when the
  `CuiSystemMessageManagerData::F_quest` flag is set, both via `appendTextToChannel` → `Tab::appendText`.
- **Our binary, in game.** The 2026-08-05 session recorded "types 1/5/9 observed"
  ([discord-bridge-plan.md](discord-bridge-plan.md), in-game verification section) — type 5 really does
  arrive here, not just in the fork.

### The tag survives cleaning

`ClientTextManager::colorAndFilterText(msg.translated, TT_systemMessage, false)` prepends a colour escape,
so the raw line is something like `\#ffff00[PvE World Boss] …`. `cleanChatText` strips `\#RRGGBB`, `\#.`
and `\>NNN` escapes and then **trims** ([DiscordBridge.cpp:2296](../SWGCommandExtension/DiscordBridge.cpp#L2296)),
so the cleaned line begins with the literal `[` of the tag. A starts-with test on the cleaned text is
sound.

### The starts-with rule is also the anti-spoof rule

Free, and worth not losing later. Player-typed lines always carry a sender prefix ahead of the body:
`onChatRoomMessageReceived` appends `getShortName(sender.chatId)` followed by `\>032: ` before the text,
which is why guild chat arrives as `[GuildChat] lotok: hi`. A system message goes through
`onSystemMessageReceived`, which appends **only** the translated text — no room prefix, no sender name.

So requiring the tag at the **start** of the cleaned line accepts the server broadcast and rejects a
player typing `[PvE World Boss] lol` in guild or spatial chat, whose line is always
`Kaelen: [PvE World Boss] lol`. One rule, both jobs — no separate spoof check.

**This is the one assumption tied to the open unknown below.** It holds if the alert is a system message.
If the server broadcasts into a chat room instead, the line arrives as
`[Planet] Something: [PvE World Boss] …`, starts-with fails, and the rule needs revisiting (probably
"tag at start, or immediately after a room+sender prefix" — which reintroduces spoofability and would
need a decision).

## Rejected alternatives — do not re-litigate

**Relaying all of `CT_systemMessage` and filtering at the relay.** Rejected on two independent grounds,
both decisive. (1) *Quota*: the worker POSTs every 1500 ms
([DiscordBridge.cpp:37](../SWGCommandExtension/DiscordBridge.cpp#L37)), so a client with a continuously-fed
queue makes ~40 requests/min, against a rate limit of `RateLimitPermitsPerMinute` = 120 **per key** —
and one key is normally shared by the whole guild. Roughly three active players would permanently
saturate the bucket and starve guild chat. (2) *Privacy*: type 5 carries mission, loot, credit and error
messages personal to each player; forwarding it wholesale would publish people's gameplay to Discord.
A narrow client-side gate is mandatory, not an optimisation.

**A relay-pushed pattern list** (rules served in the `POST /presence` response, cached client-side). Built
for a churning pattern list. Two stable literal tags do not churn, and an ini key with the tags as
built-in defaults gets the same "fixable without a rebuild" property for none of the machinery, no new
failure mode, and no risk of a bad push making every client relay traffic. Reconsider only if the tag
set genuinely starts changing often.

**A `kind` field on `ChatLine`** ([ChatBatchRequest.cs:37](../Relay/src/GalaxyExtender.Relay/Contracts/ChatBatchRequest.cs#L37))
to tell the relay a line is an alert. Existed to guard against the client's gate and the relay's rules
drifting apart. With both sides keying off the same literal string there is nothing to drift, so the wire
contract is unchanged — no `/chat` contract version skew to reason about in either deploy order.

**ANSI code blocks for coloured alert text.** The only way Discord will colour message *text* rather than
an embed's border. Rejected: it swaps the embed box for a monospace code box rather than removing a box,
its mobile rendering is uncertain, and it needs a second sanitizer path — backslash escapes render
literally inside a fence, so reusing `ForDiscord` would publish a visible `\[PvE World Boss\]`, while
backtick runs would need neutralising to stop chat text breaking out of the fence. The coloured embed bar
gets the same "this one is different" signal with none of that, and renders identically everywhere.

**Keeping embeds for guild chat too.** The status quo. Rejected on sight of the live output — with every
line boxed, nothing stands out, which is exactly what made alerts need a distinct treatment. Dropping the
box for chat is what gives the alert box its meaning.

**A per-tag cooldown on the relay.** The existing 15 s `DedupeWindowSeconds` already collapses the real
duplicate problem (every in-world client sees the same broadcast in the same second and sends its own
copy). A repeat beyond that window is a genuine second event and probably worth posting — a cooldown
could swallow a legitimate "boss defeated" line. Revisit only if the live cadence proves spammy.

## Extension side

One DLL roll. **This feature cannot be relay-only** — the client does not send these lines today and no
relay change can conjure them, so [[extension-deploy-constraint]] is satisfied the other way: make the
DLL side generic and configurable enough that it ships **once**.

- **Channel-type allow-list** for the alert scan, default `{5, 11}` (system + quest), new ini key. Keep it
  separate from `channel_type` (guild relaying) so the two concerns stay independent.
- **Tag list**, new ini key, default `[PvE World Boss]` and `[PvP World Boss]`. Match on the cleaned text,
  case-sensitive, starts-with.
- Both lists in `DiscordBridge.ini` for the same reason `channel_type` is
  ([discord-bridge-plan.md](discord-bridge-plan.md): "lets a wrong guess be corrected without a rebuild") —
  a wrong type guess or a reworded tag becomes a text-file edit, not a re-roll to every player.
- **Volume backstop**: cap alert lines enqueued per minute. Cheap insurance against a
  mis-set ini turning a chatty channel into a firehose on the shared key.
- Structure the check to keep the main thread cheap: the current code returns before `seh_copyChatText`
  for uninteresting types ([DiscordBridge.cpp:2676](../SWGCommandExtension/DiscordBridge.cpp#L2676)), and
  the allow-list preserves that for the high-volume channels (combat is type 4, spatial type 2 — neither
  is in the default set).
- Surface the alert counters in `/emu discord status`, consistent with the existing counters.
- Harness first, per the Stage 1/Stage 2 pattern — `Harness/` compiles the real `DiscordBridge.cpp`
  standalone, so the gate and the tag matcher are unit-testable without the game.

## Relay side

- Recognise the same literal tags on incoming `/chat` lines and split the batch's unique lines into two
  feeds — guild chat and alerts — each published with its own rendering (below). Today
  [ChatEndpoints.cs:122](../Relay/src/GalaxyExtender.Relay/Endpoints/ChatEndpoints.cs#L122) chunks all
  unique lines into one embed description, so alerts would otherwise be indistinguishable from someone
  typing.
- **Same channel, same webhook** (user decision) — no second webhook, no new Discord channel config.
- Gate behind a new `Discord:AlertsEnabled`, **default false**, matching the `CleanupEnabled` /
  `CommandsEnabled` convention: going live is a deliberate config change, never a redeploy side effect.
- No outbox change. `outbox.Park` stores pre-built payload JSON
  ([ChatEndpoints.cs:155](../Relay/src/GalaxyExtender.Relay/Endpoints/ChatEndpoints.cs#L155)), so either
  payload shape parks and drains like any other.
- A batch containing **both** kinds produces **two** webhook POSTs. Discord does allow `content` and
  `embeds` in one payload, so they *could* share a message — but keeping them separate means an alert
  is its own message (better for pinning, quoting and cleanup) and keeps the outbox's per-payload
  park-on-failure bookkeeping simple. Alerts are rare, so mixed batches are rare either way.
- This half ships and is fully testable **before** the DLL exists — feed `/chat` a tagged line.

## Discord rendering

Decided with the user 2026-08-10 after seeing the live output. **This changes the already-live Stage 1
guild-chat rendering, not just alerts** — treat it as a visible change to a working feature and mention it
before deploying.

### The scheme

**The box becomes the signal.** Guild chat is frequent and should be unobtrusive, so it loses the embed and
becomes plain text. Alerts are rare and should stand out, so they **keep the embed box** with a
tag-dependent left-bar colour — **green for `[PvE World Boss]`, red for `[PvP World Boss]`**. Against a
channel of plain-text guild chat, a boxed coloured alert is unmissable.

### The platform constraint that shaped this

**Discord cannot colour ordinary message text.** An embed's `color` field tints only the left border strip,
never the description text. The sole mechanism for genuinely coloured *text* is an ANSI code block, which
renders in a monospace box with its own background — so it trades the embed box for a code box, adds a
second sanitizer path (backslash escapes render literally inside a fence, so `ForDiscord` would publish a
visible `\[PvE World Boss\]`), and has uncertain mobile support. **Rejected in favour of the coloured embed
bar**, which is what the existing code already does and renders identically everywhere.

### Guild chat → plain message text

- Publish as `content` instead of `embeds`. No box, no left bar, proportional font — reads as a normal
  message.
- **The `[GuildChat]` prefix needs no work: it is already in the text.** The client's
  `onChatRoomMessageReceived` calls `getChatRoomPrefix` for the guild room, so the captured line already
  begins `[GuildChat] `, which is why the live output reads `[GuildChat] carnor: yo bud`. Do **not** add one
  relay-side — it would double up.
- **Split limit drops 4096 → 2000.** `TextSanitizer.MaxDescriptionLength`
  ([TextSanitizer.cs:19](../Relay/src/GalaxyExtender.Relay/Services/TextSanitizer.cs#L19)) is Discord's embed
  ceiling; `content` allows only 2000. `BuildDescriptions` needs the limit as a parameter rather than a
  constant.
- `[` and `]` no longer need escaping **on this path**. They are escaped today for a reason that applies
  only to embeds — embed descriptions render `[text](url)` as a masked hyperlink, plain messages render the
  brackets literally ([TextSanitizer.cs:95-98](../Relay/src/GalaxyExtender.Relay/Services/TextSanitizer.cs#L95-L98)).
  Everything else — `\`, `` ` ``, `*`, `_`, `~`, `|` escaping and the `@everyone`/`@here` zero-width rewrite
  — must stay.
- **`allowed_mentions: {"parse": []}` becomes load-bearing.** Embeds never ping anyone regardless; plain
  `content` does. The lockdown is already sent on every POST
  ([DiscordPublisher.cs](../Relay/src/GalaxyExtender.Relay/Services/DiscordPublisher.cs)) so behaviour is
  correct from day one, but it moves from belt-and-braces to the actual guarantee. Do not remove it, and
  keep the test that asserts it.

### World boss alerts → embed, coloured by tag

- Keep publishing alerts as an **embed** — exactly the shape the code already produces
  ([DiscordPublisher.cs:39-48](../Relay/src/GalaxyExtender.Relay/Services/DiscordPublisher.cs#L39-L48)) —
  with the `color` chosen from the tag the line starts with. Two new options, e.g.
  `Discord:AlertEmbedColorPvE` (green) and `Discord:AlertEmbedColorPvP` (red).
- `BuildPayload` reads the colour from `options.CurrentValue.EmbedColor` today
  ([DiscordPublisher.cs:42](../Relay/src/GalaxyExtender.Relay/Services/DiscordPublisher.cs#L42)); it needs
  the colour as a parameter instead. The existing `EmbedColor` key falls out of use once guild chat stops
  using embeds — leave it configured rather than deleting it, so reverting the guild-chat half is a
  one-line rollback.
- **Alerts keep `ForDiscord` unchanged, including the `[`/`]` escaping** — that escaping is *required*
  here, because this path really is an embed description and the masked-link risk is real. So the two
  feeds sanitise differently and `ForDiscord` needs a flag for whether the target is an embed. Getting it
  backwards is the one way to reintroduce the masked-link hole, so it deserves a test per path.
- Alerts retain the **4096** description limit; only the plain-text feed drops to 2000.
- A line whose tag matches neither colour rule — a tag added to a client's ini but not to the relay's
  colour map — must still publish in a defined default colour, never be dropped.

### Docs to update when this is built

`Relay/README.md` and [discord-bridge-plan.md](discord-bridge-plan.md) both describe the output as "one
green embed per batch" in several places. That sentence becomes wrong.

## Interactions checked, all benign

- **Stage 2 echo.** The alert reaches Discord as a **webhook** post, and the R4 echo filter skips messages
  carrying `webhook_id` / `author.bot`, so `DiscordReader` never injects an alert back into the guild room.
  No loop.
- **Stage 2 ack matching.** `stage2Queue.TryAckMarkedLine` runs on every incoming line
  ([ChatEndpoints.cs:94](../Relay/src/GalaxyExtender.Relay/Endpoints/ChatEndpoints.cs#L94)) and requires the
  strict `Name: ` before `[Discord] ` shape. An alert line has no sender prefix and a different tag, so it
  cannot be mistaken for an ack.
- **R11 delivery notices.** Those answer ordinary *user* chat in the channel that will not reach the guild
  room. Alerts are webhook posts, so they never trigger one.
- **Dedupe and repeats.** N clients see the broadcast simultaneously → the 15 s window collapses them to
  one post. A genuine repeat is labelled by `occurrence` identically on every client, so it still gets
  through.

## The remaining unknown — expected to be type 5

**Which `ChannelId.type` does the alert arrive on?** Core3 has no world-boss concept (grepped — nothing),
so this is a custom screenplay on the server and the delivery mechanism is not knowable from source here.
Four things point the same way, so **`CT_systemMessage` (5) is the working assumption and the default
allow-list is built for it**:

- The user is fairly confident it is a system message (2026-08-10).
- The fork routes system messages to `ChannelId(CT_systemMessage)` via `onSystemMessageReceived`.
- Type 5 was actually observed arriving at our hook in the 2026-08-05 session.
- A `[Tag]`-prefixed, sender-less broadcast is the shape a scripted `sendSystemMessage` produces.

That also means the **starts-with anti-spoof rule is expected to hold** — system messages carry no room or
sender prefix, so the tag really is at position 0 after cleaning. Confirm it rather than assume it: the
whole gate depends on it, and it is a one-glance check at the first live alert.

Settling the type: `/emu discord types` reports observed types with a sample line, but only samples the
**first** line of each type, so if type 5 was already seen the sample will not be the alert. Two ways
forward, in order of cost:

1. Ship the default `{5, 11}` allow-list and simply watch whether alerts appear in Discord. If they do,
   the question is answered and nothing more is needed. If they do not, widen the ini type list and retry —
   no rebuild.
2. If that is inconclusive, add a small per-type ring buffer of recent lines to `/emu discord types`
   (mirroring the `/emu discord rooms` buffer that resolved the Stage 2 send-path unknowns) so one session
   captures the exact wording and type.

Worth confirming in the same session: the exact tag text, including whether the alert carries a sender
prefix (which would break the starts-with rule as noted above).

## Accepted gaps

- **R10 tidies alerts away like chat.** `CleanupEnabled` is already true in production and deletes
  bridge-channel messages older than 5 h, pinned preserved. Boss alerts will therefore not accumulate as a
  history. Consequence of the same-channel decision; pin anything worth keeping.
- **Client profanity filtering** can differ between players and runs before the capture hook, so in
  principle two clients could emit different text for the same alert and dedupe would miss. Pre-existing
  and accepted for guild chat; far less likely to bite here since broadcast alert text is unlikely to trip
  the filter.
- **Players without the extension contribute nothing**, as with guild chat — at least one extension client
  must be in world when the boss spawns for the alert to reach Discord. There is no server-side hook.
- **Guild-chat messages get shorter.** The 4096 → 2000 limit applies to the plain-text feed, so a very busy
  batch splits into more messages than it does today. Harmless, but the channel-history cleanup sweep sees
  slightly more messages. Alerts are unaffected — they stay on 4096.
- **Alert colour is the bar, not the text.** Green and red are the embed's left border, which is all
  Discord offers short of a code block. Colour-blind readers rely on the `[PvE …]` / `[PvP …]` tag in the
  text, which is always present — worth not "tidying" the tag out of the published line.

## Proposed build order

1. **Relay rendering change first, on its own** — plain-text guild chat, the 2000-char split, the
   `[`/`]` unescape. This is independently valuable, immediately visible, and needs no client work; shipping
   it separately keeps the "did the rendering break?" question apart from "did alert capture work?".
2. **Relay alerts**: tag recognition, feed split, per-tag embed colours, `Discord:AlertsEnabled`
   (default off), tests. Verify by POSTing a tagged line to `/chat` — no DLL needed.
3. **Extension**: ini keys, type allow-list, starts-with gate, volume cap, `/emu discord status` counters,
   harness tests. Builds Debug/Release x86.
4. **Deploy relay**, set `"AlertsEnabled": true` in `appsettings.Production.json` (host + local), roll the
   DLL.
5. **In-game confirmation** at the next boss spawn: the alert appears in Discord as a boxed embed in the
   right colour, exactly once with several extension clients in world, and is not injected back into the
   guild room. Confirm in the same pass that the raw line carried no sender prefix, since the gate depends
   on it.
