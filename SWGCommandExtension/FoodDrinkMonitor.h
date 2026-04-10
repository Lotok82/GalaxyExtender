#pragma once

#include "Object.h"
#include "soewrappers.h"
#include <cstdint>

class UIText;

// ============================================================================
// FoodDrinkMonitor — memory scanning tools & net status UI updater
//
// 1. MEMORY SCANNER: Helps discover PlayerObject field offsets at runtime.
//    Usage flow:
//      /emu memscan snapshot     — save PlayerObject memory state
//      (eat food or drink in-game)
//      /emu memscan diff         — show which offsets changed
//      /emu memscan search <val> — find a specific int32 in PlayerObject memory
//      /emu memscan delta <off>  — read AutoDeltaVariable<int> at offset
//
// 2. NET STATUS UI: Updates the food/drink text on the network monitor panel
//    each frame, reading directly from PlayerObject compiled-in offsets.
// ============================================================================

class PlayerObject;

class FoodDrinkMonitor {
public:
	// --- Initialization ---
	static void initialize();
	static void shutdown();

	// --- Memory scanning tools ---
	// All operate on the PlayerObject instance from Game::getPlayerObject().

	static bool takeSnapshot();
	static bool hasSnapshot();
	static int diffSnapshot(soe::unicode& result);
	static int searchValue(int value, soe::unicode& result);
	static void readDelta(int offset, soe::unicode& result);
	static void dumpMemory(int offset, int byteCount, soe::unicode& result);

	// --- Net status UI update ---
	// Updates food/drink text widgets on the network monitor panel.
	// Called from GroundScene::parseMessages each frame.
	static void updateNetStatusUI();

	// --- Constants ---
	static const size_t SCAN_RANGE = 0x2000; // 8KB scan window

private:
	// Memory snapshot for diffing
	static uint8_t* s_snapshot;
	static size_t s_snapshotSize;

	// Net status UI widget cache
	static UIText* s_foodText;
	static UIText* s_drinkText;
	static bool s_uiLookupDone;
	static int s_lastDisplayedFood;
	static int s_lastDisplayedDrink;
	static int s_uiLookupAttempts;
};
