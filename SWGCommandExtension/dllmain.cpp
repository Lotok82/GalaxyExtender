// dllmain.cpp : Defines the entry point for the DLL application.
#include "stdafx.h"

#include <windows.h>
#include <detours.h>

#include "CreatureObject.h"
#include "CuiMediatorFactory.h"
#include "CuiChatParser.h"
#include "Game.h"
#include "TerrainObject.h"
#include "SwgCuiLoginScreen.h"
#include "SwgCuiCommandParserDefault.h"
#include "SwgCuiMediatorFactorySetup.h"
#include "SwgCuiChatWindowTab.h"
#include "FoodDrinkMonitor.h"
#include "DiscordBridge.h"

using namespace std;

/// Memory Utilties
///
void writeJmp(BYTE* address, DWORD jumpTo, DWORD length) {
	DWORD oldProtect, newProtect, relativeAddress;

	VirtualProtect(address, length, PAGE_EXECUTE_READWRITE, &oldProtect);

	relativeAddress = (DWORD)(jumpTo - (DWORD)address) - 5;
	*address = 0xE9;
	*((DWORD *)(address + 0x1)) = relativeAddress;

	for (DWORD x = 0x5; x < length; x++)
	{
		*(address + x) = 0x90;
	}

	VirtualProtect(address, length, oldProtect, &newProtect);
}

void writeBytes(BYTE* address, const BYTE* values, int size) {
	DWORD oldProtect, newProtect;

	VirtualProtect(address, size, PAGE_EXECUTE_READWRITE, &oldProtect);

	memcpy(address, values, size);

	VirtualProtect(address, size, oldProtect, &newProtect);
}

#define ATTACH_HOOK(METHOD) METHOD##_hook_t::hookStorage_t::newMethod = &METHOD; \
											DetourAttach((PVOID*) &METHOD##_hook_t::hookStorage_t::original, (PVOID) METHOD##_hook_t::callHook);

#define DETACH_HOOK(METHOD) DetourDetach((PVOID*) &METHOD##_hook_t::hookStorage_t::original, (PVOID) METHOD##_hook_t::callHook);
	

BOOL APIENTRY DllMain(HANDLE hModule, DWORD dwReason, LPVOID lpReserved)
{
	switch (dwReason)
	{
	case DLL_PROCESS_ATTACH: {
		DetourRestoreAfterWith();

		// Only creates the bridge's lock and remembers the module handle (used
		// to find DiscordBridge.ini beside the DLL). Config reads and the
		// WinHTTP worker start on the first frame, outside the loader lock.
		DiscordBridge::initialize((HMODULE)hModule);

		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());

		ATTACH_HOOK(SwgCuiLoginScreen::onButtonPressed);
		ATTACH_HOOK(CuiChatParser::parse);
		ATTACH_HOOK(TerrainObject::setHighLevelOfDetailThresholdHook);
		ATTACH_HOOK(TerrainObject::setLevelOfDetailThresholdHook);
		ATTACH_HOOK(GroundScene::parseMessages);
		ATTACH_HOOK(SwgCuiMediatorFactorySetup::install);
		ATTACH_HOOK(SwgCuiChatWindowTab::appendText);

		LONG errorCode = DetourTransactionCommit();

		if (errorCode == NO_ERROR) {
			// NOP out 7-byte terrain detail clamp at 0xC8D258, removing the
			// engine's hard cap on terrain LOD so /emu globaldetail can exceed it.
			const BYTE newData[7] = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };
			writeBytes((BYTE*)0xC8D258, newData, 7);

			Game::debugPrintUi("Use /console for details on extension command usage.");
			FoodDrinkMonitor::initialize();
		} else {
			Game::debugPrintUi("[LOAD] FAILED for CommandExtensions");
		}

		break;
	}
	case DLL_PROCESS_DETACH:
		FoodDrinkMonitor::shutdown();

		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());

		DETACH_HOOK(SwgCuiLoginScreen::onButtonPressed);
		DETACH_HOOK(CuiChatParser::parse);
		DETACH_HOOK(TerrainObject::setHighLevelOfDetailThresholdHook);
		DETACH_HOOK(TerrainObject::setLevelOfDetailThresholdHook);
		DETACH_HOOK(GroundScene::parseMessages);
		DETACH_HOOK(SwgCuiChatWindowTab::appendText);

		DetourTransactionCommit();

		// After the detach commit, so no new appendText call can enter the bridge
		// while it tears down its lock. lpReserved is non-null when the process is
		// exiting rather than being unloaded by FreeLibrary — in that case every
		// other thread is already gone and nothing may be waited on or freed.
		DiscordBridge::shutdown(lpReserved != nullptr);
		break;
	}

	return TRUE;
}

