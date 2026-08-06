#include "stdafx.h"

#include "DiscordBridge.h"
#include "SwgCuiChatWindowTab.h"
#include "CuiChatParser.h"
#include "soewrappers.h"

#include <winhttp.h>
#include <rpc.h>
#include <stdio.h>
#include <deque>
#include <vector>

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "rpcrt4.lib")

// ============================================================================
// Limits. The first three mirror the relay's own validation (Relay/README.md)
// and are enforced here because a violation rejects the *entire* batch.
// ============================================================================

namespace {

const size_t MAX_LINE_CHARS = 512;        // relay MaxLineLength, UTF-16 units
const size_t MAX_LINES_PER_BATCH = 50;    // relay MaxLinesPerBatch
const size_t MAX_BODY_BYTES = 32768;      // relay body cap
const size_t MAX_LINES_JSON_BYTES = 30000; // leaves room for the envelope
const size_t LINE_JSON_OVERHEAD = 64;     // per-line field names and numbers

const size_t MAX_QUEUE_LINES = 500;       // outbound backlog cap (oldest dropped)
const size_t MAX_HISTORY_LINES = 500;     // occurrence-window cap
const size_t MAX_OBSERVED_TYPES = 24;     // diagnostics table cap
const size_t MAX_RAW_CHAT_CHARS = 2048;   // stack buffer for one appendText call

const ULONGLONG OCCURRENCE_WINDOW_MS = 60000; // relay's occurrence definition
const DWORD BATCH_INTERVAL_MS = 1500;
const DWORD DEDUPE_WINDOW_MS = 200;       // see localDuplicate()
const DWORD WORKER_JOIN_TIMEOUT_MS = 2000;
const int MAX_BATCH_ATTEMPTS = 6;         // 5xx/timeout retries before dropping
const size_t MAX_RESPONSE_BYTES = 4096;

// Server-supplied backoff (Retry-After, retryAfterMs) is capped so a misbehaving
// response cannot silently pause the bridge for hours. A real overload clears in
// minutes; anything longer should stay visible and recoverable via /emu discord.
const int MAX_RETRY_AFTER_SECONDS = 900;

// --- Stage 2 (Discord -> game) ---

const DWORD POLL_INTERVAL_MS = 5000;           // R8 load math assumes 5 s
const DWORD POLL_DISABLED_INTERVAL_MS = 60000; // relay says Stage 2 is off — just check in occasionally
const DWORD POLL_BACKOFF_BASE_MS = 10000;      // doubles per consecutive failure...
const DWORD POLL_BACKOFF_MAX_MS = 300000;      // ...up to 5 min, mirroring the relay's own outbox cap

// The frame tick only runs in the ground scene. If it has been quiet longer
// than this the player is zoning/loading and injection would stall — claiming
// messages then would just burn one of their 2 redeliveries.
const DWORD FRAME_STALE_MS = 3000;

// S6 pacing: the server has no flood throttle on this path (S4), so this is
// purely for in-game readability. 5 messages per poll * ~1.1 s fits far
// inside the relay's 60 s claim window.
const DWORD INJECT_INTERVAL_MS = 1100;

// Injected lines render purple for every viewer, extension or not. Colour
// state persists past the end of a line, so the suffix hands it back to
// guild-chat green instead of leaving the tab purple. Applied AFTER the
// defensive cleanChatText, which would strip these very escapes.
const wchar_t INJECT_COLOUR_PREFIX[] = L"\\#800080";
const wchar_t INJECT_COLOUR_SUFFIX[] = L"\\#008000";

// A claim not injected within this long is let lapse locally: the relay
// redelivers at 60 s, and injecting close to that line would race the
// redelivery and double-post the message into the room.
const ULONGLONG CLAIM_SAFETY_MS = 45000;

// Worst legal poll response: 5 messages * (200 text + 32 author) chars, every
// one of them JSON \uXXXX-escaped at 6 bytes, plus ids/timestamps/envelope —
// comfortably under 12 KB. A truncated read would fail the parse and cost the
// claimed messages a redelivery, so size this generously.
const size_t MAX_POLL_RESPONSE_BYTES = 16384;

const size_t MAX_INCOMING_MESSAGES = 25;       // contract is ≤5/poll and we poll only when empty; backstop
const size_t MAX_PARSED_FIELD_BYTES = 2048;    // parser-level clamp per string field, ditto
const size_t MAX_MARKED_SENDER_CHARS = 48;     // display rewrite: longest plausible "Name: " prefix

// ============================================================================
// State
// ============================================================================

struct Config {
	bool enabled;
	bool stage2;            // this client polls/injects Discord messages
	int channelType;
	bool https;
	INTERNET_PORT port;
	std::wstring host;
	std::wstring path;          // "<ini path prefix>/api/v1/chat"
	std::wstring messagesPath;  // "<ini path prefix>/api/v1/messages"
	std::wstring key;       // never logged, never echoed to chat
	std::string clientId;
	std::string character;
	std::string galaxy;
	bool valid;
	std::string error;      // why !valid, shown by /emu discord status

	Config()
		: enabled(false), stage2(true), channelType(ChatChannelId::CT_guild_default), https(true),
		  port(INTERNET_DEFAULT_HTTPS_PORT), valid(false) {
	}
};

struct QueuedLine {
	std::string text;       // UTF-8, cleaned and clamped
	int occurrence;
	long long clientSeq;
};

struct HistoryEntry {
	ULONGLONG tick;
	std::string text;
};

struct ObservedChannel {
	int type;
	unsigned long count;
	std::string sample;     // first line seen on this channel, for identification
};

CRITICAL_SECTION s_lock;
bool s_lockReady = false;
HMODULE s_module = nullptr;
bool s_initialized = false;

// --- guarded by s_lock ---
Config s_config;
unsigned long s_configGeneration = 0;
std::deque<QueuedLine> s_queue;
ULONGLONG s_nextSendTick = 0;
std::string s_lastResult;
ULONGLONG s_lastResultTick = 0;
unsigned long s_batchesAccepted = 0;
unsigned long s_linesAccepted = 0;
unsigned long s_linesDropped = 0;
bool s_stopping = false;
HANDLE s_worker = nullptr;
HANDLE s_stopEvent = nullptr;
HANDLE s_doneEvent = nullptr;

// --- guarded by s_lock (Stage 2) ---
struct IncomingEntry {
	DiscordBridge::IncomingDiscordMessage message;
	ULONGLONG receivedTick;

