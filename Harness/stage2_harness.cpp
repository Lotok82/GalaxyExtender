// ============================================================================
// Stage 2 poll/inject harness — compiles the REAL DiscordBridge.cpp and drives
// it against a scripted local HTTP stub (empty / N messages / 429 / 5xx /
// malformed / disabled / 404 / 401), plus unit tests for parseMessagesResponse
// and rewriteMarkedLine. Only the game seams are stubbed:
//   - CuiChatParser::hasCachedRoomId / injectRoom (records instead of sending)
//   - soe::unicode's (wchar_t*, len) ctor (link-only; never executed here)
// Mirrors the Stage 1 harness approach (discord-bridge-plan.md).
// ============================================================================

#include "stdafx.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <string>
#include <vector>

#include "DiscordBridge.h"
#include "CuiChatParser.h"
#include "soewrappers.h"

#pragma comment(lib, "ws2_32.lib")

// ---------------------------------------------------------------------------
// Test bookkeeping
// ---------------------------------------------------------------------------

static int g_checks = 0;
static int g_failures = 0;

#define CHECK(cond, name) do { \
	++g_checks; \
	if (cond) { printf("  ok   %s\n", name); } \
	else { ++g_failures; printf("  FAIL %s (line %d)\n", name, __LINE__); } \
} while (0)

// ---------------------------------------------------------------------------
// Game seam stubs
// ---------------------------------------------------------------------------

static volatile LONG g_hasRoom = 0;

struct InjectRecord {
	std::wstring text;
	ULONGLONG tick;
};

static std::vector<InjectRecord> g_injected;   // main thread only, like the real drain

bool CuiChatParser::hasCachedRoomId() {
	return InterlockedCompareExchange(&g_hasRoom, 0, 0) != 0;
}

bool CuiChatParser::injectRoom(const wchar_t* text, size_t length) {
	InjectRecord record;
	record.text.assign(text, length);
	record.tick = GetTickCount64();
	g_injected.push_back(record);
	return true;
}

// Link-only: referenced by the appendText hook body, which this harness never
// executes (it would call into client memory).
soe::unicode::unicode(const wchar_t* cstring, uint32_t length)
	: stringbase_t<wchar_t>(cstring, static_cast<int>(length)) {
}

// ---------------------------------------------------------------------------
// Scripted HTTP stub server (raw sockets, one request per connection)
// ---------------------------------------------------------------------------

static const unsigned short STUB_PORT = 18091;

static CRITICAL_SECTION g_stubLock;
static std::vector<std::string> g_responses;   // guarded by g_stubLock
static volatile LONG g_served = 0;
static std::string g_firstRequestLine;          // guarded by g_stubLock
static volatile LONG g_stubStop = 0;

static std::string makeResponse(const char* statusLine, const char* extraHeaders, const std::string& body) {
	char header[512];
	sprintf_s(header, sizeof(header),
		"HTTP/1.1 %s\r\nContent-Type: application/json\r\nContent-Length: %u\r\n%sConnection: close\r\n\r\n",
		statusLine, static_cast<unsigned>(body.size()), extraHeaders);
	return std::string(header) + body;
}

static void queueResponse(const std::string& response) {
	EnterCriticalSection(&g_stubLock);
	g_responses.push_back(response);
	LeaveCriticalSection(&g_stubLock);
}

static DWORD WINAPI stubMain(LPVOID) {
	SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);

	sockaddr_in addr;
	memset(&addr, 0, sizeof(addr));
	addr.sin_family = AF_INET;
	addr.sin_port = htons(STUB_PORT);
	addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

	if (bind(listener, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0 ||
		listen(listener, 4) != 0) {
		printf("  STUB: bind/listen failed (%d)\n", WSAGetLastError());
		return 1;
	}

	while (InterlockedCompareExchange(&g_stubStop, 0, 0) == 0) {
		fd_set readable;
		FD_ZERO(&readable);
		FD_SET(listener, &readable);
		timeval timeout = { 0, 200000 };

		if (select(0, &readable, nullptr, nullptr, &timeout) <= 0)
			continue;

		SOCKET client = accept(listener, nullptr, nullptr);

		if (client == INVALID_SOCKET)
			continue;

		std::string request;
		char buffer[2048];

		while (request.find("\r\n\r\n") == std::string::npos) {
			int received = recv(client, buffer, sizeof(buffer), 0);

			if (received <= 0)
				break;

			request.append(buffer, received);

			if (request.size() > 16384)
				break;
		}

		std::string response;

		EnterCriticalSection(&g_stubLock);

		if (g_firstRequestLine.empty()) {
			size_t lineEnd = request.find("\r\n");
			g_firstRequestLine = request.substr(0, lineEnd == std::string::npos ? request.size() : lineEnd);
		}

		LONG index = g_served;

		if (!g_responses.empty()) {
			size_t pick = static_cast<size_t>(index);

			if (pick >= g_responses.size())
				pick = g_responses.size() - 1;

			response = g_responses[pick];
		}

		LeaveCriticalSection(&g_stubLock);

		if (response.empty())
			response = makeResponse("500 Internal Server Error", "", "{}");

		send(client, response.c_str(), static_cast<int>(response.size()), 0);
		InterlockedIncrement(&g_served);

		shutdown(client, SD_SEND);
		closesocket(client);
	}

	closesocket(listener);
	return 0;
}

