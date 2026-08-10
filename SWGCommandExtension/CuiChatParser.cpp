#include "stdafx.h"

#include <windows.h>

#include "CuiChatParser.h"
#include "CuiChatRoomManager.h"
#include "CuiMediatorFactory.h"
#include "SwgCuiConsole.h"
#include "EmuCommandParser.h"

template<typename String, typename Delimiter, typename Vector>
void split(const String& s, Delimiter delim, Vector& v) {
	auto i = 0;
	auto pos = s.find(delim);
	while (pos != String::npos) {
		v.push_back(s.substr(i, pos - i));
		i = ++pos;
		pos = s.find(delim, pos);

		if (pos == String::npos)
			v.push_back(s.substr(i, s.length()));
	}
}


// ============================================================================
// Guild room id — two sources (Documentation/discord-stage2-plan.md).
//
// Primary: the client's own CuiChatRoomManager::s_guildRoomId static
// (CuiChatRoomManager.h), read once per frame by pollClientGuildRoomId. The
// server auto-joins guild members into the guild room at login, so this is
// non-zero from the moment the character is in the world and zero again on
// guild leave — injection needs no typed line.
//
// Fallback: the original S2 stopgap — cache the last room-routed id seen for
// a TYPED line. Kept for guildless characters exercising /emu discord inject,
// and as a safety net should the static's address ever drift in a client
// update (the per-frame read would then report 0 or garbage-but-SEH-safe; a
// typed line still routes). The stopgap can hold a NON-guild room (it caches
// whatever room tab the player last typed in), which is why the client static
// wins whenever it is non-zero.
//
// Everything here is main-thread-only (parse, the /emu handlers and the
// per-frame poll all run on the game's main thread); the worker thread sees
// only the interlocked s_roomIdCachedFlag mirror.
// ============================================================================