	IncomingEntry() : receivedTick(0) {
	}
};

std::deque<IncomingEntry> s_incoming;      // claimed, awaiting injection
ULONGLONG s_nextPollTick = 0;
std::string s_lastPollResult;
ULONGLONG s_lastPollResultTick = 0;
unsigned long s_pollFailures = 0;          // consecutive, drives the backoff
int s_stage2Relay = -1;                    // X-Relay-Stage2: -1 unknown, 0 disabled, 1 enabled
unsigned long s_injectedLines = 0;
unsigned long s_expiredLocally = 0;        // claims let lapse (CLAIM_SAFETY_MS)
long long s_discordDropped = 0;            // relay-reported losses (TTL/redelivery cap)
std::string s_lastInjectedSample;
ULONGLONG s_lastInjectedTick = 0;

// --- main thread only ---
unsigned long long s_frameCounter = 0;
long long s_clientSeq = 0;
std::deque<HistoryEntry> s_history;
std::vector<ObservedChannel> s_observed;
std::string s_lastRelayedText;
unsigned long long s_lastRelayedFrame = 0;
ULONGLONG s_lastRelayedTick = 0;
const void* s_lastRelayedTab = nullptr;   // identity only, never dereferenced
ULONGLONG s_nextInjectTick = 0;           // S6 pacing

// --- cross-thread flags ---
volatile LONG s_authFailed = 0;   // 401 latch: bad key, retrying cannot help
volatile LONG s_stage2Fault = 0;  // 400/404 on /messages: polling stopped until /emu discord on|poll
volatile LONGLONG s_lastFrameTick = 0;  // freshness gate for claiming (64-bit: interlocked on x86)

void logLine(const char* format, ...) {
	char message[1024];
	va_list args;
	va_start(args, format);
	_vsnprintf_s(message, sizeof(message), _TRUNCATE, format, args);
	va_end(args);

	char out[1100];
	sprintf_s(out, sizeof(out), "[DiscordBridge] %s\n", message);
	OutputDebugStringA(out);
}

// ============================================================================
// SEH helpers — POD only. MSVC forbids __try in functions holding C++ objects
// that need unwinding, so client-memory reads live in their own functions.
// ============================================================================

bool seh_readChannelType(const void* channelId, int& outType) {
	__try {
		outType = *reinterpret_cast<const int*>(channelId);
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

// Unicode::String is the client's SOE container: {begin, end, endOfStorage}.
// Confirmed by the appendText disassembly (`mov eax,[edi]` / `cmp eax,[edi+4]`
// as the empty check) and by soe::unicode working against other client calls.
bool seh_copyChatText(const void* chatString, wchar_t* dest, size_t destChars, size_t& outLength) {
	__try {
		const wchar_t* const* fields = reinterpret_cast<const wchar_t* const*>(chatString);
		const wchar_t* begin = fields[0];
		const wchar_t* end = fields[1];

		if (begin == nullptr || end == nullptr || end < begin)
			return false;

		size_t length = static_cast<size_t>(end - begin);

		if (length > destChars)
			length = destChars;

		memcpy(dest, begin, length * sizeof(wchar_t));
		outLength = length;

		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

// ============================================================================
// Small string helpers
// ============================================================================

bool isHexDigit(wchar_t c) {
	return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F');
}

bool isDigit(wchar_t c) {
	return c >= L'0' && c <= L'9';
}

void trimWide(std::wstring& text) {
	size_t begin = 0;
	size_t end = text.size();

	while (begin < end && (text[begin] == L' ' || text[begin] == L'\t'))
		++begin;
	while (end > begin && (text[end - 1] == L' ' || text[end - 1] == L'\t'))
		--end;

	if (begin != 0 || end != text.size())
		text = text.substr(begin, end - begin);
}

void trimNarrow(std::string& text) {
	size_t begin = 0;
	size_t end = text.size();

	while (begin < end && (text[begin] == ' ' || text[begin] == '\t'))
		++begin;
	while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t'))
		--end;

	if (begin != 0 || end != text.size())
		text = text.substr(begin, end - begin);
}

std::string narrowLossy(const std::wstring& text) {
	std::string out;
	out.reserve(text.size());

	for (size_t i = 0; i < text.size(); ++i) {
		wchar_t c = text[i];
		out.push_back((c >= 0x20 && c < 0x7F) ? static_cast<char>(c) : '?');
	}

	return out;
}

void appendJsonString(std::string& out, const std::string& value) {
	out.push_back('"');

	for (size_t i = 0; i < value.size(); ++i) {
		unsigned char c = static_cast<unsigned char>(value[i]);

		switch (c) {
		case '"':  out += "\\\""; break;
		case '\\': out += "\\\\"; break;
		case '\b': out += "\\b"; break;
		case '\f': out += "\\f"; break;
		case '\n': out += "\\n"; break;
		case '\r': out += "\\r"; break;
		case '\t': out += "\\t"; break;
		default:
			if (c < 0x20) {
				char escape[8];
				sprintf_s(escape, sizeof(escape), "\\u%04x", c);
				out += escape;
			} else {
				out.push_back(static_cast<char>(c));
			}
			break;
		}
	}

	out.push_back('"');
}

void appendInt(std::string& out, long long value) {
	char buffer[32];
	sprintf_s(buffer, sizeof(buffer), "%lld", value);
	out += buffer;
}

// Minimal scraper for the relay's flat response object — diagnostics only.
long long jsonNumber(const std::string& body, const char* name, long long fallback) {
	std::string needle = "\"";
	needle += name;
	needle += "\"";

	size_t at = body.find(needle);
	if (at == std::string::npos)
		return fallback;

	at = body.find(':', at + needle.size());
	if (at == std::string::npos)
		return fallback;

	++at;
	while (at < body.size() && (body[at] == ' ' || body[at] == '\t'))
		++at;

	bool negative = false;
	if (at < body.size() && body[at] == '-') {
		negative = true;
		++at;
	}

	if (at >= body.size() || body[at] < '0' || body[at] > '9')
		return fallback;

	long long value = 0;
	while (at < body.size() && body[at] >= '0' && body[at] <= '9') {
		value = value * 10 + (body[at] - '0');
		++at;
	}

	return negative ? -value : value;
}

bool utf8ToUtf16(const std::string& in, std::wstring& out) {
	out.clear();

	if (in.empty())
		return true;

	int needed = MultiByteToWideChar(CP_UTF8, 0, in.c_str(), static_cast<int>(in.size()), nullptr, 0);

	if (needed <= 0)
		return false;

	out.resize(static_cast<size_t>(needed));

	int written = MultiByteToWideChar(CP_UTF8, 0, in.c_str(), static_cast<int>(in.size()),
		&out[0], needed);

	if (written <= 0) {
		out.clear();
		return false;
	}

	out.resize(static_cast<size_t>(written));

	return true;
}

// Query-string encoding for the client id. The id is already clamped ASCII
// (clampLabel/narrowLossy), so this mostly passes through; anything outside
// the unreserved set is %XX-escaped rather than trusted.
std::wstring urlEncodeQueryValue(const std::string& value) {
	std::wstring out;
	out.reserve(value.size());

	for (size_t i = 0; i < value.size(); ++i) {
		unsigned char c = static_cast<unsigned char>(value[i]);

		if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
			c == '-' || c == '_' || c == '.' || c == '~') {
			out.push_back(static_cast<wchar_t>(c));
		} else {
			wchar_t escape[8];
			swprintf_s(escape, _countof(escape), L"%%%02X", c);
			out += escape;
		}
	}

	return out;
}

// ============================================================================
// Minimal JSON reader for the /messages response. jsonNumber above is a
// scraper good enough for flat diagnostic fields; the messages array carries
// untrusted-adjacent string content (Discord text, relay-sanitized) that needs
// real escape handling, so this one actually walks the grammar. Anything
// structurally surprising fails the whole parse — the caller treats that as a
// failed poll and the claimed messages come back via redelivery.
// ============================================================================

struct JsonCursor {
	const char* at;
	const char* end;
};

void jsonSkipWs(JsonCursor& c) {
	while (c.at < c.end && (*c.at == ' ' || *c.at == '\t' || *c.at == '\r' || *c.at == '\n'))
		++c.at;
}

bool jsonConsume(JsonCursor& c, char expected) {
	jsonSkipWs(c);

	if (c.at >= c.end || *c.at != expected)
		return false;

	++c.at;
	return true;
}

void jsonAppendCodepointUtf8(std::string& out, unsigned long cp) {
	if (cp < 0x80) {
		out.push_back(static_cast<char>(cp));
	} else if (cp < 0x800) {
		out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
		out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
	} else if (cp < 0x10000) {
		out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
		out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
		out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
	} else {
		out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
		out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
		out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
		out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
	}
}

bool jsonParseHex4(JsonCursor& c, unsigned long& out) {
	if (c.end - c.at < 4)
		return false;

	out = 0;

	for (int i = 0; i < 4; ++i) {
		char h = c.at[i];
		unsigned long digit;

		if (h >= '0' && h <= '9')
			digit = h - '0';
		else if (h >= 'a' && h <= 'f')
			digit = 10 + (h - 'a');
		else if (h >= 'A' && h <= 'F')
			digit = 10 + (h - 'A');
		else
			return false;

		out = (out << 4) | digit;
	}

	c.at += 4;
	return true;
}

// Opening quote already positioned (not consumed). UTF-8 out; \uXXXX decoded
// including surrogate pairs (a lone surrogate becomes '?'). Overlong output is
// truncated at maxBytes without failing — the relay clamps far below that.
bool jsonParseString(JsonCursor& c, std::string& out, size_t maxBytes) {
	out.clear();

	if (!jsonConsume(c, '"'))
		return false;

	while (c.at < c.end) {
		unsigned char ch = static_cast<unsigned char>(*c.at);

		if (ch == '"') {
			++c.at;
			return true;
		}

		if (ch == '\\') {
			++c.at;

			if (c.at >= c.end)
				return false;

			char esc = *c.at;
			++c.at;

			switch (esc) {
			case '"':  if (out.size() < maxBytes) out.push_back('"');  break;
			case '\\': if (out.size() < maxBytes) out.push_back('\\'); break;
			case '/':  if (out.size() < maxBytes) out.push_back('/');  break;
			case 'b':  if (out.size() < maxBytes) out.push_back('\b'); break;
			case 'f':  if (out.size() < maxBytes) out.push_back('\f'); break;
			case 'n':  if (out.size() < maxBytes) out.push_back('\n'); break;
			case 'r':  if (out.size() < maxBytes) out.push_back('\r'); break;
			case 't':  if (out.size() < maxBytes) out.push_back('\t'); break;
			case 'u': {
				unsigned long cp;

				if (!jsonParseHex4(c, cp))
					return false;

				if (cp >= 0xD800 && cp <= 0xDBFF) {
					// High surrogate: needs \uDC00-\uDFFF right behind it.
					if (c.end - c.at >= 6 && c.at[0] == '\\' && c.at[1] == 'u') {
						JsonCursor peek = { c.at + 2, c.end };
						unsigned long low;

						if (jsonParseHex4(peek, low) && low >= 0xDC00 && low <= 0xDFFF) {
							cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
							c.at = peek.at;
						} else {
							cp = '?';
						}
					} else {
						cp = '?';
					}
				} else if (cp >= 0xDC00 && cp <= 0xDFFF) {
					cp = '?';   // lone low surrogate
				}

				if (out.size() + 4 <= maxBytes)
					jsonAppendCodepointUtf8(out, cp);

				break;
			}
			default:
				return false;
			}

			continue;
		}

		if (ch < 0x20)
			return false;   // raw control characters are invalid JSON

		if (out.size() < maxBytes)
			out.push_back(static_cast<char>(ch));

		++c.at;
	}

	return false;   // ran off the end inside the string — truncated body
}

bool jsonSkipValue(JsonCursor& c, int depth);

bool jsonSkipContainer(JsonCursor& c, char open, char close, int depth) {
	if (!jsonConsume(c, open))
		return false;

	jsonSkipWs(c);

	if (c.at < c.end && *c.at == close) {
		++c.at;
		return true;
	}

	for (;;) {
		if (open == '{') {
			std::string key;

			if (!jsonParseString(c, key, 64))
				return false;

			if (!jsonConsume(c, ':'))
				return false;
		}

		if (!jsonSkipValue(c, depth))
			return false;

		jsonSkipWs(c);

		if (c.at >= c.end)
			return false;

		if (*c.at == ',') {
			++c.at;
			continue;
		}

		if (*c.at == close) {
			++c.at;
			return true;
		}

		return false;
	}
}

bool jsonSkipValue(JsonCursor& c, int depth) {
	if (depth <= 0)
		return false;

	jsonSkipWs(c);

	if (c.at >= c.end)
		return false;

	char ch = *c.at;

	if (ch == '"') {
		std::string ignored;
		return jsonParseString(c, ignored, 64);
	}

	if (ch == '{')
		return jsonSkipContainer(c, '{', '}', depth - 1);

	if (ch == '[')
		return jsonSkipContainer(c, '[', ']', depth - 1);

	// number / true / false / null — consume the token blob
	const char* start = c.at;

	while (c.at < c.end && (*c.at == '-' || *c.at == '+' || *c.at == '.' ||
		(*c.at >= '0' && *c.at <= '9') || (*c.at >= 'a' && *c.at <= 'z') ||
		(*c.at >= 'A' && *c.at <= 'Z'))) {
		++c.at;
	}

	return c.at != start;
}

std::string newBatchId() {
	UUID uuid;
	RPC_STATUS status = UuidCreate(&uuid);

	char buffer[48];

	if (status == RPC_S_OK || status == RPC_S_UUID_LOCAL_ONLY) {
		sprintf_s(buffer, sizeof(buffer),
			"%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
			static_cast<unsigned long>(uuid.Data1),
			static_cast<unsigned>(uuid.Data2), static_cast<unsigned>(uuid.Data3),
			uuid.Data4[0], uuid.Data4[1], uuid.Data4[2], uuid.Data4[3],
			uuid.Data4[4], uuid.Data4[5], uuid.Data4[6], uuid.Data4[7]);
	} else {
		// Must still parse as a GUID or the relay rejects the batch outright.
		static unsigned long counter = 0;
		ULONGLONG tick = GetTickCount64();
		++counter;
		sprintf_s(buffer, sizeof(buffer),
			"%08lx-%04lx-4000-8000-%012llx",
			static_cast<unsigned long>(GetCurrentProcessId()),
			counter & 0xFFFF, tick & 0xFFFFFFFFFFFFULL);
	}

	return std::string(buffer);
}

// ============================================================================
// Configuration
// ============================================================================

std::wstring iniPath() {
	wchar_t buffer[MAX_PATH];
	buffer[0] = 0;

	DWORD written = GetModuleFileNameW(s_module, buffer, MAX_PATH);
	if (written == 0 || written >= MAX_PATH)
		return std::wstring();

	wchar_t* lastSlash = wcsrchr(buffer, L'\\');
	if (lastSlash == nullptr)
		return std::wstring();

	lastSlash[1] = 0;

	std::wstring path(buffer);
	path += L"DiscordBridge.ini";

	return path;
}

std::wstring readIniValue(const std::wstring& path, const wchar_t* key, const wchar_t* fallback) {
	wchar_t buffer[1024];
	buffer[0] = 0;

	DWORD length = GetPrivateProfileStringW(L"DiscordBridge", key, fallback,
		buffer, static_cast<DWORD>(_countof(buffer)), path.c_str());

	std::wstring value(buffer, length);
	trimWide(value);

	return value;
}

// Accepts "https://host/relay", with or without a trailing slash, and tolerates
// the full chat URL being pasted in. The relay is an IIS application in a
// subfolder, so the path prefix matters.
bool parseEndpoint(const std::wstring& endpoint, Config& config) {
	URL_COMPONENTS components;
	memset(&components, 0, sizeof(components));

	wchar_t host[256];
	wchar_t urlPath[1024];
	host[0] = 0;
	urlPath[0] = 0;

	components.dwStructSize = sizeof(components);
	components.lpszHostName = host;
	components.dwHostNameLength = static_cast<DWORD>(_countof(host));
	components.lpszUrlPath = urlPath;
	components.dwUrlPathLength = static_cast<DWORD>(_countof(urlPath));

	if (!WinHttpCrackUrl(endpoint.c_str(), static_cast<DWORD>(endpoint.size()), 0, &components)) {
		config.error = "endpoint is not a valid URL";
		return false;
	}

	if (components.nScheme != INTERNET_SCHEME_HTTPS && components.nScheme != INTERNET_SCHEME_HTTP) {
		config.error = "endpoint scheme must be http or https";
		return false;
	}

	config.https = (components.nScheme == INTERNET_SCHEME_HTTPS);
	config.port = components.nPort;
	config.host = host;

	std::wstring prefix(urlPath);

	while (!prefix.empty() && prefix[prefix.size() - 1] == L'/')
		prefix.erase(prefix.size() - 1);

	const wchar_t* fullSuffix = L"/api/v1/chat";
	const wchar_t* versionSuffix = L"/api/v1";
	size_t fullLength = wcslen(fullSuffix);
	size_t versionLength = wcslen(versionSuffix);

	if (prefix.size() >= fullLength && prefix.compare(prefix.size() - fullLength, fullLength, fullSuffix) == 0)
		prefix.erase(prefix.size() - fullLength);
	else if (prefix.size() >= versionLength && prefix.compare(prefix.size() - versionLength, versionLength, versionSuffix) == 0)
		prefix.erase(prefix.size() - versionLength);

	config.path = prefix + fullSuffix;
	config.messagesPath = prefix + L"/api/v1/messages";

	if (config.host.empty()) {
		config.error = "endpoint has no host";
		return false;
	}

	return true;
}

std::string clampLabel(const std::wstring& value) {
	std::string narrow = narrowLossy(value);

	if (narrow.size() > 64)
		narrow.resize(64);

	trimNarrow(narrow);

	return narrow;
}

// The default id must be stable per machine WITHOUT identifying it: hostnames
// frequently embed real names ("JAMES-LAPTOP"), and this value travels with
// every batch and sits in the relay's logs. FNV-1a of the computer name keeps
// the dedupe/diagnostic value and drops the PII; anyone who wants a readable
// id sets client_id in the ini.
std::string defaultClientId() {
	char name[MAX_COMPUTERNAME_LENGTH + 1];
	DWORD size = static_cast<DWORD>(_countof(name));

	if (!GetComputerNameA(name, &size) || size == 0)
		return "unknown-client";

	unsigned long long hash = 14695981039346656037ULL;

	for (DWORD i = 0; i < size; ++i) {
		hash ^= static_cast<unsigned char>(name[i]);
		hash *= 1099511628211ULL;
	}

	char id[32];
	sprintf_s(id, sizeof(id), "client-%08lx%08lx",
		static_cast<unsigned long>(hash >> 32),
		static_cast<unsigned long>(hash & 0xFFFFFFFFUL));

	return id;
}

// Builds a fresh Config from the ini. Caller assigns it under the lock.
Config loadConfigFromDisk() {
	Config config;

	std::wstring path = iniPath();

	if (path.empty()) {
		config.error = "could not locate the DLL directory";
		return config;
	}

	if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
		config.error = "DiscordBridge.ini not found beside the DLL";
		return config;
	}

	// GetPrivateProfileString caches file contents; flush so /emu discord on
	// picks up edits made while the client was running.
	WritePrivateProfileStringW(nullptr, nullptr, nullptr, path.c_str());

	std::wstring endpoint = readIniValue(path, L"endpoint", L"");
	std::wstring key = readIniValue(path, L"key", L"");
	std::wstring enabled = readIniValue(path, L"enabled", L"1");
	std::wstring clientId = readIniValue(path, L"client_id", L"");
	std::wstring character = readIniValue(path, L"character", L"");
	std::wstring galaxy = readIniValue(path, L"galaxy", L"");
	std::wstring channelType = readIniValue(path, L"channel_type", L"");
	std::wstring allowHttp = readIniValue(path, L"allow_http", L"0");
	std::wstring stage2 = readIniValue(path, L"stage2", L"1");

	config.enabled = !(enabled == L"0" || enabled == L"false" || enabled == L"no");
	config.stage2 = !(stage2 == L"0" || stage2 == L"false" || stage2 == L"no");

	if (!channelType.empty()) {
		// Strict parse. _wtoi would turn "guild" (or any typo) into 0 and
		// silently retarget the bridge to channel type 0; a value that is not
		// a plain non-negative number must fail loudly instead.
		wchar_t* parseEnd = nullptr;
		long parsed = wcstol(channelType.c_str(), &parseEnd, 10);

		if (parseEnd == channelType.c_str() || *parseEnd != L'\0' || parsed < 0) {
			config.error = "channel_type in DiscordBridge.ini is not a non-negative number";
			return config;
		}

		config.channelType = static_cast<int>(parsed);
	}

	config.clientId = clampLabel(clientId);
	if (config.clientId.empty())
		config.clientId = defaultClientId();

	config.character = clampLabel(character);
	config.galaxy = clampLabel(galaxy);

	if (endpoint.empty()) {
		config.error = "endpoint is not set in DiscordBridge.ini";
		return config;
	}

	if (key.empty()) {
		config.error = "key is not set in DiscordBridge.ini";
		return config;
	}

	if (!parseEndpoint(endpoint, config))
		return config;

	if (!config.https &&
		!(allowHttp == L"1" || allowHttp == L"true" || allowHttp == L"yes")) {
		// Over plain http the X-Relay-Key header and all captured guild chat
		// travel in cleartext on every hop. Refuse unless explicitly accepted.
		config.error = "endpoint is http:// so the relay key would travel unencrypted; "
			"use https, or set allow_http=1 to accept the risk";
		return config;
	}

	config.key = key;
	config.valid = true;

	return config;
}

// ============================================================================
// HTTP (worker thread)
// ============================================================================

struct HttpResult {
	bool transportError;
	DWORD lastError;
	DWORD statusCode;
	int retryAfterSeconds;
	std::string body;
	std::string stage2Header;   // X-Relay-Stage2, empty when absent

	HttpResult() : transportError(true), lastError(0), statusCode(0), retryAfterSeconds(0) {
	}
};

void closeHandleIfSet(HINTERNET& handle) {
	if (handle != nullptr) {
		WinHttpCloseHandle(handle);
		handle = nullptr;
	}
}

bool openConnection(const Config& config, HINTERNET& session, HINTERNET& connection) {
	session = WinHttpOpen(L"GalaxyExtender-DiscordBridge/1.0",
		WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);

	if (session == nullptr) {
		logLine("WinHttpOpen failed (%lu)", GetLastError());
		return false;
	}

	WinHttpSetTimeouts(session, 5000, 5000, 5000, 8000);

	if (config.https) {
		DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
#ifdef WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3
		protocols |= WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
#endif
		WinHttpSetOption(session, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols));
	}

	connection = WinHttpConnect(session, config.host.c_str(), config.port, 0);

	if (connection == nullptr) {
		logLine("WinHttpConnect failed (%lu)", GetLastError());
		closeHandleIfSet(session);
		return false;
	}

	return true;
}

int parseRetryAfterSeconds(const std::wstring& value) {
	if (value.empty())
		return 0;

	// Delta-seconds form. An HTTP-date is legal too; the relay sends seconds,
	// so anything unparseable falls back to a conservative minute.
	wchar_t* end = nullptr;
	long seconds = wcstol(value.c_str(), &end, 10);

	if (end != value.c_str() && seconds > 0)
		return (seconds > MAX_RETRY_AFTER_SECONDS) ? MAX_RETRY_AFTER_SECONDS : static_cast<int>(seconds);

	return (value[0] == L'0') ? 0 : 60;
}

// Shared tail of both requests: status code, Retry-After, X-Relay-Stage2 and a
// bounded body read. The request handle stays owned by the caller.
void readResponse(HINTERNET request, HttpResult& result, size_t maxBodyBytes) {
	DWORD statusCode = 0;
	DWORD statusSize = sizeof(statusCode);

	if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
			WINHTTP_HEADER_NAME_BY_INDEX, &statusCode, &statusSize, WINHTTP_NO_HEADER_INDEX)) {
		result.lastError = GetLastError();
		return;
	}