// ---------------------------------------------------------------------------
// Driving helpers
// ---------------------------------------------------------------------------

// Frame pump: the harness's stand-in for GroundScene::parseMessages.
static void pumpFrames(DWORD milliseconds) {
	ULONGLONG until = GetTickCount64() + milliseconds;

	while (GetTickCount64() < until) {
		DiscordBridge::onFrame();
		Sleep(30);
	}
}

// Waits for the stub to have served `target` requests, pumping frames so the
// poll gates stay open. False on timeout.
static bool pumpUntilServed(LONG target, DWORD timeoutMs) {
	ULONGLONG until = GetTickCount64() + timeoutMs;

	while (GetTickCount64() < until) {
		if (InterlockedCompareExchange(&g_served, 0, 0) >= target)
			return true;

		DiscordBridge::onFrame();
		Sleep(30);
	}

	return false;
}

// Same, but WITHOUT pumping frames (for the staleness gate and for keeping
// claimed messages uninjected).
static bool waitUntilServed(LONG target, DWORD timeoutMs) {
	ULONGLONG until = GetTickCount64() + timeoutMs;

	while (GetTickCount64() < until) {
		if (InterlockedCompareExchange(&g_served, 0, 0) >= target)
			return true;

		Sleep(30);
	}

	return false;
}

static bool pumpUntilInjected(size_t target, DWORD timeoutMs) {
	ULONGLONG until = GetTickCount64() + timeoutMs;

	while (GetTickCount64() < until) {
		if (g_injected.size() >= target)
			return true;

		DiscordBridge::onFrame();
		Sleep(30);
	}

	return false;
}

static std::string bridgeStatus() {
	std::string status;
	DiscordBridge::appendStatus(status);
	return status;
}

static bool statusContains(const char* needle) {
	return bridgeStatus().find(needle) != std::string::npos;
}

static void writeIni() {
	wchar_t path[MAX_PATH];
	GetModuleFileNameW(nullptr, path, MAX_PATH);
	wchar_t* slash = wcsrchr(path, L'\\');
	slash[1] = 0;
	std::wstring ini(path);
	ini += L"DiscordBridge.ini";

	FILE* file = nullptr;
	_wfopen_s(&file, ini.c_str(), L"w");
	fprintf(file,
		"[DiscordBridge]\n"
		"enabled=1\n"
		"endpoint=http://127.0.0.1:%u/relay\n"
		"key=harness-test-key\n"
		"client_id=harness\n"
		"allow_http=1\n"
		"stage2=1\n",
		STUB_PORT);
	fclose(file);
}

// ---------------------------------------------------------------------------
// Unit tests: parseMessagesResponse
// ---------------------------------------------------------------------------

