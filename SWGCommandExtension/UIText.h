#pragma once

#include "UIWidget.h"

// SetLocalText address: found via SwgCuiNetStatus::update in Ghidra.
// Called on m_textPing/m_textPacketLoss/m_textFps widgets at 0x0110F580.
#define UITEXT_SETLOCALTEXT_ADDRESS  0x0110F580

class UIText : public UIWidget {
public:
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