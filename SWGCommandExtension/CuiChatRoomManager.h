#pragma once

// CuiChatRoomManager — client-side data only; nothing here is hooked.
//
// s_guildRoomId is the client's own record of the guild chat room id. The
// server auto-joins a guild member into the "GuildChat" room at login;
// CuiChatRoomManager::receiveOnEnteredRoom matches the room name and stores
// the id here (fork CuiChatRoomManager.cpp:1187-1191), and the guild-leave
// path stores 0 back. Reading it gives the bridge a guild room id the moment
// the character is in the world — no typed line required (previously the one
// per-session friction of the Stage 2 injector, discord-stage2-plan.md S2).
//
// Address triple-verified 2026-08-10 against SWGEmu.exe — hunt scripts
// Documentation/tools/find_guildroomid.py / verify_guildroomid.py:
//
//   1. receiveOnEnteredRoom found at 0x00A2BAF0 via its unique WARNING string
//      ("received ChatOnEnteredRoom but room [%d] doesn't exist on client.",
//      exactly one code ref). Its _stricmp chain compares the room name
//      against the static std::strings for "system" / "Planet" / "GroupChat" /
//      "GuildChat" in fork source order; the GuildChat match calls
//      setGuildRoomId (0x00A2E7B0), which stores its argument to 0x01939FB4.
//   2. The sibling setters store to consecutive dwords — planet 0x01939FAC
//      (0x00A2E270), group 0x01939FB0 (0x00A2E510), guild 0x01939FB4 —
//      matching adjacent static definitions in the fork translation unit.
//   3. Every other .text reference to 0x01939FB4 matches a fork use:
//      getGuildRoomId at 0x00A2E260 (`mov eax,[0x01939FB4]; ret`) and the
//      guild-leave zeroing (`push 0; call 0x00A2E7B0` behind a compare of the
//      leaving room id against this dword).
#define CUICHATROOMMANAGER_GUILDROOMID_ADDRESS 0x01939FB4