	result.transportError = false;
	result.statusCode = statusCode;

	wchar_t retryAfter[64];
	DWORD retrySize = sizeof(retryAfter);

	if (WinHttpQueryHeaders(request, WINHTTP_QUERY_RETRY_AFTER, WINHTTP_HEADER_NAME_BY_INDEX,
			retryAfter, &retrySize, WINHTTP_NO_HEADER_INDEX)) {
		result.retryAfterSeconds = parseRetryAfterSeconds(std::wstring(retryAfter));
	}

	wchar_t stage2[32];
	DWORD stage2Size = sizeof(stage2);
	wchar_t stage2Name[] = L"X-Relay-Stage2";

	if (WinHttpQueryHeaders(request, WINHTTP_QUERY_CUSTOM, stage2Name,
			stage2, &stage2Size, WINHTTP_NO_HEADER_INDEX)) {
		result.stage2Header = narrowLossy(std::wstring(stage2, stage2Size / sizeof(wchar_t)));
		trimNarrow(result.stage2Header);
	}

	for (;;) {
		DWORD available = 0;

		if (!WinHttpQueryDataAvailable(request, &available) || available == 0)
			break;

		if (result.body.size() >= maxBodyBytes)
			break;

		if (available > maxBodyBytes - result.body.size())
			available = static_cast<DWORD>(maxBodyBytes - result.body.size());

		std::vector<char> chunk(available);
		DWORD read = 0;

		if (!WinHttpReadData(request, &chunk[0], available, &read) || read == 0)
			break;

		result.body.append(&chunk[0], read);
	}
}

