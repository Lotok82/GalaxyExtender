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

	// --- Stage 2 S1/S2 send-path spike (Documentation/discord-stage2-plan.md) ---
	// parse() records (chatRoomID, useChatRoom, text) for every line the player
	// submits and caches the most recent room-routed id. Main thread only, like
	// parse itself.
	static void appendRoomLog(soe::unicode& out);                        // /emu discord rooms
	static bool injectChat(const soe::unicode& text, soe::unicode& out); // /emu discord inject

	DEFINE_HOOK(COMMAND_HANDLER_ADDRESS, parse, originalParse);
};
