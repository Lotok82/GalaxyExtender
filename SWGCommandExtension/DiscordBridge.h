#pragma once

#include <windows.h>
#include <cstdint>
#include <string>
#include <vector>

// ============================================================================
// DiscordBridge — the extension side of the Discord chat bridge.
//
// Stage 1 (game -> Discord): guild chat lines captured by
// SwgCuiChatWindowTab::appendText are cleaned, queued, and POSTed in ~1.5 s
// batches to the GalaxyExtender relay, which de-duplicates across guild
// members and forwards a single copy to Discord. The extension never talks to
// Discord and never sees the webhook URL.
//
// Stage 2 (Discord -> game): the worker polls GET /messages — a per-message
// claim work queue, NOT a broadcast feed (Relay/README.md) — and the frame
// tick injects each claimed message into the real guild room as
// "[Discord] <author>: <text>" through the game's own chat pipeline
// (CuiChatParser::injectRoom), paced at ~1 line/s. The injected line coming
// back through the Stage 1 capture is the relay's delivery ack; the relay
// never forwards marked lines to Discord, so nothing loops. Polling only
// happens while a claim could actually be honoured: room id cached, frame
// tick fresh (in the ground scene), and the incoming queue drained.
//
// World boss alerts: the same capture hook also scans a small allow-list of
// other channel types (CT_systemMessage, CT_quest by default) for lines that
// START with a configured tag — "[PvE World Boss]" / "[PvP World Boss]" — and
// relays only those. Matching at the start is what stops a player typing a fake
// alert: a server broadcast has no sender prefix, a player's line always does.
// Everything else on those channels is personal to the player (mission, loot and
// error messages) and never leaves the machine. The relay recognises the same
// tags and renders them as a coloured embed instead of plain chat. See
// Documentation/world-boss-alert-plan.md.
//
// Configuration lives in DiscordBridge.ini beside the DLL (git-ignored — it
// holds the relay key):
//
//   [DiscordBridge]
//   enabled=1
//   endpoint=https://example.invalid/relay
//   key=<X-Relay-Key handed out by the relay operator>
//   client_id=kaelen          ; optional, relay logging only (defaults to an
//                             ; anonymous per-machine hash, never the hostname)
//   character=Kaelen          ; optional, relay logging only
//   galaxy=Basilisk           ; optional, relay logging only
//   channel_type=9            ; optional, CT_guild override (see below);
//                             ; must be a plain non-negative number
//   allow_http=0              ; http:// endpoints are refused unless set to 1
//                             ; (the key would travel in cleartext)
//   stage2=1                  ; set to 0 to opt this client out of polling /
//                             ; injecting Discord messages (Stage 1 relaying
//                             ; and the display rewrite are unaffected)
//   alerts=1                  ; set to 0 to opt out of relaying tagged server
//                             ; broadcasts (world boss alerts)
//   alert_channel_types=5,11  ; optional, channels scanned for alert tags
//                             ; (CT_systemMessage, CT_quest). Replaces the
//                             ; default rather than adding to it.
//   alert_tags=[PvE World Boss],[PvP World Boss]
//                             ; optional, comma-separated. A line is an alert
//                             ; when it STARTS with one of these, matched
//                             ; case-insensitively. Replaces the default.
//
// Threading: everything in the hook path runs on the game's main thread and
// must never block — clean, enqueue, return. All HTTP happens on the worker
// thread (WinHTTP). The queue and the send state are guarded by a
// CRITICAL_SECTION; the per-frame dedupe state, the occurrence history and the
// observed-channel table are main-thread-only and unguarded by design.
//
// Wire contract, limits and status-code handling: Relay/README.md.
// ============================================================================

class DiscordBridge {
public:
	// Called from DllMain. Only sets up the lock and remembers the module
	// handle — config load and worker start are deferred to first use so no
	// file I/O or thread creation happens under the loader lock.
	static void initialize(HMODULE selfModule);

	// processExiting mirrors DllMain's lpReserved != nullptr. On process exit
	// every other thread is already gone, so we must not wait on the worker.
	static void shutdown(bool processExiting);

	// Per-frame main-thread tick from GroundScene::parseMessages. Drives the
	// frame counter used for local (multi-tab) dedupe.
	static void onFrame();

	// From the SwgCuiChatWindow::Tab::appendText hook, main thread.
	// tab is the Tab object's this-pointer, used ONLY as an identity for the
	// multi-tab dedupe — never dereferenced. The other two are raw client
	// objects read with SEH guards:
	//   channelId  -> ChannelId, int type at +0x0
	//   chatString -> Unicode::String, {begin, end} wchar_t pointers at +0x0/+0x4
	// Nothing here can throw or block (a catch-all fences bad_alloc); the
	// caller always runs the original.
	static void onChatAppend(const void* tab, const void* channelId, const void* chatString);

	// From the same hook, after onChatAppend: if the line is a bridged Discord
	// message ("Sender: [Discord] ..." on the guild channel), produce a display
	// copy with the injecting player's sender-name prefix stripped. Returns
	// false (out untouched) for every other line. The capture above always
	// relays the ORIGINAL line — the relay's ack matching depends on it; only
	// what the local player sees is rewritten.
	static bool maybeRewriteForDisplay(const void* channelId, const void* chatString, std::wstring& out);

	// --- /emu discord ---
	static bool isEnabled();
	static void setEnabled(bool value);   // 'on' also reloads config + clears the 401 latch
	static void enqueueTestLine();
	static void requestPollNow();         // /emu discord poll — also clears the poll fault latch
	static void appendStatus(std::string& out);        // never includes the key
	static void appendChannelTypes(std::string& out);  // CT_guild verification aid

	// --- Stage 2 pieces exported for the test harness ---
	struct IncomingDiscordMessage {
		std::string id;       // Discord snowflake, opaque here
		std::string author;   // relay-sanitized, ≤ 32 chars
		std::string text;     // relay-sanitized, ≤ 200 chars, UTF-8
	};

	// Parses the GET /messages response body. Strict enough to reject
	// truncated or malformed JSON (false), tolerant of unknown fields.
	static bool parseMessagesResponse(const std::string& body,
		std::vector<IncomingDiscordMessage>& outMessages, long long& outDropped);

	// The display-rewrite core: given a raw chat line (SWG escapes intact),
	// strips the sender-name prefix in front of a "[Discord] " marker.
	// "[GuildChat] Kaelen: [Discord] Bob: hi" -> "[GuildChat] [Discord] Bob: hi".
	// Returns false (out untouched) when the line does not match.
	static bool rewriteMarkedLine(const wchar_t* in, size_t length, std::wstring& out);

	// --- World boss alert gate, exported for the test harness ---
	// Both read the loaded configuration, so they answer with whatever
	// alert_tags / alert_channel_types are in force.

	// True when text (already passed through cleanChatText) STARTS with one of
	// the configured tags, compared case-insensitively. Start-anchored on
	// purpose — see the note in the .cpp; this is the anti-spoof rule.
	static bool isAlertLine(const wchar_t* text, size_t length);

	// True when this ChannelId.type is scanned for alert tags.
	static bool isAlertChannel(int channelType);

	// --- text helpers (exported for reuse/diagnostics) ---
	// Strips SWG escapes (\#RRGGBB, \#., \>NNN), maps control characters to
	// spaces, trims the ends and clamps to the relay's 512-char line limit.
	// Must stay byte-stable across clients or the relay's dedupe hash misses.
	static void cleanChatText(const wchar_t* in, size_t length, std::wstring& out);
	static bool utf16ToUtf8(const wchar_t* in, size_t length, std::string& out);
};
