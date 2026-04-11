#pragma once

#include "Object.h"

class VehicleHoverDynamics : public BaseHookedObject {
public:
	static constexpr int HOVER_HEIGHT_OFFSET = 0x68;

	float getHoverHeight() {
		return getMemoryReference<float>(HOVER_HEIGHT_OFFSET);
	}

	void setHoverHeight(float height) {
		getMemoryReference<float>(HOVER_HEIGHT_OFFSET) = height;
	}
};
