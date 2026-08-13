#pragma once

#include "UIBaseObject.h"
#include "UILowerString.h"

// Addresses found by Documentation/tools/find_uivtables.py / verify_uivtables.py.
// UIData does NOT override GetProperty — its vtable slot 13 is
// UIBaseObject::GetProperty, which serves the generic mProperties map where a
// list row's "Text"/"LocalText" live (SuiListBox rows get "Text" set by the
// server; "LocalText" only exists when localization changed the value, so
// read LocalText first and fall back to Text, mirroring UIList::Render).
#define UIDATA_VTABLE_ADDRESS               0x015FAF9C
#define UIBASEOBJECT_GETPROPERTY_ADDRESS    0x010F3BE0 /* vtable slot 13: UIBaseObject::GetProperty(const UILowerString&, UIString&) const */

class UIData : public UIBaseObject {
public:
	constexpr static uint32_t VTABLE = UIDATA_VTABLE_ADDRESS;

	bool GetProperty(const UILowerString& name, soe::unicode& value) const {
		return runMethod<UIBASEOBJECT_GETPROPERTY_ADDRESS, bool, const UILowerString&, soe::unicode&>(name, value);
	}
};
