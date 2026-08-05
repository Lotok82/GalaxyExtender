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

	// Thread-safe "has a room id been cached this session?" — the bridge's
	// worker thread reads it to decide whether polling (claiming) is safe.
	static bool hasCachedRoomId();

	DEFINE_HOOK(COMMAND_HANDLER_ADDRESS, parse, originalParse);
};