static void testParse() {
	printf("parseMessagesResponse:\n");

	std::vector<DiscordBridge::IncomingDiscordMessage> messages;
	long long dropped = -1;

	CHECK(DiscordBridge::parseMessagesResponse("{\"messages\":[],\"dropped\":0}", messages, dropped)
		&& messages.empty() && dropped == 0, "empty list");

	const char* two =
		"{ \"messages\": [\n"
		"  { \"id\": \"1402691702617341952\", \"author\": \"Bob\", \"text\": \"hi \\\"there\\\"\","
		"    \"timestampUtc\": \"2026-08-05T21:14:09+00:00\", \"extra\": { \"nested\": [1,2] } },\n"
		"  { \"text\": \"caf\\u00e9 \\ud83d\\ude00\", \"author\": \"M\\u00fcller\", \"id\": \"2\" }\n"
		"], \"dropped\": 3 }";

	CHECK(DiscordBridge::parseMessagesResponse(two, messages, dropped)
		&& messages.size() == 2 && dropped == 3, "two messages + unknown fields");
	CHECK(messages.size() == 2 && messages[0].id == "1402691702617341952"
		&& messages[0].author == "Bob" && messages[0].text == "hi \"there\"", "field values");
	CHECK(messages.size() == 2 && messages[1].text == "caf\xC3\xA9 \xF0\x9F\x98\x80"
		&& messages[1].author == "M\xC3\xBCller", "\\u escapes incl. surrogate pair");

	CHECK(!DiscordBridge::parseMessagesResponse("{}", messages, dropped), "bare object rejected");
	CHECK(!DiscordBridge::parseMessagesResponse("", messages, dropped), "empty body rejected");
	CHECK(!DiscordBridge::parseMessagesResponse("<html>oops</html>", messages, dropped), "html rejected");
	CHECK(!DiscordBridge::parseMessagesResponse("{\"messages\":[{\"id\":\"1\",\"au", messages, dropped),
		"truncated body rejected");
	CHECK(!DiscordBridge::parseMessagesResponse("{\"messages\":[null],\"dropped\":0}", messages, dropped),
		"null element rejected");
	CHECK(!DiscordBridge::parseMessagesResponse("{\"dropped\":0}", messages, dropped),
		"missing messages array rejected");
	CHECK(DiscordBridge::parseMessagesResponse(
		"{\"unknown\":{\"a\":[true,null,1.5]},\"messages\":[],\"dropped\":0}", messages, dropped),
		"unknown top-level object tolerated");
}

// ---------------------------------------------------------------------------
// Unit tests: rewriteMarkedLine
// ---------------------------------------------------------------------------

static bool rewrites(const wchar_t* in, const wchar_t* expected) {
	std::wstring out;

	if (!DiscordBridge::rewriteMarkedLine(in, wcslen(in), out))
		return false;

	return out == expected;
}

static bool leavesAlone(const wchar_t* in) {
	std::wstring out;
	return !DiscordBridge::rewriteMarkedLine(in, wcslen(in), out);
}

static void testRewrite() {
	printf("rewriteMarkedLine:\n");

	CHECK(rewrites(L"[GuildChat] Kaelen: [Discord] Bob: hi",
		L"[GuildChat] [Discord] Bob: hi"), "tag + sender stripped");
	CHECK(rewrites(L"Kaelen: [Discord] Bob: hi",
		L"[Discord] Bob: hi"), "no tag, sender stripped");
	CHECK(rewrites(L"\\#8888ff[GuildChat] \\#ffffffKaelen\\#.: [Discord] Bob: hi",
		L"\\#8888ff[GuildChat] \\#ffffff\\#.[Discord] Bob: hi"),
		"colour escapes tolerated and kept (colour state carries into the body)");
	CHECK(rewrites(L"[GuildChat] Kaelen: \\#800080[Discord] Bob: hi\\#008000",
		L"[GuildChat] \\#800080[Discord] Bob: hi\\#008000"),
		"injected purple wrap survives the sender strip");
	CHECK(rewrites(L"[GuildChat] Kae len: [Discord] Bob: hi",
		L"[GuildChat] [Discord] Bob: hi"), "sender with space stripped");

	CHECK(leavesAlone(L"[GuildChat] Kaelen: check this [Discord] thing"), "marker mid-sentence kept");
	CHECK(leavesAlone(L"[GuildChat] Kaelen: see: [Discord] x"), "second colon kept");
	CHECK(leavesAlone(L"[GuildChat] Kaelen: hello everyone"), "plain line kept");
	CHECK(leavesAlone(L"[Discord] Bob: hi"), "marker at start kept");
	CHECK(leavesAlone(L"[GuildChat] [Discord] Bob: hi"), "already-clean line kept");
	CHECK(leavesAlone(L""), "empty kept");
	CHECK(leavesAlone(L"[GuildChat] AbsurdlyLongSenderNameThatCannotBeACharacterNameAtAllNoWay: [Discord] Bob: hi"),
		"overlong sender kept");
}

// ---------------------------------------------------------------------------
// Live loop against the scripted stub
// ---------------------------------------------------------------------------