namespace {

struct TypedLineRecord {
	unsigned long seq;
	uint32_t roomId;
	bool useChatRoom;
	wchar_t sample[64];
};

const size_t ROOM_LOG_CAPACITY = 16;

TypedLineRecord s_roomLog[ROOM_LOG_CAPACITY];
unsigned long s_roomLogTotal = 0;

uint32_t s_cachedRoomId = 0;
unsigned long s_cachedRoomSeq = 0;
bool s_roomIdCached = false;

// Main-thread copy of the client's guild-room static, refreshed every frame.
uint32_t s_clientGuildRoomId = 0;

// Mirror of "some source has a room id" readable from the bridge's worker
// thread. The main thread is the only writer; a plain bool read cross-thread
// would work on x86 but the interlocked mirror keeps the intent explicit.
volatile LONG s_roomIdCachedFlag = 0;

// The id injection would use right now: client static first, typed-line
// cache second, 0 for nothing.
uint32_t currentRoomId() {
	if (s_clientGuildRoomId != 0)
		return s_clientGuildRoomId;

	return s_roomIdCached ? s_cachedRoomId : 0;
}

// SEH-guarded read of the client static — its own function because MSVC
// forbids __try in functions with C++ objects needing unwinding (same rule as
// DiscordBridge's seh_* helpers). The address is .data in our own module's
// process, so a fault is not expected; the guard costs nothing and turns
// "cannot be read" into "no id" instead of a crash.
bool seh_readGuildRoomId(uint32_t& out) {
	__try {
		out = *reinterpret_cast<const volatile uint32_t*>(
			static_cast<uintptr_t>(CUICHATROOMMANAGER_GUILDROOMID_ADDRESS));
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

void recordTypedLine(const soe::unicode& text, uint32_t chatRoomID, bool useChatRoom) {
	TypedLineRecord& record = s_roomLog[s_roomLogTotal % ROOM_LOG_CAPACITY];

	record.seq = ++s_roomLogTotal;
	record.roomId = chatRoomID;
	record.useChatRoom = useChatRoom;

	size_t length = text.size();

	if (length > _countof(record.sample) - 1)
		length = _countof(record.sample) - 1;

	memcpy(record.sample, text.c_str(), length * sizeof(wchar_t));
	record.sample[length] = 0;

	if (useChatRoom && chatRoomID != 0) {
		s_cachedRoomId = chatRoomID;
		s_cachedRoomSeq = record.seq;
		s_roomIdCached = true;
		InterlockedExchange(&s_roomIdCachedFlag, 1);
	}
}

} // anonymous namespace

void CuiChatParser::appendRoomLog(soe::unicode& out) {
	out += L"\\#88ccffTyped-line room log (S1 spike):\\#ffffff\n";

	if (s_roomLogTotal == 0) {
		out += L"  (nothing typed since the DLL loaded)\n";
		return;
	}

	unsigned long shown = s_roomLogTotal < ROOM_LOG_CAPACITY
		? s_roomLogTotal : static_cast<unsigned long>(ROOM_LOG_CAPACITY);

	for (unsigned long i = 0; i < shown; ++i) {
		// Oldest retained record first. Record with seq N lives in slot (N-1).
		unsigned long slot = (s_roomLogTotal - shown + i) % ROOM_LOG_CAPACITY;
		const TypedLineRecord& record = s_roomLog[slot];

		char line[96];
		sprintf_s(line, sizeof(line), "  #%-3lu room=%u (0x%08X) useChatRoom=%d  ",
			record.seq, record.roomId, record.roomId, record.useChatRoom ? 1 : 0);

		out += line;
		out += record.sample;
		out += L"\n";
	}

	if (s_roomIdCached) {
		char line[96];
		sprintf_s(line, sizeof(line), "  cached room id: %u (0x%08X) from line #%lu\n",
			s_cachedRoomId, s_cachedRoomId, s_cachedRoomSeq);
		out += line;
	} else {
		out += L"  cached room id: (none - no room-routed line seen yet)\n";
	}

	if (s_clientGuildRoomId != 0) {
		char line[96];
		sprintf_s(line, sizeof(line),
			"  client guild room id: %u (0x%08X) - used for injection\n",
			s_clientGuildRoomId, s_clientGuildRoomId);
		out += line;
	} else {
		out += L"  client guild room id: (none - not in a guild room; "
			L"typed-line cache is the fallback)\n";
	}
}

void CuiChatParser::pollClientGuildRoomId() {
	uint32_t roomId = 0;

	if (!seh_readGuildRoomId(roomId))
		roomId = 0;

	s_clientGuildRoomId = roomId;

	// Recomputed every frame so a guild LEAVE (static back to 0, no typed
	// line cached) also switches the worker's polling gate off.
	InterlockedExchange(&s_roomIdCachedFlag, currentRoomId() != 0 ? 1 : 0);
}

int CuiChatParser::cachedRoomIdSource() {
	if (s_clientGuildRoomId != 0)
		return 1;

	return s_roomIdCached ? 2 : 0;
}

bool CuiChatParser::injectChat(const soe::unicode& text, soe::unicode& out) {
	uint32_t roomId = currentRoomId();

	if (roomId == 0) {
		out += L"\\#ff4444No chat room id available.\\#ffffff The client reports no guild "
			L"room (guildless character?). Type a line in the guild tab, verify with "
			L"/emu discord rooms, then retry.\n";
		return false;
	}

	// The S1 hypothesis: the original handler sends plain text into the room
	// itself. Deliberately not routed through our parse() so the injected line
	// is not re-recorded here.
	soe::unicode echo;
	bool handled = originalParse::run(text, echo, roomId, true);

	char line[128];
	sprintf_s(line, sizeof(line), "inject: originalParse::run(room=%u (0x%08X), useChatRoom=1) returned %s\n",
		roomId, roomId, handled ? "true" : "false");
	out += line;

	if (!echo.empty()) {
		out += L"  result text: ";
		out += echo;
		out += L"\n";
	}

	return true;
}

bool CuiChatParser::injectRoom(const wchar_t* text, size_t length) {
	uint32_t roomId = currentRoomId();

	if (roomId == 0 || text == nullptr || length == 0)
		return false;

	// Same confirmed mechanism as injectChat, without the diagnostic output.
	// Not routed through our parse() so the injected line is not re-recorded.
	soe::unicode line(text, static_cast<uint32_t>(length));
	soe::unicode echo;

	return originalParse::run(line, echo, roomId, true);
}

bool CuiChatParser::hasCachedRoomId() {
	return InterlockedCompareExchange(&s_roomIdCachedFlag, 0, 0) != 0;
}

bool CuiChatParser::parse(const soe::unicode& incomingCommand, soe::unicode& resultUnicode, uint32_t chatRoomID, bool useChatRoom) {
	recordTypedLine(incomingCommand, chatRoomID, useChatRoom);

	CuiMediator* console = CuiMediatorFactory::get("Console");

	bool consoleActive = console ? console->isActive() : false;

	if (consoleActive)
		resultUnicode += (L"\\#ffffff > \\#888888" + incomingCommand + L"\\#ffffff\n");

	auto foundSlash = incomingCommand.find(L"/");
	if (foundSlash != soe::unicode::npos) {
		// Strip the slash.
		auto command = incomingCommand.substr(foundSlash + 1);

		if (command == L"console") {
			if (console == nullptr) {
				typedef CuiMediatorFactory::Constructor<SwgCuiConsole> ctor_t;

				auto ctor = new ctor_t(("/Console"));
				CuiMediatorFactory::addConstructor("Console", ctor);

				console = CuiMediatorFactory::get("Console");

				if (console != nullptr) {
					resultUnicode += L"swgemu console installed";

					CuiMediatorFactory::activate("Console");
				} else {
					resultUnicode += "could not find console in cui mediator factory";
				}
			} else {
				CuiMediatorFactory::toggle("Console");
			}

			return true;
		} else if (command == L"exit") {
			if (consoleActive) {
				CuiMediatorFactory::toggle("Console");

				return true;
			}
		} else if ((command.size() == 3 && command == L"emu") || (command.find(L"emu ") == 0)) {
			if (!consoleActive) {
				soe::vector<soe::unicode> args;
				split(command, L' ', args);

				EmuCommandParser::parse(args, command, resultUnicode);

				return true;
			}
		}
	}

	return originalParse::run(incomingCommand, resultUnicode, chatRoomID, useChatRoom);
}