HttpResult postBatch(HINTERNET connection, const Config& config, const std::string& body) {
	HttpResult result;

	HINTERNET request = WinHttpOpenRequest(connection, L"POST", config.path.c_str(), nullptr,
		WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
		config.https ? WINHTTP_FLAG_SECURE : 0);

	if (request == nullptr) {
		result.lastError = GetLastError();
		return result;
	}

	std::wstring headers = L"Content-Type: application/json; charset=utf-8\r\nX-Relay-Key: ";
	headers += config.key;

	BOOL sent = WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(headers.size()),
		const_cast<char*>(body.c_str()), static_cast<DWORD>(body.size()),
		static_cast<DWORD>(body.size()), 0);

	// Scrub the key from our copy as soon as it is on the wire.
	SecureZeroMemory(&headers[0], headers.size() * sizeof(wchar_t));

	if (!sent || !WinHttpReceiveResponse(request, nullptr)) {
		result.lastError = GetLastError();
		closeHandleIfSet(request);
		return result;
	}

	readResponse(request, result, MAX_RESPONSE_BYTES);
	closeHandleIfSet(request);

	return result;
}

// GET /messages?client=<id> — the Stage 2 claim poll.
HttpResult getMessages(HINTERNET connection, const Config& config) {
	HttpResult result;

	std::wstring path = config.messagesPath;
	path += L"?client=";
	path += urlEncodeQueryValue(config.clientId);

	HINTERNET request = WinHttpOpenRequest(connection, L"GET", path.c_str(), nullptr,
		WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
		config.https ? WINHTTP_FLAG_SECURE : 0);

	if (request == nullptr) {
		result.lastError = GetLastError();
		return result;
	}

	std::wstring headers = L"X-Relay-Key: ";
	headers += config.key;

	BOOL sent = WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(headers.size()),
		WINHTTP_NO_REQUEST_DATA, 0, 0, 0);

	SecureZeroMemory(&headers[0], headers.size() * sizeof(wchar_t));

	if (!sent || !WinHttpReceiveResponse(request, nullptr)) {
		result.lastError = GetLastError();
		closeHandleIfSet(request);
		return result;
	}

	readResponse(request, result, MAX_POLL_RESPONSE_BYTES);
	closeHandleIfSet(request);

	return result;
}

std::string buildBody(const Config& config, const std::string& batchId,
	const std::vector<QueuedLine>& lines) {

	std::string body;
	body.reserve(1024);

	body += "{\"batchId\":";
	appendJsonString(body, batchId);
	body += ",\"client\":{\"id\":";
	appendJsonString(body, config.clientId);

	if (!config.character.empty()) {
		body += ",\"character\":";
		appendJsonString(body, config.character);
	}

	if (!config.galaxy.empty()) {
		body += ",\"galaxy\":";
		appendJsonString(body, config.galaxy);
	}

	body += "},\"lines\":[";

	for (size_t i = 0; i < lines.size(); ++i) {
		if (i != 0)
			body.push_back(',');

		body += "{\"text\":";
		appendJsonString(body, lines[i].text);
		body += ",\"occurrence\":";
		appendInt(body, lines[i].occurrence);
		body += ",\"clientSeq\":";
		appendInt(body, lines[i].clientSeq);
		body.push_back('}');
	}

	body += "]}";

	return body;
}

void recordResult(const char* text) {
	EnterCriticalSection(&s_lock);
	s_lastResult = text;
	s_lastResultTick = GetTickCount64();
	LeaveCriticalSection(&s_lock);
}

// ============================================================================
// Stage 2: claim poll (worker thread)
// ============================================================================

// One element of the /messages array; cursor positioned at its '{'.
bool jsonParseMessage(JsonCursor& c, DiscordBridge::IncomingDiscordMessage& message) {
	if (!jsonConsume(c, '{'))
		return false;

	jsonSkipWs(c);

	if (c.at < c.end && *c.at == '}') {
		++c.at;
		return true;
	}

	for (;;) {
		std::string field;

		if (!jsonParseString(c, field, 64))
			return false;

		if (!jsonConsume(c, ':'))
			return false;

		if (field == "id") {
			if (!jsonParseString(c, message.id, MAX_PARSED_FIELD_BYTES))
				return false;
		} else if (field == "author") {
			if (!jsonParseString(c, message.author, MAX_PARSED_FIELD_BYTES))
				return false;
		} else if (field == "text") {
			if (!jsonParseString(c, message.text, MAX_PARSED_FIELD_BYTES))
				return false;
		} else if (!jsonSkipValue(c, 6)) {
			return false;
		}

		jsonSkipWs(c);

		if (c.at >= c.end)
			return false;

		if (*c.at == ',') {
			++c.at;
			continue;
		}

		if (*c.at == '}') {
			++c.at;
			return true;
		}

		return false;
	}
}

void recordPollResult(const char* text) {
	EnterCriticalSection(&s_lock);
	s_lastPollResult = text;
	s_lastPollResultTick = GetTickCount64();
	LeaveCriticalSection(&s_lock);
}

ULONGLONG pollBackoffDelay(unsigned long consecutiveFailures) {
	ULONGLONG delay = POLL_BACKOFF_BASE_MS;

	for (unsigned long i = 1; i < consecutiveFailures && delay < POLL_BACKOFF_MAX_MS; ++i)
		delay *= 2;

	return delay < POLL_BACKOFF_MAX_MS ? delay : POLL_BACKOFF_MAX_MS;
}

// Runs on every worker wake, shares the batch path's connection. A 200 CLAIMS
// the returned messages for this client (Relay/README.md), so the gates below
// all answer one question: could a claim taken right now actually be injected
// well inside the relay's 60 s window?
void pollStep(HINTERNET& session, HINTERNET& connection, unsigned long& connectionGeneration) {
	if (InterlockedCompareExchange(&s_stage2Fault, 0, 0) != 0)
		return;

	ULONGLONG now = GetTickCount64();

	Config config;
	unsigned long generation = 0;
	bool due = false;

	EnterCriticalSection(&s_lock);

	// s_incoming must be empty: claims are taken at most 5 at a time and fully
	// injected (~5.5 s) before the next batch is claimed.
	if (s_config.valid && s_config.enabled && s_config.stage2 && !s_stopping &&
		now >= s_nextPollTick && s_incoming.empty()) {
		config = s_config;
		generation = s_configGeneration;
		due = true;
	}

	LeaveCriticalSection(&s_lock);

	if (!due)
		return;

	// Frame tick live (ground scene, not zoning) and room id cached (one line
	// typed in the guild tab this session). Silent skip — this is the normal
	// state on the login screen; the next wake re-checks.
	LONGLONG lastFrame = InterlockedCompareExchange64(&s_lastFrameTick, 0, 0);

	if (lastFrame == 0 || now < static_cast<ULONGLONG>(lastFrame) ||
		now - static_cast<ULONGLONG>(lastFrame) > FRAME_STALE_MS)
		return;

	if (!CuiChatParser::hasCachedRoomId())
		return;

	if (connection != nullptr && generation != connectionGeneration) {
		closeHandleIfSet(connection);
		closeHandleIfSet(session);
	}

	if (connection == nullptr) {
		if (!openConnection(config, session, connection)) {
			recordPollResult("cannot open connection to relay");

			EnterCriticalSection(&s_lock);
			s_nextPollTick = now + POLL_INTERVAL_MS;
			LeaveCriticalSection(&s_lock);

			return;
		}

		connectionGeneration = generation;
	}

	HttpResult result = getMessages(connection, config);
	now = GetTickCount64();

	char summary[256];

	if (result.transportError) {
		closeHandleIfSet(connection);
		closeHandleIfSet(session);

		EnterCriticalSection(&s_lock);
		++s_pollFailures;
		unsigned long failures = s_pollFailures;
		s_nextPollTick = now + pollBackoffDelay(failures);
		LeaveCriticalSection(&s_lock);

		sprintf_s(summary, sizeof(summary), "poll network error %lu (failure %lu)",
			result.lastError, failures);
		logLine("%s", summary);
		recordPollResult(summary);

		return;
	}

	if (result.statusCode >= 200 && result.statusCode < 300) {
		std::vector<DiscordBridge::IncomingDiscordMessage> messages;
		long long dropped = 0;

		if (!DiscordBridge::parseMessagesResponse(result.body, messages, dropped)) {
			// The 200 already claimed whatever was in the body; losing the
			// parse costs those messages a redelivery, nothing worse.
			std::string excerpt = result.body;

			if (excerpt.size() > 256)
				excerpt.resize(256);

			logLine("unparseable /messages body: %s", excerpt.empty() ? "(empty)" : excerpt.c_str());

			EnterCriticalSection(&s_lock);
			++s_pollFailures;
			s_nextPollTick = now + pollBackoffDelay(s_pollFailures);
			LeaveCriticalSection(&s_lock);

			recordPollResult("200 with unparseable body (claims will be redelivered)");
			return;
		}

		int relayState = -1;

		if (result.stage2Header == "enabled")
			relayState = 1;
		else if (result.stage2Header == "disabled")
			relayState = 0;

		size_t queued = 0;

		EnterCriticalSection(&s_lock);

		s_pollFailures = 0;
		s_stage2Relay = relayState;
		s_discordDropped += dropped;

		if (!s_stopping && s_config.enabled && s_config.stage2) {
			for (size_t i = 0; i < messages.size() && s_incoming.size() < MAX_INCOMING_MESSAGES; ++i) {
				if (messages[i].text.empty())
					continue;   // nothing to inject; the claim just lapses

				IncomingEntry entry;
				entry.message = messages[i];
				entry.receivedTick = now;

				s_incoming.push_back(entry);
				++queued;
			}
		}

		s_nextPollTick = now + (relayState == 0 ? POLL_DISABLED_INTERVAL_MS : POLL_INTERVAL_MS);

		LeaveCriticalSection(&s_lock);

		if (relayState == 0) {
			recordPollResult("ok - stage 2 disabled on the relay");
		} else if (queued == 0 && dropped == 0) {
			recordPollResult("ok - no messages");
		} else {
			sprintf_s(summary, sizeof(summary), "ok - claimed %u message(s)%s",
				static_cast<unsigned>(queued), dropped > 0 ? ", relay reported missed messages" : "");
			recordPollResult(summary);
		}

		return;
	}

	if (result.statusCode == 401 || result.statusCode == 403) {
		// Same key as /chat, same latch, same recovery (/emu discord on).
		logLine("relay rejected the key on /messages (HTTP %lu) - stopping. Fix 'key' in "
			"DiscordBridge.ini and run /emu discord on.", result.statusCode);

		sprintf_s(summary, sizeof(summary), "%lu rejected key - bridge stopped", result.statusCode);
		recordPollResult(summary);

		InterlockedExchange(&s_authFailed, 1);
		return;
	}

	if (result.statusCode == 429) {
		int seconds = result.retryAfterSeconds > 0 ? result.retryAfterSeconds : 60;

		sprintf_s(summary, sizeof(summary), "429 rate limited, next poll in %ds", seconds);
		recordPollResult(summary);

		EnterCriticalSection(&s_lock);
		s_nextPollTick = now + static_cast<ULONGLONG>(seconds) * 1000;
		LeaveCriticalSection(&s_lock);

		return;
	}

	if (result.statusCode >= 500) {
		EnterCriticalSection(&s_lock);
		++s_pollFailures;
		unsigned long failures = s_pollFailures;
		s_nextPollTick = now + pollBackoffDelay(failures);
		LeaveCriticalSection(&s_lock);

		sprintf_s(summary, sizeof(summary), "%lu server error on poll (failure %lu)",
			result.statusCode, failures);
		recordPollResult(summary);

		return;
	}

	// 400 (client id the relay refuses) or 404 (relay predates the Stage 2
	// endpoint): retrying cannot fix it — latch polling off. Stage 1 keeps
	// running; /emu discord on or /emu discord poll clears the latch.
	std::string excerpt = result.body;

	if (excerpt.size() > 256)
		excerpt.resize(256);

	logLine("/messages rejected with HTTP %lu: %s", result.statusCode,
		excerpt.empty() ? "(no body)" : excerpt.c_str());

	sprintf_s(summary, sizeof(summary),
		"%lu on poll - stage 2 stopped (%s)",
		result.statusCode,
		result.statusCode == 404 ? "relay has no /messages, update the relay" : "see debug log");
	recordPollResult(summary);

	InterlockedExchange(&s_stage2Fault, 1);
}

