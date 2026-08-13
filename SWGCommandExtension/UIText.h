#pragma once

#include "UIWidget.h"
#include "UILowerString.h"

// SetLocalText address: found via SwgCuiNetStatus::update in Ghidra.
// Called on m_textPing/m_textPacketLoss/m_textFps widgets at 0x0110F580.
#define UITEXT_SETLOCALTEXT_ADDRESS  0x0110F580

// Found by Documentation/tools/find_uivtables.py / verify_uivtables.py.
#define UITEXT_VTABLE_ADDRESS        0x015FA1D4
#define UITEXT_GETPROPERTY_ADDRESS   0x01111F50 /* vtable slot 13: bool GetProperty(const UILowerString&, UIString&) const */

class UIText : public UIWidget {
public:
	constexpr static uint32_t VTABLE = UITEXT_VTABLE_ADDRESS;

	// "Text" returns the raw server-sent value (e.g. "@guild:members_title"),
	// "LocalText" the localized display string ("GUILD MEMBERS").
	bool GetProperty(const UILowerString& name, soe::unicode& value) const {
		return runMethod<UITEXT_GETPROPERTY_ADDRESS, bool, const UILowerString&, soe::unicode&>(name, value);
	}

	void AppendLocalText(const soe::unicode& str) {
		runMethod<0x0110FB40, void, const soe::unicode&>(str);
	}

	void SetPreLocalized(bool preLocalized) {
		runMethod<0x01112660, void>(preLocalized);
	}

#if UITEXT_SETLOCALTEXT_ADDRESS != 0
	void SetLocalText(const soe::unicode& str) {
		runMethod<UITEXT_SETLOCALTEXT_ADDRESS, void, const soe::unicode&>(str);
	}

	bool hasSetLocalText() const { return true; }
#else
	void SetLocalText(const soe::unicode& str) {
		// Stub — address not yet found
	}

	bool hasSetLocalText() const { return false; }
#endif
};