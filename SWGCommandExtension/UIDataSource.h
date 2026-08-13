#pragma once

#include "UIBaseObject.h"

// Addresses found by Documentation/tools/find_uivtables.py / verify_uivtables.py.
#define UIDATASOURCE_VTABLE_ADDRESS        0x015FAE7C
#define UIDATASOURCE_GETCHILDCOUNT_ADDRESS 0x01131AE0 /* vtable slot 21: unsigned long GetChildCount() const — counts the child list nodes */

// A UIDataSource holds the row UIData objects of a UIList (e.g. the SUI list
// box's "List.dataList"). Rows are UIData children; Core3 names them by their
// menu index ("0", "1", ...), so GetObjectFromPath("<n>") is the row accessor.
class UIDataSource : public UIBaseObject {
public:
	constexpr static uint32_t VTABLE = UIDATASOURCE_VTABLE_ADDRESS;

	unsigned long GetChildCount() const {
		return runMethod<UIDATASOURCE_GETCHILDCOUNT_ADDRESS, unsigned long>();
	}
};