// ============================================================================
// Stage 2: paced injection (main thread, from onFrame)
// ============================================================================

void drainIncoming(ULONGLONG now) {
	if (now < s_nextInjectTick)
		return;

	if (!CuiChatParser::hasCachedRoomId())
		return;

	IncomingEntry entry;
	bool have = false;

	EnterCriticalSection(&s_lock);

	if (!s_stopping && s_config.valid && s_config.enabled && s_config.stage2 && !s_incoming.empty()) {
		entry = s_incoming.front();
		s_incoming.pop_front();
		have = true;
	}

	LeaveCriticalSection(&s_lock);

	if (!have)
		return;

	if (now - entry.receivedTick > CLAIM_SAFETY_MS) {
		// Injecting this close to the relay's 60 s redelivery would race the
		// next claimant and double-post; let the claim lapse instead. One
		// entry per frame keeps this loop-free — the rest of a stale batch
		// follows on the next frames.
		EnterCriticalSection(&s_lock);
		++s_expiredLocally;
		LeaveCriticalSection(&s_lock);

		return;
	}

	std::string composed = "[Discord] ";
	composed += entry.message.author.empty() ? "discord" : entry.message.author;
	composed += ": ";
	composed += entry.message.text;

	std::wstring wide;

	if (!utf8ToUtf16(composed, wide))
		return;

	// The relay pre-sanitizes (R5); this is defence in depth with the exact
	// strip/clamp rules the capture side uses, so a relay bug cannot smuggle
	// SWG escapes into the room through this client.
	std::wstring line;
	DiscordBridge::cleanChatText(wide.c_str(), wide.size(), line);

	if (line.empty())
		return;

	std::wstring wire;
	wire.reserve(line.size() + 16);
	wire += INJECT_COLOUR_PREFIX;
	wire += line;
	wire += INJECT_COLOUR_SUFFIX;

	bool sent = CuiChatParser::injectRoom(wire.c_str(), wire.size());

	std::string sample = narrowLossy(line);

	if (sample.size() > 60)
		sample.resize(60);

	EnterCriticalSection(&s_lock);

	if (sent)
		++s_injectedLines;

	s_lastInjectedSample = sent ? sample : ("(send failed) " + sample);
	s_lastInjectedTick = now;

	LeaveCriticalSection(&s_lock);

	// Pace unconditionally — a failed send should not burst-retry either.
	s_nextInjectTick = now + INJECT_INTERVAL_MS;
}

DWORD WINAPI workerMain(LPVOID) {
	HINTERNET session = nullptr;
	HINTERNET connection = nullptr;
	unsigned long connectionGeneration = 0;

	std::string pendingBody;
	std::string pendingBatchId;
	size_t pendingLines = 0;
	int attempts = 0;

	for (;;) {
		if (WaitForSingleObject(s_stopEvent, BATCH_INTERVAL_MS) == WAIT_OBJECT_0)
			break;

		if (InterlockedCompareExchange(&s_authFailed, 0, 0) != 0)
			continue;

		// Stage 2 poll first: it is time-gated (≥5 s cadence) and cheap when
		// idle, and running it before the batch logic keeps a busy chat stream
		// from starving it (most batch outcomes `continue` this loop).
		pollStep(session, connection, connectionGeneration);

		if (InterlockedCompareExchange(&s_authFailed, 0, 0) != 0)
			continue;   // the poll may just have latched a bad key

		Config config;
		unsigned long generation = 0;
		std::vector<QueuedLine> lines;
		ULONGLONG now = GetTickCount64();

		EnterCriticalSection(&s_lock);

		bool active = s_config.valid && s_config.enabled && !s_stopping;
		bool ready = active && now >= s_nextSendTick;

		// /emu discord off promises to discard everything queued. That includes
		// the batch this thread had already taken out of the queue — without this
		// it would survive the off/on cycle and be sent after all.
		bool dropPending = !active && !pendingBody.empty();

		if (dropPending)
			s_linesDropped += static_cast<unsigned long>(pendingLines);

		if (ready) {
			config = s_config;
			generation = s_configGeneration;

			if (pendingBody.empty()) {
				size_t bytes = 0;

				while (lines.size() < MAX_LINES_PER_BATCH && !s_queue.empty()) {
					const QueuedLine& front = s_queue.front();
					// text.size() * 2 is the worst-case JSON-escaped length,
					// since control characters are already stripped.
					size_t projected = bytes + front.text.size() * 2 + LINE_JSON_OVERHEAD;

					if (!lines.empty() && projected > MAX_LINES_JSON_BYTES)
						break;

					bytes = projected;
					lines.push_back(front);
					s_queue.pop_front();
				}
			}
		}

		LeaveCriticalSection(&s_lock);

		if (dropPending) {
			pendingBody.clear();
			pendingLines = 0;
			attempts = 0;
		}

		if (!ready)
			continue;

		if (pendingBody.empty()) {
			if (lines.empty())
				continue;

			pendingBatchId = newBatchId();
			pendingBody = buildBody(config, pendingBatchId, lines);
			pendingLines = lines.size();
			attempts = 0;

			if (pendingBody.size() > MAX_BODY_BYTES) {
				// Cannot happen with the estimate above; drop rather than
				// hand the relay something it will reject.
				logLine("dropping oversize batch (%u bytes, %u lines)",
					static_cast<unsigned>(pendingBody.size()), static_cast<unsigned>(pendingLines));
				recordResult("batch dropped: body over 32 KB");

				EnterCriticalSection(&s_lock);
				s_linesDropped += static_cast<unsigned long>(pendingLines);
				LeaveCriticalSection(&s_lock);

				pendingBody.clear();
				continue;
			}
		}

		if (connection != nullptr && generation != connectionGeneration) {
			closeHandleIfSet(connection);
			closeHandleIfSet(session);
		}

		if (connection == nullptr) {
			if (!openConnection(config, session, connection)) {
				recordResult("cannot open connection to relay");
				EnterCriticalSection(&s_lock);
				s_nextSendTick = GetTickCount64() + 10000;
				LeaveCriticalSection(&s_lock);
				continue;
			}
			connectionGeneration = generation;
		}

		HttpResult result = postBatch(connection, config, pendingBody);

		// attempts counts only failures that burn a retry (transport errors and
		// 5xx). 429 deliberately does not: it is the relay pacing us, and letting
		// it inflate the counter would make one subsequent 5xx drop the batch.
		char summary[256];

		if (result.transportError) {
			// Retry the same batchId — a fresh GUID would double-post.
			++attempts;
			bool giveUp = attempts >= MAX_BATCH_ATTEMPTS;

			sprintf_s(summary, sizeof(summary), "network error %lu (attempt %d%s)",
				result.lastError, attempts, giveUp ? ", dropped" : "");
			logLine("%s", summary);
			recordResult(summary);

			closeHandleIfSet(connection);
			closeHandleIfSet(session);

			EnterCriticalSection(&s_lock);
			if (giveUp) {
				s_linesDropped += static_cast<unsigned long>(pendingLines);
			}
			s_nextSendTick = GetTickCount64() + static_cast<ULONGLONG>(BATCH_INTERVAL_MS) * attempts;
			LeaveCriticalSection(&s_lock);

			if (giveUp)
				pendingBody.clear();

			continue;
		}

		if (result.statusCode >= 200 && result.statusCode < 300) {
			long long accepted = jsonNumber(result.body, "accepted", -1);
			long long deduped = jsonNumber(result.body, "deduped", -1);
			long long retryAfterMs = jsonNumber(result.body, "retryAfterMs", 0);

			if (retryAfterMs > static_cast<long long>(MAX_RETRY_AFTER_SECONDS) * 1000)
				retryAfterMs = static_cast<long long>(MAX_RETRY_AFTER_SECONDS) * 1000;

			sprintf_s(summary, sizeof(summary), "%lu accepted=%lld deduped=%lld",
				result.statusCode, accepted, deduped);
			recordResult(summary);

			EnterCriticalSection(&s_lock);
			++s_batchesAccepted;
			s_linesAccepted += static_cast<unsigned long>(accepted > 0 ? accepted : 0);
			if (retryAfterMs > 0)
				s_nextSendTick = GetTickCount64() + static_cast<ULONGLONG>(retryAfterMs);
			LeaveCriticalSection(&s_lock);

			pendingBody.clear();
			continue;
		}

		if (result.statusCode == 401 || result.statusCode == 403) {
			// Bad or missing key. Retrying cannot help, so latch off and say so
			// once — /emu discord on re-reads the ini and clears the latch.
			logLine("relay rejected the key (HTTP %lu) - stopping. Fix 'key' in "
				"DiscordBridge.ini and run /emu discord on.", result.statusCode);

			sprintf_s(summary, sizeof(summary), "%lu rejected key - bridge stopped", result.statusCode);
			recordResult(summary);

			InterlockedExchange(&s_authFailed, 1);

			EnterCriticalSection(&s_lock);
			s_linesDropped += static_cast<unsigned long>(pendingLines + s_queue.size());
			s_queue.clear();
			LeaveCriticalSection(&s_lock);

			pendingBody.clear();
			continue;
		}

		if (result.statusCode == 429) {
			int seconds = result.retryAfterSeconds > 0 ? result.retryAfterSeconds : 60;

			sprintf_s(summary, sizeof(summary), "429 rate limited, retrying in %ds", seconds);
			logLine("%s", summary);
			recordResult(summary);

			EnterCriticalSection(&s_lock);
			s_nextSendTick = GetTickCount64() + static_cast<ULONGLONG>(seconds) * 1000;
			LeaveCriticalSection(&s_lock);

			// Same batch, same batchId — it was never accepted.
			continue;
		}

		if (result.statusCode >= 500) {
			++attempts;
			bool giveUp = attempts >= MAX_BATCH_ATTEMPTS;

			sprintf_s(summary, sizeof(summary), "%lu server error (attempt %d%s)",
				result.statusCode, attempts, giveUp ? ", dropped" : "");
			logLine("%s", summary);
			recordResult(summary);

			EnterCriticalSection(&s_lock);
			if (giveUp)
				s_linesDropped += static_cast<unsigned long>(pendingLines);
			s_nextSendTick = GetTickCount64() + static_cast<ULONGLONG>(BATCH_INTERVAL_MS) * attempts;
			LeaveCriticalSection(&s_lock);

			if (giveUp)
				pendingBody.clear();

			continue;
		}

		// 400 / 413 / anything else in the 4xx range: a contract violation this
		// client will never fix by resending. Log the body — it names the
		// offending field, e.g. lines[1].text — and drop the batch.
		std::string bodyExcerpt = result.body;
		if (bodyExcerpt.size() > 512)
			bodyExcerpt.resize(512);

		logLine("relay rejected batch with HTTP %lu: %s", result.statusCode,
			bodyExcerpt.empty() ? "(no body)" : bodyExcerpt.c_str());

		sprintf_s(summary, sizeof(summary), "%lu rejected batch (see debug log)", result.statusCode);
		recordResult(summary);

		EnterCriticalSection(&s_lock);
		s_linesDropped += static_cast<unsigned long>(pendingLines);
		LeaveCriticalSection(&s_lock);

		pendingBody.clear();
	}

	closeHandleIfSet(connection);
	closeHandleIfSet(session);

	// Signalled as the last real work this thread does. shutdown() waits on
	// this rather than the thread handle: waiting for the thread to *exit*
	// inside DllMain would deadlock on the loader lock via DLL_THREAD_DETACH.
	if (s_doneEvent != nullptr)
		SetEvent(s_doneEvent);

	// Releases startWorker()'s module pin and exits WITHOUT executing another
	// instruction of module code. A plain return would still run this
	// function's epilogue and the CRT thread thunk inside the image — a race
	// against an unloader that saw s_doneEvent and proceeded to unmap.
	FreeLibraryAndExitThread(s_module, 0);
}

