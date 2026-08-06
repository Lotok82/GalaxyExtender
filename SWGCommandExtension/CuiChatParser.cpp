#include "stdafx.h"

#include <windows.h>

#include "CuiChatParser.h"
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
// Stage 2 S1/S2 send-path spike (Documentation/discord-stage2-plan.md).
//
// S1: record what (chatRoomID, useChatRoom) arrive for typed lines so a live
// session can show how the client routes room chat — including whether plain
// guild-tab lines reach this handler at all.
// S2 stopgap: cache the last room-routed id so injectChat has a destination
// before the proper s_guildRoomId static is hunted down.
//
// Everything here is main-thread-only: parse and the /emu handlers all run
// inside the typed-chat dispatch.
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

// Mirror of s_roomIdCached readable from the bridge's worker thread. The main
// thread is the only writer; a plain bool read cross-thread would work on x86
// but the interlocked mirror keeps the intent explicit.
volatile LONG s_roomIdCachedFlag = 0;

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
}

bool CuiChatParser::injectChat(const soe::unicode& text, soe::unicode& out) {
	if (!s_roomIdCached) {
		out += L"\\#ff4444No chat room id cached.\\#ffffff Type a line in the guild tab, "
			L"verify with /emu discord rooms, then retry.\n";
		return false;
	}

	// The S1 hypothesis: the original handler sends plain text into the room
	// itself. Deliberately not routed through our parse() so the injected line
	// is not re-recorded here.
	soe::unicode echo;
	bool handled = originalParse::run(text, echo, s_cachedRoomId, true);

	char line[128];
	sprintf_s(line, sizeof(line), "inject: originalParse::run(room=%u (0x%08X), useChatRoom=1) returned %s\n",
		s_cachedRoomId, s_cachedRoomId, handled ? "true" : "false");
	out += line;

	if (!echo.empty()) {
		out += L"  result text: ";
		out += echo;
		out += L"\n";
	}

	return true;
}

bool CuiChatParser::injectRoom(const wchar_t* text, size_t length) {
	if (!s_roomIdCached || text == nullptr || length == 0)
		return false;

	// Same confirmed mechanism as injectChat, without the diagnostic output.
	// Not routed through our parse() so the injected line is not re-recorded.
	soe::unicode line(text, static_cast<uint32_t>(length));
	soe::unicode echo;

	return originalParse::run(line, echo, s_cachedRoomId, true);
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
