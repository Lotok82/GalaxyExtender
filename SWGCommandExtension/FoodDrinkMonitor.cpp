#include "stdafx.h"
#include "FoodDrinkMonitor.h"
#include "PlayerObject.h"
#include "AutoDeltaVariable.h"
#include "Game.h"
#include "UIManager.h"
#include "UIPage.h"
#include "UIText.h"
#include "CuiMediatorFactory.h"

// --- Static member initialization ---
uint8_t* FoodDrinkMonitor::s_snapshot = nullptr;
size_t FoodDrinkMonitor::s_snapshotSize = 0;

UIText* FoodDrinkMonitor::s_foodText = nullptr;
UIText* FoodDrinkMonitor::s_drinkText = nullptr;
bool FoodDrinkMonitor::s_uiLookupDone = false;
int FoodDrinkMonitor::s_lastDisplayedFood = -1;
int FoodDrinkMonitor::s_lastDisplayedDrink = -1;
int FoodDrinkMonitor::s_uiLookupAttempts = 0;
void* FoodDrinkMonitor::s_lastPlayerObject = nullptr;

// ============================================================================
// SEH helper functions — POD-only, safe to use __try/__except
// These are separated because MSVC forbids __try in functions with C++ objects.
// ============================================================================

namespace {

bool seh_readInt(void* base, int offset, int& outValue) {
	__try {
		outValue = *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(base) + offset);
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

bool seh_readByte(void* base, int offset, uint8_t& outValue) {
	__try {
		outValue = reinterpret_cast<uint8_t*>(base)[offset];
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

bool seh_memcpy(void* dest, const void* src, size_t size) {
	__try {
		memcpy(dest, src, size);
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

} // anonymous namespace

// ============================================================================
// Initialization / Shutdown
// ============================================================================

void FoodDrinkMonitor::initialize() {
	s_snapshot = nullptr;
	s_snapshotSize = 0;
	s_foodText = nullptr;
	s_drinkText = nullptr;
	s_uiLookupDone = false;
	s_lastDisplayedFood = -1;
	s_lastDisplayedDrink = -1;
	s_uiLookupAttempts = 0;
	s_lastPlayerObject = nullptr;
}

void FoodDrinkMonitor::shutdown() {
	if (s_snapshot) {
		delete[] s_snapshot;
		s_snapshot = nullptr;
		s_snapshotSize = 0;
	}
	s_foodText = nullptr;
	s_drinkText = nullptr;
	s_uiLookupDone = false;
}

// ============================================================================
// Memory Scanner — Snapshot
// ============================================================================

bool FoodDrinkMonitor::takeSnapshot() {
	PlayerObject* playerObject = Game::getPlayerObject();
	if (!playerObject)
		return false;

	if (!s_snapshot) {
		s_snapshot = new uint8_t[SCAN_RANGE];
	}

	if (seh_memcpy(s_snapshot, reinterpret_cast<uint8_t*>(playerObject), SCAN_RANGE)) {
		s_snapshotSize = SCAN_RANGE;
		return true;
	}

	s_snapshotSize = 0;
	return false;
}

bool FoodDrinkMonitor::hasSnapshot() {
	return s_snapshot != nullptr && s_snapshotSize > 0;
}

// ============================================================================
// Memory Scanner — Diff snapshot vs current
// ============================================================================

int FoodDrinkMonitor::diffSnapshot(soe::unicode& result) {
	if (!hasSnapshot()) {
		result += L"No snapshot taken. Use /emu memscan snapshot first.";
		return 0;
	}

	PlayerObject* playerObject = Game::getPlayerObject();
	if (!playerObject) {
		result += L"PlayerObject is null.";
		return 0;
	}

	int matchCount = 0;
	char line[256];

	result += L"\\#88ccff[Memory Diff] Changed int32 offsets:\\#ffffff\n";

	for (size_t offset = 0; offset + 4 <= s_snapshotSize; offset += 4) {
		int oldVal = *reinterpret_cast<int*>(s_snapshot + offset); // snapshot is our buffer, always safe
		int newVal;

		if (!seh_readInt(playerObject, (int)offset, newVal))
			break;

		if (oldVal != newVal) {
			sprintf_s(line, sizeof(line), "  0x%04X: %d -> %d", (unsigned)offset, oldVal, newVal);
			result += line;

			// Check if this could be the +0xC field of an AutoDeltaVariable
			if (offset >= 0xC) {
				int lastVal;
				if (seh_readInt(playerObject, (int)(offset + 4), lastVal) && lastVal == oldVal) {
					sprintf_s(line, sizeof(line),
						" \\#00ff00<-- likely AutoDelta at base 0x%04X\\#ffffff",
						(unsigned)(offset - 0xC));
					result += line;
				}
			}

			result += L"\n";
			matchCount++;

			if (matchCount >= 50) {
				result += L"  ... (truncated, 50+ changes found)\n";
				break;
			}
		}
	}

	if (matchCount == 0) {
		result += L"  No changes detected.\n";
	}

	sprintf_s(line, sizeof(line), "Total: %d changed offsets", matchCount);
	result += line;

	return matchCount;
}

// ============================================================================
// Memory Scanner — Search for a specific int32 value
// ============================================================================

int FoodDrinkMonitor::searchValue(int value, soe::unicode& result) {
	PlayerObject* playerObject = Game::getPlayerObject();
	if (!playerObject) {
		result += L"PlayerObject is null.";
		return 0;
	}

	int matchCount = 0;
	char line[256];

	sprintf_s(line, sizeof(line), "\\#88ccff[Memory Search] Looking for int32 value %d (0x%08X):\\#ffffff\n",
		value, (unsigned)value);
	result += line;

	for (size_t offset = 0; offset + 4 <= SCAN_RANGE; offset += 4) {
		int readVal;

		if (!seh_readInt(playerObject, (int)offset, readVal))
			break;

		if (readVal == value) {
			sprintf_s(line, sizeof(line), "  0x%04X: %d", (unsigned)offset, readVal);
			result += line;

			if (offset >= 0xC) {
				int nextVal;
				if (seh_readInt(playerObject, (int)(offset + 4), nextVal) && nextVal == value) {
					sprintf_s(line, sizeof(line),
						" \\#00ff00(+0x04 also matches — possible AutoDelta base: 0x%04X)\\#ffffff",
						(unsigned)(offset - 0xC));
					result += line;
				}
			}

			result += L"\n";
			matchCount++;

			if (matchCount >= 40) {
				result += L"  ... (truncated, 40+ matches)\n";
				break;
			}
		}
	}

	if (matchCount == 0) {
		result += L"  No matches found in scan range.\n";
	}

	return matchCount;
}

// ============================================================================
// Memory Scanner — Read AutoDeltaVariable<int> at specific offset
// ============================================================================

void FoodDrinkMonitor::readDelta(int offset, soe::unicode& result) {
	PlayerObject* playerObject = Game::getPlayerObject();
	if (!playerObject) {
		result += L"PlayerObject is null.";
		return;
	}

	int currentVal, lastVal;

	if (!seh_readInt(playerObject, offset + 0xC, currentVal) ||
		!seh_readInt(playerObject, offset + 0x10, lastVal)) {
		result += L"Access violation reading that offset.";
		return;
	}

	char line[256];
	sprintf_s(line, sizeof(line),
		"\\#88ccff[AutoDelta @ 0x%04X]\\#ffffff current: %d (0x%08X)  last: %d (0x%08X)",
		(unsigned)offset, currentVal, (unsigned)currentVal, lastVal, (unsigned)lastVal);
	result += line;
}

// ============================================================================
// Memory Scanner — Raw hex dump
// ============================================================================

void FoodDrinkMonitor::dumpMemory(int offset, int byteCount, soe::unicode& result) {
	PlayerObject* playerObject = Game::getPlayerObject();
	if (!playerObject) {
		result += L"PlayerObject is null.";
		return;
	}

	if (byteCount <= 0)
		byteCount = 64;
	if (byteCount > 512)
		byteCount = 512;

	char line[128];

	sprintf_s(line, sizeof(line), "\\#88ccff[Hex Dump] PlayerObject + 0x%04X, %d bytes:\\#ffffff\n",
		(unsigned)offset, byteCount);
	result += line;

	for (int i = 0; i < byteCount; i += 16) {
		sprintf_s(line, sizeof(line), "  0x%04X: ", (unsigned)(offset + i));
		result += line;

		// Hex bytes
		char hexPart[64] = { 0 };
		char asciiPart[20] = { 0 };
		int hexPos = 0;
		int asciiPos = 0;

		for (int j = 0; j < 16 && (i + j) < byteCount; j++) {
			uint8_t byte;
			if (seh_readByte(playerObject, offset + i + j, byte)) {
				hexPos += sprintf_s(hexPart + hexPos, sizeof(hexPart) - hexPos, "%02X ", byte);
				asciiPart[asciiPos++] = (byte >= 0x20 && byte < 0x7F) ? (char)byte : '.';
			} else {
				hexPos += sprintf_s(hexPart + hexPos, sizeof(hexPart) - hexPos, "?? ");
				asciiPart[asciiPos++] = '.';
			}
		}
		asciiPart[asciiPos] = 0;

		result += hexPart;
		result += L" ";
		result += asciiPart;
		result += L"\n";
	}
}

// ============================================================================
// Net Status UI Update
// ============================================================================

void FoodDrinkMonitor::updateNetStatusUI() {
#if UITEXT_SETLOCALTEXT_ADDRESS == 0
	return; // SetLocalText not available yet
#else
	// Detect character switch — PlayerObject pointer changes when relogging
	PlayerObject* player = Game::getPlayerObject();
	if (player != s_lastPlayerObject) {
		s_lastPlayerObject = player;
		s_foodText = nullptr;
		s_drinkText = nullptr;
		s_uiLookupDone = false;
		s_lastDisplayedFood = -1;
		s_lastDisplayedDrink = -1;
		s_uiLookupAttempts = 0;
	}

	if (!player)
		return;

	// Lazy lookup — find the food/drink text widgets, retry until found
	if (!s_uiLookupDone) {
		// Throttle: only attempt every 60 frames (~1 second) to avoid overhead
		if (++s_uiLookupAttempts % 60 != 1)
			return;

		// The net status panel is a workspace mediator (duplicateOnly=true).
		// The template at "pda.netStatus" is NOT the visible clone.
		// We must get the active mediator and access its cloned page at offset +0x14.
		CuiMediator* mediator = CuiMediatorFactory::get("WS_NetStatus", false);
		if (!mediator)
			return;

		// CuiMediator stores its UIPage* at offset 0x14
		UIPage* page = mediator->getMemoryReference<UIPage*>(0x14);
		if (!page)
			return;

		UIBaseObject* foodObj = page->GetObjectFromPath("comp.food.text");
		UIBaseObject* drinkObj = page->GetObjectFromPath("comp.drink.text");

		if (foodObj) {
			s_foodText = reinterpret_cast<UIText*>(foodObj);
			s_foodText->SetPreLocalized(true);
		}
		if (drinkObj) {
			s_drinkText = reinterpret_cast<UIText*>(drinkObj);
			s_drinkText->SetPreLocalized(true);
		}

		// If both found, stop retrying
		if (s_foodText && s_drinkText)
			s_uiLookupDone = true;

		// Give up after ~10 seconds (600 frames) to avoid searching forever
		if (s_uiLookupAttempts > 600)
			s_uiLookupDone = true;
	}

	if (!s_foodText && !s_drinkText)
		return;

	int food = player->getFood();
	int maxFood = player->getMaxFood();
	int drink = player->getDrink();
	int maxDrink = player->getMaxDrink();

	// Clamp to 0 — server can send -1 during decay ticks
	if (food < 0) food = 0;
	if (drink < 0) drink = 0;

	// Only update the widget text when values change
	char buf[32];

	if (s_foodText && (food != s_lastDisplayedFood)) {
		s_lastDisplayedFood = food;
		sprintf_s(buf, sizeof(buf), "%d/%d", food, maxFood);
		s_foodText->SetLocalText(soe::unicode(buf));
	}

	if (s_drinkText && (drink != s_lastDisplayedDrink)) {
		s_lastDisplayedDrink = drink;
		sprintf_s(buf, sizeof(buf), "%d/%d", drink, maxDrink);
		s_drinkText->SetLocalText(soe::unicode(buf));
	}
#endif
}
