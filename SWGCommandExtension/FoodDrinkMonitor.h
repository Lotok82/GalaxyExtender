#pragma once

#include "Object.h"
#include "soewrappers.h"
#include <cstdint>

class UIText;

// ============================================================================
// FoodDrinkMonitor — runtime food/drink value cache & memory scanning tools
//
// This class provides two main capabilities:
//
// 1. MEMORY SCANNER: Helps discover PlayerObject field offsets at runtime.
//    Usage flow:
//      /emu memscan snapshot     — save PlayerObject memory state
//      (eat food or drink in-game)
//      /emu memscan diff         — show which offsets changed
//      /emu memscan search <val> — find a specific int32 in PlayerObject memory
//      /emu memscan delta <off>  — read AutoDeltaVariable<int> at offset
//
// 2. RUNTIME MONITOR: Once offsets are discovered, configure them without
//    recompiling:
//      /emu monitor setoffsets <food> <maxFood> <drink> <maxDrink>
//      /emu monitor on
//    Then /emu stomach|food|drink reads from the cached values.
//
// The monitor polls from GroundScene::parseMessages (once per frame) when
// enabled and offsets are configured.
//
// Future: Hook DeltasMessage handler for PLAY9 to get push-based updates.
// ============================================================================

class PlayerObject;

class FoodDrinkMonitor {
public:
	// --- Initialization ---
	static void initialize();
	static void shutdown();

	// --- Runtime offset configuration ---
	// Offsets are from the PlayerObject base pointer to the AutoDeltaVariable<int>.
	// The actual int value is read at (offset + 0xC) per AutoDeltaVariable layout.
	static void setOffsets(int food, int maxFood, int drink, int maxDrink);
	static bool isConfigured();

	// --- Monitoring ---
	static void enable();
	static void disable();
	static bool isEnabled();

	// Called from GroundScene::parseMessages each frame.
	// Reads current values from PlayerObject memory if configured.
	// Returns true if any value changed since last poll.
	static bool poll();

	// --- Cached value getters ---
	static int getFood();
	static int getMaxFood();
	static int getDrink();
	static int getMaxDrink();

	// --- Memory scanning tools ---
	// All operate on the PlayerObject instance from Game::getPlayerObject().
	// Results are written to resultBuffer. Returns number of matches found.

	// Save a snapshot of PlayerObject memory (first SCAN_RANGE bytes).
	static bool takeSnapshot();
	static bool hasSnapshot();

	// Compare current memory to the saved snapshot.
	// Reports offsets where int32-aligned values changed.
	static int diffSnapshot(soe::unicode& result);

	// Search PlayerObject memory for a specific int32 value.
	// Reports all 4-byte-aligned offsets that contain the value.
	static int searchValue(int value, soe::unicode& result);

	// Read the AutoDeltaVariable<int> at a specific offset.
	// Shows current value (at +0xC) and last value (at +0x10).
	static void readDelta(int offset, soe::unicode& result);

	// Hex dump of raw bytes starting at an offset from PlayerObject base.
	static void dumpMemory(int offset, int byteCount, soe::unicode& result);

	// --- Net status UI update ---
	// Updates the food/drink text widgets on the network monitor panel.
	// Called from GroundScene::parseMessages each frame.
	// Looks up widgets lazily on first call, caches pointers.
	static void updateNetStatusUI();

	// Debug: report UI lookup status
	static bool isUIFound();
	static int getUILookupAttempts();

	// --- Constants ---
	static const size_t SCAN_RANGE = 0x2000; // 8KB scan window

private:
	// Cached food/drink values (updated by poll())
	static int s_food;
	static int s_maxFood;
	static int s_drink;
	static int s_maxDrink;

	// Runtime-configured field offsets (to AutoDeltaVariable<int> base)
	static int s_foodOffset;
	static int s_maxFoodOffset;
	static int s_drinkOffset;
	static int s_maxDrinkOffset;

	// State
	static bool s_enabled;
	static bool s_offsetsConfigured;

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

	// Helper: safely read PlayerObject memory at offset as int32
	static bool safeReadInt(PlayerObject* obj, int offset, int& outValue);
};
