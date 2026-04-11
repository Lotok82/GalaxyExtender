#pragma once

#include "IntangibleObject.h"
#include "AutoDeltaVariable.h"

// ============================================================================
// PlayerObject food/drink field offsets
//
// Discovered via runtime memscan (snapshot -> eat/drink -> diff).
// The client's getter functions (getFood, etc.) are inlined, so there are no
// callable addresses. We read the AutoDeltaVariable<int> fields directly from
// the PlayerObject memory layout instead.
//
// See Documentation/food-drink-research.md for full discovery methodology.
// ============================================================================

// Getter function addresses — unused (inlined by compiler), kept for reference
#define PLAYEROBJECT_GETFOOD_ADDRESS     0x0
#define PLAYEROBJECT_GETMAXFOOD_ADDRESS  0x0
#define PLAYEROBJECT_GETDRINK_ADDRESS    0x0
#define PLAYEROBJECT_GETMAXDRINK_ADDRESS 0x0

// AutoDeltaVariable<int> field offsets from PlayerObject base.
// Value is at offset + 0xC (see AutoDeltaVariable.h).
// Fields are declared consecutively: m_food, m_maxFood, m_drink, m_maxDrink, m_meds, m_maxMeds
#define PLAYEROBJECT_FOOD_FIELD_OFFSET     0x0570  // Confirmed — current value at 0x057C
#define PLAYEROBJECT_MAXFOOD_FIELD_OFFSET  0x0     // TODO: predicted 0x0584, verify via memscan
#define PLAYEROBJECT_DRINK_FIELD_OFFSET    0x0598  // Confirmed — current value at 0x05A4
#define PLAYEROBJECT_MAXDRINK_FIELD_OFFSET 0x0     // TODO: predicted 0x05AC, verify via memscan

class PlayerObject : public IntangibleObject {
public:
	PlayerObject() {

	}

	bool speaksLanguage(int langid) const {
		return runMethod<0x65FEA0, bool>(langid);
	}

#if PLAYEROBJECT_GETFOOD_ADDRESS != 0
	int getFood() const     { return runMethod<PLAYEROBJECT_GETFOOD_ADDRESS, int>(); }
	int getMaxFood() const  { return runMethod<PLAYEROBJECT_GETMAXFOOD_ADDRESS, int>(); }
	int getDrink() const    { return runMethod<PLAYEROBJECT_GETDRINK_ADDRESS, int>(); }
	int getMaxDrink() const { return runMethod<PLAYEROBJECT_GETMAXDRINK_ADDRESS, int>(); }
#elif PLAYEROBJECT_FOOD_FIELD_OFFSET != 0
	int getFood() const     { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_FOOD_FIELD_OFFSET).getCurrent(); }
	int getDrink() const    { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_DRINK_FIELD_OFFSET).getCurrent(); }
#if PLAYEROBJECT_MAXFOOD_FIELD_OFFSET != 0
	int getMaxFood() const  { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_MAXFOOD_FIELD_OFFSET).getCurrent(); }
#else
	int getMaxFood() const  { return 100; }  // TODO: replace when maxFood offset confirmed
#endif
#if PLAYEROBJECT_MAXDRINK_FIELD_OFFSET != 0
	int getMaxDrink() const { return getMemoryReference<AutoDeltaVariable<int>>(PLAYEROBJECT_MAXDRINK_FIELD_OFFSET).getCurrent(); }
#else
	int getMaxDrink() const { return 100; }  // TODO: replace when maxDrink offset confirmed
#endif
#else
	// No addresses configured — return -1 as sentinel
	int getFood() const     { return -1; }
	int getMaxFood() const  { return -1; }
	int getDrink() const    { return -1; }
	int getMaxDrink() const { return -1; }
#endif

	bool hasFoodDrinkAddresses() const {
		return (PLAYEROBJECT_GETFOOD_ADDRESS != 0) || (PLAYEROBJECT_FOOD_FIELD_OFFSET != 0);
	}
};