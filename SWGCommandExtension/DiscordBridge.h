#pragma once

#include <windows.h>
#include <cstdint>
#include <string>

// ============================================================================
// DiscordBridge — Stage 1 of the Discord chat bridge (game -> Discord).
//
// Guild chat lines captured by SwgCuiChatWindowTab::appendText are cleaned,
// queued, and POSTed in ~1.5 s batches to the GalaxyExtender relay, which
// de-duplicates across guild members and forwards a single copy to Discord.
// The extension never talks to Discord and never sees the webhook URL.
//
// Configuration lives in DiscordBridge.ini beside the DLL (git-ignored — it
// holds the relay key):
//
//   [DiscordBridge]
//   enabled=1
//   endpoint=https://example.invalid/relay
//   key=<X-Relay-Key handed out by the relay operator>
//   client_id=kaelen          ; optional, relay logging only
//   character=Kaelen          ; optional, relay logging only
//   galaxy=Basilisk           ; optional, relay logging only
//   channel_type=9            ; optional, CT_guild override (see below)
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
	// Both pointers are raw client objects read with SEH guards:
	//   channelId  -> ChannelId, int type at +0x0
	//   chatString -> Unicode::String, {begin, end} wchar_t pointers at +0x0/+0x4
	// Nothing here can throw or block; the caller always runs the original.
	static void onChatAppend(const void* channelId, const void* chatString);

	// --- /emu discord ---
	static bool isEnabled();
	static void setEnabled(bool value);   // 'on' also reloads config + clears the 401 latch
	static void enqueueTestLine();
	static void appendStatus(std::string& out);        // never includes the key
	static void appendChannelTypes(std::string& out);  // CT_guild verification aid

	// --- text helpers (exported for reuse/diagnostics) ---
	// Strips SWG escapes (\#RRGGBB, \#., \>NNN), maps control characters to
	// spaces, trims the ends and clamps to the relay's 512-char line limit.
	// Must stay byte-stable across clients or the relay's dedupe hash misses.
	static void cleanChatText(const wchar_t* in, size_t length, std::wstring& out);
	static bool utf16ToUtf8(const wchar_t* in, size_t length, std::string& out);
};
