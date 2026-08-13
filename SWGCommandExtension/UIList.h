#pragma once

#include "UIWidget.h"
#include "UILowerString.h"

// Addresses found by Documentation/tools/find_uivtables.py / verify_uivtables.py
// (RTTI -> vtable dump over the reference exe, slots identified by disassembly:
// SetProperty confirmed via UIList's "SelectedItem." dotted-property prologue,
// GetProperty via the same '.'-scan + mProperties fallback shape).
#define UILIST_VTABLE_ADDRESS       0x015FB2B4
#define UILIST_SETPROPERTY_ADDRESS  0x011390F0 /* vtable slot 11: bool SetProperty(const UILowerString&, const UIString&) */
#define UILIST_GETPROPERTY_ADDRESS  0x01139460 /* vtable slot 13: bool GetProperty(const UILowerString&, UIString&) const */

class UIList : public UIWidget {
public:
	constexpr static uint32_t VTABLE = UILIST_VTABLE_ADDRESS;

	// Property-based access only: "SelectedRow" (select is traffic-free — the
	// server harvests it when Ok/Cancel closes the window), "ScrollLocation",
	// "ScrollExtent", "Size". SetProperty("SelectedRow") does NOT scroll; the
	// caller has to set "ScrollLocation" itself (and clamp it — the property
	// path bypasses ScrollToPoint's clamping).
	bool SetProperty(const UILowerString& name, const soe::unicode& value) {
		return runMethod<UILIST_SETPROPERTY_ADDRESS, bool, const UILowerString&, const soe::unicode&>(name, value);
	}

	bool GetProperty(const UILowerString& name, soe::unicode& value) const {
		return runMethod<UILIST_GETPROPERTY_ADDRESS, bool, const UILowerString&, soe::unicode&>(name, value);
	}
};