static void testLiveLoop() {
	printf("poll/inject loop vs scripted stub:\n");

	writeIni();

	WSADATA wsa;
	WSAStartup(MAKEWORD(2, 2), &wsa);
	InitializeCriticalSection(&g_stubLock);

	HANDLE stubThread = CreateThread(nullptr, 0, stubMain, nullptr, 0, nullptr);
	Sleep(200);

	// Response script, in served order.
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":["
		"{\"id\":\"1\",\"author\":\"Bob\",\"text\":\"hi there\"},"
		"{\"id\":\"2\",\"author\":\"Alice\",\"text\":\"caf\\u00e9 time\"}"
		"],\"dropped\":0}"));                                                        // 1: two injections
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":[],\"dropped\":0}"));                                         // 2: idle
	queueResponse(makeResponse("429 Too Many Requests", "Retry-After: 2\r\n", ""));  // 3: rate limited
	queueResponse(makeResponse("500 Internal Server Error", "", "{}"));              // 4: server error
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":[{\"id\":"));                                                 // 5: malformed
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":[],\"dropped\":3}"));                                         // 6: relay-side drops
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: disabled\r\n",
		"{\"messages\":[],\"dropped\":0}"));                                         // 7: stage 2 off on relay
	queueResponse(makeResponse("404 Not Found", "", "{\"title\":\"nope\"}"));        // 8: fault latch
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":[],\"dropped\":0}"));                                         // 9: recovered
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":["
		"{\"id\":\"3\",\"author\":\"Bob\",\"text\":\"you are offline\"},"
		"{\"id\":\"4\",\"author\":\"Bob\",\"text\":\"still offline\"}"
		"],\"dropped\":0}"));                                                        // 10: claimed then off
	queueResponse(makeResponse("401 Unauthorized", "", ""));                         // 11: key latch
	queueResponse(makeResponse("200 OK", "X-Relay-Stage2: enabled\r\n",
		"{\"messages\":[],\"dropped\":0}"));                                         // 12+: recovery

	DiscordBridge::initialize(GetModuleHandleW(nullptr));

	// Gate 1: no room id cached -> no poll even with fresh frames.
	pumpFrames(3500);
	CHECK(InterlockedCompareExchange(&g_served, 0, 0) == 0, "no poll without a room id");

	// Gate 2: room id cached but frame tick stale -> still no poll. The tick
	// must go stale BEFORE the room id appears, or the worker legitimately
	// polls inside the freshness window left over from gate 1.
	Sleep(3600);
	InterlockedExchange(&g_hasRoom, 1);
	Sleep(3600);
	CHECK(InterlockedCompareExchange(&g_served, 0, 0) == 0, "no poll while the frame tick is stale");

	// 1: two messages -> two paced injections through the CuiChatParser seam.
	CHECK(pumpUntilInjected(2, 15000), "two messages injected");

	if (g_injected.size() >= 2) {
		CHECK(g_injected[0].text == L"\\#800080[Discord] Bob: hi there\\#008000",
			"injected line 1 composed (purple wrap)");
		CHECK(g_injected[1].text == L"\\#800080[Discord] Alice: caf\x00E9 time\\#008000",
			"injected line 2 composed (UTF-8 -> UTF-16, purple wrap)");

		ULONGLONG gap = g_injected[1].tick - g_injected[0].tick;
		CHECK(gap >= 1000, "injection paced >= 1s");
	}

	EnterCriticalSection(&g_stubLock);
	std::string firstRequest = g_firstRequestLine;
	LeaveCriticalSection(&g_stubLock);
	CHECK(firstRequest == "GET /relay/api/v1/messages?client=harness HTTP/1.1", "request line + query");

	// 2: idle poll.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(2, 10000), "idle poll served");
	Sleep(100);
	CHECK(statusContains("ok - no messages"), "status: no messages");

	// 3: 429 honours Retry-After.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(3, 10000), "429 served");
	Sleep(100);
	CHECK(statusContains("429 rate limited"), "status: 429");

	// 4: 500 backs off.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(4, 10000), "500 served");
	Sleep(100);
	CHECK(statusContains("server error on poll"), "status: server error");

	// 5: malformed 200.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(5, 10000), "malformed served");
	Sleep(100);
	CHECK(statusContains("unparseable body"), "status: unparseable");

	// 6: relay-reported drops surface.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(6, 10000), "dropped=3 served");
	Sleep(100);
	CHECK(statusContains("missed on relay: 3"), "status: missed count");

	// 7: relay says stage 2 disabled.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(7, 10000), "disabled served");
	Sleep(100);
	CHECK(statusContains("stage 2 disabled on the relay"), "status: relay disabled");
	CHECK(statusContains("relay: \\#ffcc00disabled"), "status: relay state line");

	// 8: 404 latches the stage-2 fault; Stage 1 keeps running.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(8, 10000), "404 served");
	Sleep(100);
	CHECK(statusContains("stage 2 stopped"), "status: fault latched");
	CHECK(DiscordBridge::isEnabled(), "bridge itself still enabled after 404");

	LONG servedBeforeFaultCheck = InterlockedCompareExchange(&g_served, 0, 0);
	pumpFrames(2000);
	CHECK(InterlockedCompareExchange(&g_served, 0, 0) == servedBeforeFaultCheck, "no polls while faulted");

	// 9: /emu discord poll clears the fault.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(9, 10000), "recovered after fault clear");
	Sleep(100);
	CHECK(statusContains("ok - no messages"), "status: recovered");

	// 10: claimed messages are discarded by /emu discord off before injection.
	size_t injectedBefore = g_injected.size();
	pumpFrames(1000);   // keep the frame tick fresh for the poll gate...
	DiscordBridge::requestPollNow();
	CHECK(waitUntilServed(10, 10000), "claims served");   // ...but no frames now: nothing injects
	DiscordBridge::setEnabled(false);
	pumpFrames(2500);
	CHECK(g_injected.size() == injectedBefore, "off discarded claimed messages");
	CHECK(statusContains("incoming: 0 queued"), "status: incoming empty after off");

	DiscordBridge::setEnabled(true);
	pumpFrames(200);

	// 11: 401 latches the whole bridge.
	DiscordBridge::requestPollNow();
	CHECK(pumpUntilServed(11, 10000), "401 served");
	Sleep(100);
	CHECK(!DiscordBridge::isEnabled(), "401 latched the bridge off");
	CHECK(statusContains("rejected key"), "status: rejected key");

	// 12: /emu discord on recovers.
	DiscordBridge::setEnabled(true);
	CHECK(pumpUntilServed(12, 10000), "poll resumed after on");
	Sleep(100);
	CHECK(DiscordBridge::isEnabled(), "bridge re-enabled");

	// Teardown.
	DiscordBridge::shutdown(false);
	InterlockedExchange(&g_stubStop, 1);
	WaitForSingleObject(stubThread, 3000);
	CloseHandle(stubThread);
	WSACleanup();
}

