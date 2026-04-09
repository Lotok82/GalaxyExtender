#pragma once

#include "IntangibleObject.h"
#include "AutoDeltaVariable.h"

// ============================================================================
// PlayerObject food/drink/meds accessor addresses
// 
// These addresses must be found by reverse-engineering the compiled SWG client
// binary (e.g. using Ghidra or IDA Pro). In PlayerObject.cpp source, the
// functions are at lines 1686-1720. They are trivial accessors that return
// m_food.get(), m_maxFood.get(), etc.
//
// OPTION A: If you find the function addresses, use the runMethod approach.
//           Search near speaksLanguage (0x65FEA0) in the same compilation unit.
//
// OPTION B: If you find the member variable offsets within the PlayerObject
//           instance (via debugger memory inspection), use getMemoryReference 
//           with AutoDeltaVariable. The AutoDeltaVariable stores its current
//           value at internal offset 0xC (see AutoDeltaVariable.h).
//           To find offsets: in a debugger, set a breakpoint on speaksLanguage
//           (0x65FEA0), inspect ECX (the this pointer), then search the memory
//           region for known food/drink values after eating/drinking in-game.
//
// OPTION C: Runtime pattern scan (most robust across builds). Scan the .text
//           section for the byte signature of the getFood function.
// ============================================================================

// --- Set these to 0 until you have the real addresses ---
#define PLAYEROBJECT_GETFOOD_ADDRESS     0x0  // TODO: find via reverse engineering
#define PLAYEROBJECT_GETMAXFOOD_ADDRESS  0x0  // TODO: find via reverse engineering
#define PLAYEROBJECT_GETDRINK_ADDRESS    0x0  // TODO: find via reverse engineering
#define PLAYEROBJECT_GETMAXDRINK_ADDRESS 0x0  // TODO: find via reverse engineering

// --- Alternative: memory offsets of AutoDeltaVariable<int> fields in PlayerObject ---
// Set these if you find the offsets via debugger inspection.
// Usage: getMemoryReference<AutoDeltaVariable<int>>(offset).getCurrent()
// These are consecutive: m_food, m_maxFood, m_drink, m_maxDrink, m_meds, m_maxMeds
#define PLAYEROBJECT_FOOD_FIELD_OFFSET     0x0  // TODO: find via debugger
#define PLAYEROBJECT_MAXFOOD_FIELD_OFFSET  0x0  // TODO: find via debugger
#define PLAYEROBJECT_DRINK_FIELD_OFFSET    0x0  // TODO: find via debugger
#define PLAYEROBJECT_MAXDRINK_FIELD_OFFSET 0x0  // TODO: find via debugger

class PlayerObject : public IntangibleObject {
public:
	PlayerObject() {

	}

	bool speaksLanguage(int langid) const {
		return runMethod<0x65FEA0, bool>(langid);
	}

	// ---- Food/Drink accessors ----
	// Two approaches provided. Use whichever one you can find addresses for.

	// Approach A: Call the client's getter functions directly (preferred if addresses known)
#if PLAYEROBJECT_GETFOOD_ADDRESS != 0
	int getFood() const     { return runMethod<PLAYEROBJECT_GETFOOD_ADDRESS, int>(); }
	int getMaxFood() const  { return runMethod<PLAYEROBJECT_GETMAXFOOD_ADDRESS, int>(); }
	int getDrink() const    { return runMethod<PLAYEROBJECT_GETDRINK_ADDRESS, int>(); }
	int getMaxDrink() const { return runMethod<PLAYEROBJECT_GETMAXDRINK_ADDRESS, int>(); }
#elif PLAYEROBJECT_FOOD_FIELD_OFFSET != 0
	// Approach B: Read the AutoDeltaVariable fields directly from memory
	int getFood() const     { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_FOOD_FIELD_OFFSET).getCurrent(); }
	int getMaxFood() const  { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_MAXFOOD_FIELD_OFFSET).getCurrent(); }
	int getDrink() const    { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_DRINK_FIELD_OFFSET).getCurrent(); }
	int getMaxDrink() const { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_MAXDRINK_FIELD_OFFSET).getCurrent(); }
#else
	// Stub methods - return -1 to indicate addresses not yet configured
	int getFood() const     { return -1; }
	int getMaxFood() const  { return -1; }
	int getDrink() const    { return -1; }
	int getMaxDrink() const { return -1; }
#endif

	bool hasFoodDrinkAddresses() const {
		return (PLAYEROBJECT_GETFOOD_ADDRESS != 0) || (PLAYEROBJECT_FOOD_FIELD_OFFSET != 0);
	}
};