// ============================================================================
// Lazy initialisation (main thread)
// ============================================================================

void startWorker() {
	if (s_worker != nullptr)
		return;

	// Events are kept across failed attempts rather than recreated, so a
	// CreateThread failure does not leak a fresh pair on every retry.
	if (s_stopEvent == nullptr)
		s_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (s_doneEvent == nullptr)
		s_doneEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

	if (s_stopEvent == nullptr || s_doneEvent == nullptr) {
		logLine("could not create worker events (%lu)", GetLastError());
		return;
	}

	// Pin the DLL for the worker's lifetime. Without this a FreeLibrary from
	// the injector can unmap the module while the worker sits inside a
	// synchronous WinHTTP call (the per-stage timeouts add up to ~25 s, far
	// beyond any join shutdown() could afford under the loader lock), and the
	// worker then executes unmapped code — a client crash, not a leak. With
	// the pin, an external FreeLibrary while the bridge runs leaves the DLL
	// resident and working; the worker releases the pin as its final act via
	// FreeLibraryAndExitThread, and only then can the unload complete.
	HMODULE pin = nullptr;

	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
			reinterpret_cast<LPCWSTR>(&startWorker), &pin)) {
		logLine("could not pin the module for the worker (%lu)", GetLastError());
		return;
	}

	s_worker = CreateThread(nullptr, 0, workerMain, nullptr, 0, nullptr);

	if (s_worker == nullptr) {
		logLine("could not create worker thread (%lu)", GetLastError());
		// Reference-count decrement only: the injector still holds its own
		// reference, so this cannot unmap the code currently executing.
		FreeLibrary(pin);
	}
}

// Deferred out of DllMain deliberately: ini reads and CreateThread both do
// more than is safe under the loader lock.
bool ensureInitialized() {
	if (s_initialized)
		return true;

	if (!s_lockReady)
		return false;

	s_initialized = true;

	Config config = loadConfigFromDisk();

	EnterCriticalSection(&s_lock);
	s_config = config;
	++s_configGeneration;
	LeaveCriticalSection(&s_lock);

	if (config.valid) {
		logLine("configured for %s (client id '%s', channel type %d, %s)",
			narrowLossy(config.host).c_str(), config.clientId.c_str(), config.channelType,
			config.enabled ? "enabled" : "disabled");
		startWorker();
	} else {
		logLine("inactive: %s", config.error.c_str());
	}

	return true;
}

// ============================================================================
// Capture path (main thread)
// ============================================================================

void noteChannelType(int type, const std::wstring& cleaned) {
	for (size_t i = 0; i < s_observed.size(); ++i) {
		if (s_observed[i].type == type) {
			++s_observed[i].count;
			return;
		}
	}

	if (s_observed.size() >= MAX_OBSERVED_TYPES)
		return;

	ObservedChannel entry;
	entry.type = type;
	entry.count = 1;
	entry.sample = narrowLossy(cleaned);

	if (entry.sample.size() > 60)
		entry.sample.resize(60);

	s_observed.push_back(entry);

	logLine("first line on channel type %d: %s", type, entry.sample.c_str());
}

// One client showing the guild channel in several tabs or windows produces
// several appendText calls for a single message, all inside one dispatch — one
// call per Tab object. The relay cannot tell that from a genuine repeat, so
// collapse it here before occurrence is computed. The tab identity is the
// discriminator: fan-out duplicates arrive on DIFFERENT tabs, while a genuine
// repeat of the same text re-enters through the SAME tab — without this, two
// identical messages landing in one frame would collapse on every guild
// member's client at once and the second would never reach Discord, the exact
// case the occurrence scheme exists to protect. The tick check keeps genuine
// repeats flowing if the frame counter is not advancing
// (GroundScene::parseMessages only ticks in the ground scene); duplicate calls
// within one dispatch are microseconds apart.
bool localDuplicate(const std::string& text, const void* tab, ULONGLONG now) {
	if (text == s_lastRelayedText && s_frameCounter == s_lastRelayedFrame &&
		(now - s_lastRelayedTick) < DEDUPE_WINDOW_MS && tab != s_lastRelayedTab) {
		return true;
	}

	s_lastRelayedText = text;
	s_lastRelayedFrame = s_frameCounter;
	s_lastRelayedTick = now;
	s_lastRelayedTab = tab;

	return false;
}

// How many times this client has seen this exact line in the last 60 s,
// including this one. Every guild member's client sees the same stream, so all
// of them label the first "lol" as 1 and the second as 2 — that is what lets
// the relay collapse cross-client duplicates without eating real repeats.
int computeOccurrence(const std::string& text, ULONGLONG now) {
	while (!s_history.empty() &&
		(now - s_history.front().tick) > OCCURRENCE_WINDOW_MS) {
		s_history.pop_front();
	}

	while (s_history.size() >= MAX_HISTORY_LINES)
		s_history.pop_front();

	int occurrence = 1;

	for (std::deque<HistoryEntry>::const_iterator it = s_history.begin(); it != s_history.end(); ++it) {
		if (it->text == text)
			++occurrence;
	}

	HistoryEntry entry;
	entry.tick = now;
	entry.text = text;
	s_history.push_back(entry);

	return occurrence;
}

void enqueue(const std::string& text, int occurrence) {
	EnterCriticalSection(&s_lock);

	if (!s_stopping) {
		while (s_queue.size() >= MAX_QUEUE_LINES) {
			s_queue.pop_front();
			++s_linesDropped;
		}

		QueuedLine line;
		line.text = text;
		line.occurrence = occurrence;
		line.clientSeq = ++s_clientSeq;

		s_queue.push_back(line);
	}

	LeaveCriticalSection(&s_lock);
}

// ============================================================================
// Display-rewrite helpers (rewriteMarkedLine below)
// ============================================================================

// Length of the SWG escape starting at i, or 0 if in[i] does not start one.
// Same three forms cleanChatText strips: \#RRGGBB, \#., \>NNN.
size_t escapeLength(const wchar_t* in, size_t length, size_t i) {
	if (in[i] != L'\\' || i + 1 >= length)
		return 0;

	wchar_t next = in[i + 1];

	if (next == L'#') {
		if (i + 2 < length && in[i + 2] == L'.')
			return 3;

		if (i + 7 < length &&
			isHexDigit(in[i + 2]) && isHexDigit(in[i + 3]) && isHexDigit(in[i + 4]) &&
			isHexDigit(in[i + 5]) && isHexDigit(in[i + 6]) && isHexDigit(in[i + 7])) {
			return 8;
		}
	} else if (next == L'>') {
		if (i + 4 < length &&
			isDigit(in[i + 2]) && isDigit(in[i + 3]) && isDigit(in[i + 4])) {
			return 5;
		}
	}

	return 0;
}

size_t skipEscapesAndSpaces(const wchar_t* in, size_t length, size_t i) {
	while (i < length) {
		if (in[i] == L' ') {
			++i;
			continue;
		}

		size_t escape = escapeLength(in, length, i);

		if (escape == 0)
			break;

		i += escape;
	}

	return i;
}

} // anonymous namespace

// ============================================================================
// Text helpers
// ============================================================================

void DiscordBridge::cleanChatText(const wchar_t* in, size_t length, std::wstring& out) {
	out.clear();

	if (in == nullptr)
		return;

	out.reserve(length);

	size_t i = 0;

	while (i < length) {
		wchar_t c = in[i];

		if (c == L'\\' && (i + 1) < length) {
			wchar_t next = in[i + 1];

			if (next == L'#') {
				if ((i + 2) < length && in[i + 2] == L'.') {   // \#. — colour reset
					i += 3;
					continue;
				}

				if ((i + 7) < length &&                        // \#RRGGBB
					isHexDigit(in[i + 2]) && isHexDigit(in[i + 3]) && isHexDigit(in[i + 4]) &&
					isHexDigit(in[i + 5]) && isHexDigit(in[i + 6]) && isHexDigit(in[i + 7])) {
					i += 8;
					continue;
				}
			} else if (next == L'>') {
				if ((i + 4) < length &&                        // \>NNN — indent
					isDigit(in[i + 2]) && isDigit(in[i + 3]) && isDigit(in[i + 4])) {
					i += 5;
					continue;
				}
			}
		}

		// Control characters would have to be JSON-escaped and serve no purpose
		// in a Discord message. Mapping them to spaces is deterministic, which
		// is what matters for the relay's cross-client dedupe hash.
		out.push_back((c < 0x20 || c == 0x7F) ? L' ' : c);
		++i;
	}

	trimWide(out);

	if (out.size() > MAX_LINE_CHARS) {
		out.resize(MAX_LINE_CHARS);

		// Never leave a lone high surrogate at the end.
		if (!out.empty() && out[out.size() - 1] >= 0xD800 && out[out.size() - 1] <= 0xDBFF)
			out.erase(out.size() - 1);

		trimWide(out);
	}
}

