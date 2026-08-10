#include "stdafx.h"

#include "MessageQueue.h"
#include "GroundScene.h"
#include "Game.h"
#include "Graphics.h"
#include "InputMap.h"
#include "FoodDrinkMonitor.h"
#include "DiscordBridge.h"
#include "CuiChatParser.h"

void GroundScene::parseMessages(InputMap* map) {
	// Update food/drink values on the net status UI panel
	FoodDrinkMonitor::updateNetStatusUI();

	// Pick up the client's own guild room id (set by the server's guild-room
	// auto-join at login) before the bridge tick below, so Stage 2 injection
	// works from login without a typed line and this frame's drain already
	// sees a fresh id.
	CuiChatParser::pollClientGuildRoomId();

	// Drives the Discord bridge's frame counter (used to collapse the duplicate
	// appendText calls one message produces when several tabs show guild chat)
	// and performs its deferred first-use initialisation.
	DiscordBridge::onFrame();

	MessageQueue* queue = map->getMessageQueue();

	bool reset = false;

	for (uint32_t i = 0; i < queue->getNumberOfMessages(); ++i) {
		int message; float value;

		queue->getMessage(i, &message, &value);

		switch (message) {
		case 125: {
			if (value == 0)
				value = 2;

			setView((int)value, 0);
			reset = true;
			break;
		}
		case 228:
			setView(5, 0);
			reset = true;
			break;
		case 147: {
			static bool solid = true;

			if (solid) {
				solid = false;
				Graphics::setFillMode(0);
			} else {
				solid = true;
				Graphics::setFillMode(1);
			}
			break;
		}
		default:
			break;
		}
	}

	if (reset) {
		map->handleInputReset();
	} else {
		originalParseMessages::run(this, map);
	}
}