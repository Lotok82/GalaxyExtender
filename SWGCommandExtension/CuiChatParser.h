#pragma once

#include "soewrappers.h"
#include "Object.h"

// Technically this isn't the command handler, it's just the handler
// for language commands, moods, etc., but it's perfect for our case as 
// it's the last type of command checked, which means we know most others
// have gotten a chance to be processed before us.
#define COMMAND_HANDLER_ADDRESS 0x9FF6F0

class CuiChatParser : public BaseHookedObject {
public:
	static bool parse(const soe::unicode& command, soe::unicode& result, uint32_t chatRoomID, bool useChatRoom);

	// --- Stage 2 send path (Documentation/discord-stage2-plan.md) ---
	// parse() records (chatRoomID, useChatRoom, text) for every line the player
	// submits and caches the most recent room-routed id. Main thread only, like
	// parse itself.
	static void appendRoomLog(soe::unicode& out);                        // /emu discord rooms
	static bool injectChat(const soe::unicode& text, soe::unicode& out); // /emu discord inject

	// Sends one line into the cached room through the game's own chat pipeline
	// (the confirmed S1 mechanism). Main thread only; false if no room id is
	// cached yet or the handler refused the line. DiscordBridge's injector calls
	// this — kept as a plain wchar_t seam so the bridge translation unit compiles
	// into the test harness without soe::unicode's client allocators.
	static bool injectRoom(const wchar_t* text, size_t length);

	// Reads the client's own guild room id static (CuiChatRoomManager.h) into
	// the cache. Called once per frame from GroundScene::parseMessages, main
	// thread. The static is set when the server auto-joins the character into
	// the guild room at login and zeroed on guild leave, so injection works
	// from login with no typed line. When it reads non-zero it takes priority
	// over the typed-line cache below, which stays as the fallback.
	static void pollClientGuildRoomId();

	// Thread-safe "has a room id been cached this session?" — the bridge's
	// worker thread reads it to decide whether polling (claiming) is safe.
	// True when either source (client static, typed line) has an id.
	static bool hasCachedRoomId();

	// Which source injectRoom would use right now: 0 = none, 1 = the client's
	// guild-room static, 2 = the typed-line cache. Main thread only — status
	// text, not a gate.
	static int cachedRoomIdSource();

	DEFINE_HOOK(COMMAND_HANDLER_ADDRESS, parse, originalParse);
};