bool DiscordBridge::utf16ToUtf8(const wchar_t* in, size_t length, std::string& out) {
	out.clear();

	if (in == nullptr || length == 0)
		return true;

	int needed = WideCharToMultiByte(CP_UTF8, 0, in, static_cast<int>(length), nullptr, 0, nullptr, nullptr);

	if (needed <= 0)
		return false;

	out.resize(static_cast<size_t>(needed));

	int written = WideCharToMultiByte(CP_UTF8, 0, in, static_cast<int>(length),
		&out[0], needed, nullptr, nullptr);

	if (written <= 0) {
		out.clear();
		return false;
	}

	out.resize(static_cast<size_t>(written));

	return true;
}

// ============================================================================
// Stage 2 text pieces (exported for the test harness)
// ============================================================================

bool DiscordBridge::parseMessagesResponse(const std::string& body,
	std::vector<IncomingDiscordMessage>& outMessages, long long& outDropped) {

	outMessages.clear();
	outDropped = 0;

	JsonCursor c = { body.c_str(), body.c_str() + body.size() };

	if (!jsonConsume(c, '{'))
		return false;

	jsonSkipWs(c);

	if (c.at < c.end && *c.at == '}')
		return false;   // {} — the contract always carries a messages array

	bool messagesSeen = false;

	for (;;) {
		std::string key;

		if (!jsonParseString(c, key, 64))
			return false;

		if (!jsonConsume(c, ':'))
			return false;

		if (key == "messages") {
			messagesSeen = true;

			if (!jsonConsume(c, '['))
				return false;

			jsonSkipWs(c);

			if (c.at < c.end && *c.at == ']') {
				++c.at;
			} else {
				for (;;) {
					IncomingDiscordMessage message;

					if (!jsonParseMessage(c, message))
						return false;

					if (outMessages.size() < MAX_INCOMING_MESSAGES)
						outMessages.push_back(message);

					jsonSkipWs(c);

					if (c.at >= c.end)
						return false;

					if (*c.at == ',') {
						++c.at;
						continue;
					}

					if (*c.at == ']') {
						++c.at;
						break;
					}

					return false;
				}
			}
		} else if (key == "dropped") {
			jsonSkipWs(c);

			bool negative = false;

			if (c.at < c.end && *c.at == '-') {
				negative = true;
				++c.at;
			}

			if (c.at >= c.end || *c.at < '0' || *c.at > '9')
				return false;

			long long value = 0;

			while (c.at < c.end && *c.at >= '0' && *c.at <= '9') {
				value = value * 10 + (*c.at - '0');
				++c.at;
			}

			outDropped = negative ? -value : value;
		} else if (!jsonSkipValue(c, 8)) {
			return false;
		}

		jsonSkipWs(c);

		if (c.at >= c.end)
			return false;

		if (*c.at == ',') {
			++c.at;
			continue;
		}

		if (*c.at == '}')
			break;

		return false;
	}

	return messagesSeen;
}

bool DiscordBridge::rewriteMarkedLine(const wchar_t* in, size_t length, std::wstring& out) {
	if (in == nullptr || length == 0)
		return false;

	static const wchar_t MARKER[] = L"[Discord] ";
	const size_t markerLength = _countof(MARKER) - 1;

	// Leading colour escapes / indent / spaces stay with the kept prefix.
	size_t i = skipEscapesAndSpaces(in, length, 0);

	// Optional channel tag ("[GuildChat] "). The marker is bracketed too — if
	// the bracket IS the marker there is no sender prefix to strip.
	if (i < length && in[i] == L'[') {
		if (length - i >= markerLength && wcsncmp(in + i, MARKER, markerLength) == 0)
			return false;

		size_t closing = i;

		while (closing < length && in[closing] != L']')
			++closing;

		if (closing >= length || closing - i > 24)
			return false;   // unterminated or implausibly long tag

		i = skipEscapesAndSpaces(in, length, closing + 1);
	}

	size_t senderBegin = i;

	size_t marker = length;

	for (size_t scan = senderBegin; scan + markerLength <= length; ++scan) {
		if (wcsncmp(in + scan, MARKER, markerLength) == 0) {
			marker = scan;
			break;
		}
	}

	if (marker >= length || marker == senderBegin)
		return false;

	// The segment between tag and marker, escapes removed, must be exactly a
	// sender prefix: "Name: " — one colon at the end, nothing bracket-like.
	// Anything else ("Kaelen: check this [Discord] thing") is a genuine chat
	// line and stays untouched. The escapes themselves are kept: colour state
	// carries forward into the rest of the line, so dropping them would render
	// the body in the sender-name colour (and lose the injector's purple).
	std::wstring plain;
	plain.reserve(marker - senderBegin);

	std::wstring keptEscapes;

	for (size_t at = senderBegin; at < marker;) {
		size_t escape = escapeLength(in, marker, at);

		if (escape != 0) {
			keptEscapes.append(in + at, in + at + escape);
			at += escape;
			continue;
		}

		plain.push_back(in[at]);
		++at;
	}

	while (!plain.empty() && plain[plain.size() - 1] == L' ')
		plain.erase(plain.size() - 1);

	if (plain.size() < 2 || plain.size() > MAX_MARKED_SENDER_CHARS)
		return false;

	if (plain[plain.size() - 1] != L':')
		return false;

	for (size_t at = 0; at + 1 < plain.size(); ++at) {
		wchar_t ch = plain[at];

		if (ch == L':' || ch == L'[' || ch == L']' || ch == L'\\')
			return false;
	}

	out.assign(in, in + senderBegin);
	out += keptEscapes;
	out.append(in + marker, in + length);

	return true;
}

bool DiscordBridge::maybeRewriteForDisplay(const void* channelId, const void* chatString, std::wstring& out) {
	if (channelId == nullptr || chatString == nullptr)
		return false;

	if (!s_initialized || !s_config.valid || !s_config.enabled)
		return false;

	int channelType = 0;

	if (!seh_readChannelType(channelId, channelType) || channelType != s_config.channelType)
		return false;

	wchar_t raw[MAX_RAW_CHAT_CHARS];
	size_t rawLength = 0;

	if (!seh_copyChatText(chatString, raw, MAX_RAW_CHAT_CHARS, rawLength) || rawLength == 0)
		return false;

	// A copy that filled the buffer may have been truncated; rewriting it
	// would silently display a shortened line. Leave those alone.
	if (rawLength >= MAX_RAW_CHAT_CHARS)
		return false;

	return rewriteMarkedLine(raw, rawLength, out);
}

// ============================================================================
// Lifecycle
// ============================================================================

void DiscordBridge::initialize(HMODULE selfModule) {
	if (s_lockReady)
		return;

	s_module = selfModule;

	InitializeCriticalSection(&s_lock);
	s_lockReady = true;
}

void DiscordBridge::shutdown(bool processExiting) {
	if (!s_lockReady)
		return;

	if (processExiting) {
		// The process is going away: every other thread has already been
		// TERMINATED — possibly mid-hold on s_lock, so acquiring it here could
		// deadlock the exit path. Touch nothing; the OS reclaims it all.
		return;
	}

	EnterCriticalSection(&s_lock);
	s_stopping = true;
	s_queue.clear();
	s_incoming.clear();
	LeaveCriticalSection(&s_lock);

	if (s_worker != nullptr) {
		if (s_stopEvent != nullptr)
			SetEvent(s_stopEvent);

		// Belt-and-braces: the worker's module pin (startWorker) means a
		// FreeLibrary-driven detach cannot actually reach this point while the
		// worker is alive — the unload only proceeds once the worker has
		// released the pin. If we do time out anyway, leaking the handles and
		// the lock is strictly better than freeing memory the worker still
		// reads.
		if (s_doneEvent == nullptr ||
			WaitForSingleObject(s_doneEvent, WORKER_JOIN_TIMEOUT_MS) != WAIT_OBJECT_0) {
			logLine("worker did not stop in time; leaving resources allocated");
			return;
		}

		CloseHandle(s_worker);
		s_worker = nullptr;
	}

	if (s_stopEvent != nullptr) {
		CloseHandle(s_stopEvent);
		s_stopEvent = nullptr;
	}

	if (s_doneEvent != nullptr) {
		CloseHandle(s_doneEvent);
		s_doneEvent = nullptr;
	}

	s_lockReady = false;
	DeleteCriticalSection(&s_lock);
}

void DiscordBridge::onFrame() {
	++s_frameCounter;

	if (!s_initialized)
		ensureInitialized();

	ULONGLONG now = GetTickCount64();

	// Freshness gate for the worker's claim polling: only ticks in the ground
	// scene, so zoning/loading stops claims from being taken at all.
	InterlockedExchange64(&s_lastFrameTick, static_cast<LONGLONG>(now));

	// Injection composes strings and can throw bad_alloc; trading one Discord
	// message for a client crash is never right (same fence as the capture).
	try {
		drainIncoming(now);
	} catch (...) {
	}
}

// ============================================================================
// Chat capture
// ============================================================================

namespace {

void onChatAppendImpl(const void* tab, const void* channelId, const void* chatString) {
	if (!ensureInitialized())
		return;

	int channelType = 0;

	if (!seh_readChannelType(channelId, channelType))
		return;

	bool interesting = (channelType == s_config.channelType);
	bool newType = true;

	for (size_t i = 0; i < s_observed.size(); ++i) {
		if (s_observed[i].type == channelType) {
			++s_observed[i].count;
			newType = false;
			break;
		}
	}

	// Nothing else to do for channels we are not relaying, unless this is the
	// first time we have seen the type — then clean one line as a sample so
	// /emu discord types can identify it.
	if (!interesting && !newType)
		return;

	wchar_t raw[MAX_RAW_CHAT_CHARS];
	size_t rawLength = 0;

	if (!seh_copyChatText(chatString, raw, MAX_RAW_CHAT_CHARS, rawLength))
		return;

	std::wstring cleaned;
	DiscordBridge::cleanChatText(raw, rawLength, cleaned);

	if (newType)
		noteChannelType(channelType, cleaned);

	if (!interesting)
		return;

	if (!s_config.valid || !s_config.enabled)
		return;

	if (InterlockedCompareExchange(&s_authFailed, 0, 0) != 0)
		return;

	if (cleaned.empty())
		return;   // relay rejects blank text, and it would fail the whole batch

	std::string utf8;

	if (!DiscordBridge::utf16ToUtf8(cleaned.c_str(), cleaned.size(), utf8) || utf8.empty())
		return;

	ULONGLONG now = GetTickCount64();

	if (localDuplicate(utf8, tab, now))
		return;

	enqueue(utf8, computeOccurrence(utf8, now));
}

} // anonymous namespace

