#pragma once

#include "Object.h"
#include "soewrappers.h"

// SwgCuiChatWindow::Tab::appendText(const ChannelId&, const Unicode::String&)
// __thiscall, this = Tab. Every chat line that reaches any tab of any chat
// window passes through here, which is why it is the bridge's capture point.
// Address triple-verified — see Documentation/discord-bridge-research.md.
#define CHATWINDOWTAB_APPENDTEXT_ADDRESS 0x0102DA80

// SwgCuiChatWindow::ChannelId. Only `type` (int at +0x0) is needed for Stage 1
// and it is the one field confirmed in our binary (the appendText disassembly
// compares dword [eax] against CT_none). The trailing std::string /
// Unicode::String members are deliberately not modelled — old-MSVC ABI.
class ChatChannelId : public BaseHookedObject {
public:
	// Fork enum: CT_none=0, CT_chatRoom, CT_spatial, CT_planet, CT_combat,
	// CT_systemMessage, CT_instantMessage, CT_group, CT_matchMaking,
	// CT_guild=9, CT_city, CT_quest, CT_gcw, CT_named. The fork has diverged
	// from our binary — /emu discord types reports what actually arrives, and
	// channel_type in DiscordBridge.ini overrides the assumed value.
	enum { CT_guild_default = 9 };

	int getType() const {
		return getMemoryReference<int>(0x0);
	}
};

class SwgCuiChatWindowTab : public BaseHookedObject {
public:
	// Defined in DiscordBridge.cpp — the hook body is bridge logic.
	void appendText(const ChatChannelId& id, const soe::unicode& text);

	DEFINE_HOOK(CHATWINDOWTAB_APPENDTEXT_ADDRESS, appendText, originalAppendText);
};