// ---------------------------------------------------------------------------
// Live mode: harness.exe live <path-to-real-DiscordBridge.ini>
// Copies the real ini beside the exe and polls the LIVE relay's /messages
// stub once. Expected while R3 is unbuilt: "stage 2 disabled on the relay".
// appendStatus never contains the key.
// ---------------------------------------------------------------------------

static int liveMode(const wchar_t* iniSource) {
	wchar_t path[MAX_PATH];
	GetModuleFileNameW(nullptr, path, MAX_PATH);
	wchar_t* slash = wcsrchr(path, L'\\');
	slash[1] = 0;
	std::wstring target(path);
	target += L"DiscordBridge.ini";

	if (!CopyFileW(iniSource, target.c_str(), FALSE)) {
		printf("could not copy the ini (%lu)\n", GetLastError());
		return 1;
	}

	DiscordBridge::initialize(GetModuleHandleW(nullptr));
	InterlockedExchange(&g_hasRoom, 1);

	DiscordBridge::requestPollNow();

	ULONGLONG until = GetTickCount64() + 20000;
	std::string status;

	while (GetTickCount64() < until) {
		DiscordBridge::onFrame();
		Sleep(30);

		status = bridgeStatus();

		if (status.find("last poll:") != std::string::npos)
			break;
	}

	printf("--- live status ---\n%s-------------------\n", status.c_str());

	bool ok = status.find("stage 2 disabled on the relay") != std::string::npos ||
		status.find("ok - no messages") != std::string::npos;

	printf(ok ? "LIVE POLL OK\n" : "LIVE POLL DID NOT REACH THE STUB\n");

	DiscordBridge::shutdown(false);
	DeleteFileW(target.c_str());   // do not leave the real key lying around

	return ok ? 0 : 1;
}

int main(int argc, char** argv) {
	printf("=== DiscordBridge Stage 2 harness ===\n");

	if (argc >= 3 && strcmp(argv[1], "live") == 0) {
		wchar_t source[MAX_PATH];
		MultiByteToWideChar(CP_ACP, 0, argv[2], -1, source, MAX_PATH);
		return liveMode(source);
	}

	testParse();
	testRewrite();
	testLiveLoop();

	printf("=== %d checks, %d failure(s) ===\n", g_checks, g_failures);
	return g_failures == 0 ? 0 : 1;
}