void DiscordBridge::onChatAppend(const void* tab, const void* channelId, const void* chatString) {
	if (channelId == nullptr || chatString == nullptr)
		return;

	// The header promises nothing here can throw into the game's chat
	// dispatch. The string work in the impl can throw bad_alloc when the
	// 32-bit address space is exhausted — trading "drop one chat line" for a
	// client crash is never right, so fence it. (C++ EH only: the raw client
	// memory reads are behind the SEH-guarded seh_* functions.)
	try {
		onChatAppendImpl(tab, channelId, chatString);
	} catch (...) {
	}
}

void SwgCuiChatWindowTab::appendText(const ChatChannelId& id, const soe::unicode& text) {
	DiscordBridge::onChatAppend(this, &id, &text);

	// Stage 2 display rewrite (stage2 plan, "Injector-name display"): bridged
	// Discord lines drop the injecting player's sender prefix locally. The
	// capture above already relayed the ORIGINAL line — the relay's ack
	// matching depends on that — only what this client displays changes.
	try {
		std::wstring rewritten;

		if (DiscordBridge::maybeRewriteForDisplay(&id, &text, rewritten)) {
			soe::unicode display(rewritten.c_str(), static_cast<uint32_t>(rewritten.size()));

			originalAppendText::run(this, id, display);
			return;
		}
	} catch (...) {
		// bad_alloc while composing the display copy: fall through and show
		// the original line rather than lose it.
	}

	originalAppendText::run(this, id, text);
}

// ============================================================================
// /emu discord
// ============================================================================

bool DiscordBridge::isEnabled() {
	ensureInitialized();

	return s_config.valid && s_config.enabled && InterlockedCompareExchange(&s_authFailed, 0, 0) == 0;
}

void DiscordBridge::setEnabled(bool value) {
	ensureInitialized();

	if (value) {
		// Re-read the ini so a corrected endpoint or key takes effect without a
		// client restart, and clear the 401 and Stage 2 fault latches.
		Config config = loadConfigFromDisk();
		config.enabled = true;

		EnterCriticalSection(&s_lock);
		s_config = config;
		++s_configGeneration;
		s_stopping = false;
		s_nextSendTick = 0;
		s_nextPollTick = 0;
		s_pollFailures = 0;
		s_stage2Relay = -1;
		LeaveCriticalSection(&s_lock);

		InterlockedExchange(&s_authFailed, 0);
		InterlockedExchange(&s_stage2Fault, 0);

		if (config.valid)
			startWorker();

		return;
	}

	// Off discards both directions: the outbound queue and the claimed-but-not-
	// injected incoming messages. Their claims simply expire on the relay and
	// are redelivered to another client.
	EnterCriticalSection(&s_lock);
	s_config.enabled = false;
	s_queue.clear();
	s_incoming.clear();
	LeaveCriticalSection(&s_lock);
}

void DiscordBridge::requestPollNow() {
	ensureInitialized();

	InterlockedExchange(&s_stage2Fault, 0);

	EnterCriticalSection(&s_lock);
	s_nextPollTick = 0;
	s_pollFailures = 0;
	LeaveCriticalSection(&s_lock);
}

void DiscordBridge::enqueueTestLine() {
	ensureInitialized();

	std::wstring cleaned;
	wchar_t sample[256];

	// Deliberately carries escapes so the strip path is exercised end to end.
	swprintf_s(sample, _countof(sample),
		L"\\#00ff00GalaxyExtender\\>032: \\#ffffffbridge test %llu\\>000",
		static_cast<unsigned long long>(GetTickCount64() / 1000));

	cleanChatText(sample, wcslen(sample), cleaned);

	std::string utf8;

	if (!utf16ToUtf8(cleaned.c_str(), cleaned.size(), utf8) || utf8.empty())
		return;

	ULONGLONG now = GetTickCount64();

	enqueue(utf8, computeOccurrence(utf8, now));
}

void DiscordBridge::appendStatus(std::string& out) {
	ensureInitialized();

	Config config;
	size_t queueDepth = 0;
	std::string lastResult;
	ULONGLONG lastResultTick = 0;
	unsigned long batchesAccepted = 0;
	unsigned long linesAccepted = 0;
	unsigned long linesDropped = 0;
	ULONGLONG nextSendTick = 0;
	bool workerRunning = false;
	size_t incomingDepth = 0;
	std::string lastPollResult;
	ULONGLONG lastPollResultTick = 0;
	int stage2Relay = -1;
	unsigned long injectedLines = 0;
	unsigned long expiredLocally = 0;
	long long discordDropped = 0;
	std::string lastInjectedSample;
	ULONGLONG lastInjectedTick = 0;

	EnterCriticalSection(&s_lock);
	config = s_config;
	queueDepth = s_queue.size();
	lastResult = s_lastResult;
	lastResultTick = s_lastResultTick;
	batchesAccepted = s_batchesAccepted;
	linesAccepted = s_linesAccepted;
	linesDropped = s_linesDropped;
	nextSendTick = s_nextSendTick;
	workerRunning = (s_worker != nullptr);
	incomingDepth = s_incoming.size();
	lastPollResult = s_lastPollResult;
	lastPollResultTick = s_lastPollResultTick;
	stage2Relay = s_stage2Relay;
	injectedLines = s_injectedLines;
	expiredLocally = s_expiredLocally;
	discordDropped = s_discordDropped;
	lastInjectedSample = s_lastInjectedSample;
	lastInjectedTick = s_lastInjectedTick;
	LeaveCriticalSection(&s_lock);

	bool authFailed = InterlockedCompareExchange(&s_authFailed, 0, 0) != 0;
	ULONGLONG now = GetTickCount64();

	char line[512];

	out += "\\#88ccffDiscord bridge status:\\#ffffff\n";

	if (!config.valid) {
		sprintf_s(line, sizeof(line), "  state: \\#ff4444not configured\\#ffffff (%s)\n",
			config.error.empty() ? "no endpoint" : config.error.c_str());
		out += line;
	} else if (authFailed) {
		out += "  state: \\#ff4444stopped\\#ffffff - relay rejected the key. "
			"Fix 'key' in DiscordBridge.ini, then /emu discord on\n";
	} else if (!config.enabled) {
		out += "  state: \\#ffcc00off\\#ffffff\n";
	} else {
		sprintf_s(line, sizeof(line), "  state: \\#00ff00on\\#ffffff (worker %s)\n",
			workerRunning ? "running" : "not started");
		out += line;
	}

	if (config.valid) {
		// Host only — the key never leaves this process in readable form.
		sprintf_s(line, sizeof(line), "  relay host: %s (%s)\n",
			narrowLossy(config.host).c_str(), config.https ? "https" : "http");
		out += line;
	}

	sprintf_s(line, sizeof(line), "  client id: %s%s%s\n",
		config.clientId.c_str(),
		config.character.empty() ? "" : "   character: ",
		config.character.empty() ? "" : config.character.c_str());
	out += line;

	sprintf_s(line, sizeof(line), "  guild channel type: %d   frames: %llu\n",
		config.channelType, static_cast<unsigned long long>(s_frameCounter));
	out += line;

	sprintf_s(line, sizeof(line), "  queue: %u line(s)   60s history: %u   dropped: %lu\n",
		static_cast<unsigned>(queueDepth), static_cast<unsigned>(s_history.size()), linesDropped);
	out += line;

	sprintf_s(line, sizeof(line), "  accepted: %lu line(s) in %lu batch(es)\n",
		linesAccepted, batchesAccepted);
	out += line;

	if (lastResult.empty()) {
		out += "  last result: (nothing sent yet)\n";
	} else {
		sprintf_s(line, sizeof(line), "  last result: %s (%llus ago)\n",
			lastResult.c_str(),
			static_cast<unsigned long long>((now - lastResultTick) / 1000));
		out += line;
	}

	if (nextSendTick > now) {
		sprintf_s(line, sizeof(line), "  sending paused for %llus\n",
			static_cast<unsigned long long>((nextSendTick - now) / 1000));
		out += line;
	}

	// --- Stage 2 (Discord -> game) ---

	bool stage2Fault = InterlockedCompareExchange(&s_stage2Fault, 0, 0) != 0;

	if (!config.stage2) {
		out += "  stage 2 (Discord -> game): \\#ffcc00off\\#ffffff (stage2=0 in DiscordBridge.ini)\n";
	} else if (stage2Fault) {
		out += "  stage 2 (Discord -> game): \\#ff4444stopped\\#ffffff - poll rejected; "
			"\\#88ccff/emu discord poll\\#ffffff to retry\n";
	} else {
		const char* relayState = stage2Relay == 1 ? "\\#00ff00enabled\\#ffffff"
			: stage2Relay == 0 ? "\\#ffcc00disabled\\#ffffff" : "unknown";

		sprintf_s(line, sizeof(line), "  stage 2 (Discord -> game): on   relay: %s   room id: %s\n",
			relayState,
			CuiChatParser::hasCachedRoomId()
				? "cached"
				: "\\#ffcc00not cached - type a line in the guild tab\\#ffffff");
		out += line;
	}

	sprintf_s(line, sizeof(line),
		"  incoming: %u queued   injected: %lu   expired claims: %lu   missed on relay: %lld\n",
		static_cast<unsigned>(incomingDepth), injectedLines, expiredLocally, discordDropped);
	out += line;

	if (!lastPollResult.empty()) {
		sprintf_s(line, sizeof(line), "  last poll: %s (%llus ago)\n",
			lastPollResult.c_str(),
			static_cast<unsigned long long>((now - lastPollResultTick) / 1000));
		out += line;
	}

	if (!lastInjectedSample.empty()) {
		sprintf_s(line, sizeof(line), "  last injected: %s (%llus ago)\n",
			lastInjectedSample.c_str(),
			static_cast<unsigned long long>((now - lastInjectedTick) / 1000));
		out += line;
	}
}

void DiscordBridge::appendChannelTypes(std::string& out) {
	ensureInitialized();

	out += "\\#88ccffObserved chat channel types:\\#ffffff\n";

	if (s_observed.empty()) {
		out += "  (nothing seen yet - chat has to arrive while the DLL is loaded)\n";
		return;
	}

	char line[256];

	for (size_t i = 0; i < s_observed.size(); ++i) {
		const ObservedChannel& entry = s_observed[i];

		sprintf_s(line, sizeof(line), "  type %-3d %6lu line(s)%s  %s\n",
			entry.type, entry.count,
			entry.type == s_config.channelType ? " \\#00ff00<- relayed\\#ffffff" : "",
			entry.sample.c_str());
		out += line;
	}

	sprintf_s(line, sizeof(line),
		"  Relaying type %d. If guild chat shows a different type, set "
		"channel_type in DiscordBridge.ini.\n", s_config.channelType);
	out += line;
}
