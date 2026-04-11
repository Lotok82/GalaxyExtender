# GalaxyExtender — Architecture & Technical Reference

## Overview

SWGCommandExtension is a **DLL injected into the running SWG client process** (SwgClient_r.exe) using **Microsoft Detours**. It extends the client with new slash commands and overrides by hooking existing client functions at hardcoded binary addresses, reading/writing client memory directly, and navigating the client's UI widget tree.

The DLL is a Win32 (x86, 32-bit) Dynamic Library built with MSVC v143 (VS 2022 toolset).

---

## Reference Projects

| Project | Path | Notes |
|---------|------|-------|
| **GalaxyExtender** | `D:\Galaxies\GalaxyExtender\SWGCommandExtension` | This project — the injected DLL |
| **Client source (forked)** | `D:\Galaxies\SWGClient\client-tools` | A fork of the SWG client source. **Diverged from our binary** — code structure and class layouts are useful as reference, but offsets, function addresses, and some logic may differ. Do not assume 1:1 correspondence. The actual client binary we inject into has no matching source code. |
| **Server (Core3)** | `D:\Galaxies\Core3\Core3` | SWGEmu server code. Useful for understanding baselines/deltas, network message formats (e.g. PLAY9), object data flow, and server-side logic that affects client state. |
| **Game files clone** | `D:\Galaxies\SWGEmu_Clone` | Full game data. UI `.inc` files can be exported from TRE archives and overridden here. |

---

## Project Structure

```
SWGCommandExtension/
├── dllmain.cpp                  # DLL entry point — sets up all Detour hooks on attach/detach
├── SWGCommandExtension.cpp      # (empty — exported functions placeholder)
│
│   ── Core Infrastructure ──
├── Object.h                     # BaseHookedObject base class, ThisCall/Call templates, Hook system, vtable utilities
├── soewrappers.h                # SOE memory allocators, soe::string, soe::unicode, soe::vector, Hook/HookStorage templates
├── soewrappers.cpp              # soe::string and soe::unicode implementations
├── stlwrappers.h / .cpp         # STL wrapper utilities
├── utility.h / .cpp             # General helpers
├── Misc.h                       # DuplicateString allocator wrapper
│
│   ── Game Object Wrappers ──
├── Game.h / .cpp                # Static accessors: getPlayer(), getPlayerCreature(), getPlayerObject(), debugPrintUi()
├── CreatureObject.h             # getAttribute(), getState(), getLookAtTarget(), posture, equipment, mount
├── PlayerObject.h               # speaksLanguage(), getFood/Drink (confirmed offsets)
├── ClientObject.h               # Base client object (inherits Object)
├── IntangibleObject.h           # Inherits ClientObject
├── TangibleObject.h             # Inherits ClientObject
├── NetworkId.h                  # SOE NetworkId wrapper
├── CachedNetworkId.h            # Cached network ID with object pointer
├── AutoDeltaVariable.h          # Server-synced variable template (current value at internal offset 0xC)
│
│   ── UI System Wrappers ──
├── UIBaseObject.h               # GetObjectFromPath(), GetRoot(), IsA(), Attach()
├── UIWidget.h                   # SetFocus(), SetVisible(), SetEnabled(), AddCallback()
├── UIPage.h                     # DuplicateInto(), MoveKeyboardFocus()
├── UIText.h                     # SetLocalText(), AppendLocalText()
├── UITextbox.h                  # Text input widget
├── UIMessage.h                  # UI message types
├── UIEventCallback.h            # Callback interface
├── UIManager.h                  # gUIManager() singleton, GetRootPage()
│
│   ── Command System ──
├── CuiChatParser.h / .cpp       # Hooks the client's command parser — intercepts slash commands
├── CommandParser.h / .cpp        # Base SOE command parser (ctor, performParsing vtable)
├── EmuCommandParser.h / .cpp     # All /emu subcommands (graphics overrides, diagnostics, food/drink, hover height)
├── FoodDrinkMonitor.h / .cpp     # Memory scanner tools + net status UI updater
├── CustomizationData.h           # VehicleHoverDynamics wrapper (direct memory access to hover parameters)
│
│   ── Mediator / UI System ──
├── CuiMediator.h                # UI mediator wrapper (get/create, fetch/release, isActive)
├── CuiMediatorFactory.h         # Factory: Constructor<T> template, addConstructor, get/activate/toggle
├── SwgCuiMediatorFactorySetup.h/.cpp  # Hooks the mediator install to register custom mediators
├── SwgCuiConsole.h / .cpp       # Custom in-game console mediator
├── SwgCuiLoginScreen.h / .cpp   # Hooks login screen button press
│
│   ── Scene & Terrain ──
├── GroundScene.h / .cpp         # Scene access, camera, view modes, message parsing hook
├── FreeChaseCamera.h            # Camera control (view distance, zoom, position)
├── TerrainObject.h / .cpp       # Terrain LOD threshold hooks
├── ClientProceduralTerrainAppearance.h/.cpp  # Flora distance overrides
├── CellProperty.h / .cpp        # Interior cell properties
├── CollisionWorld.h             # Collision system access
│
│   ── Other ──
├── LuaBridge/                   # Lua bridge headers
├── LuaEngine.h / .cpp           # Lua scripting engine integration
├── Graphics.h                   # Fill mode (wireframe toggle)
├── InputMap.h                   # Input message queue access
├── MessageQueue.h               # getMessage() for input processing
├── Controller.h                 # Object controller (appendMessage)
├── ObjectAttributeManager.h     # Debug info formatter
├── GameLanguageManager.h        # Language skill mod lookup
```

---

## How It Works

### 1. Injection & Hooking (dllmain.cpp)

On `DLL_PROCESS_ATTACH`, the DLL uses Microsoft Detours to intercept client functions:

```
ATTACH_HOOK(SwgCuiLoginScreen::onButtonPressed)  — Login screen events
ATTACH_HOOK(CuiChatParser::parse)                — Slash command interception
ATTACH_HOOK(TerrainObject::set*Threshold)         — LOD clamping removal
ATTACH_HOOK(GroundScene::parseMessages)           — Input message injection
ATTACH_HOOK(SwgCuiMediatorFactorySetup::install)  — Custom UI mediator registration
```

It also NOP-patches 7 bytes at `0xC8D258` to remove a terrain detail clamp.

### 2. Calling Client Functions

All client interaction goes through template-based dispatchers in `Object.h`:

- **`ThisCall<ADDRESS, Return, This, Args...>::run(this, args)`** — `__thiscall` convention (ECX = this)
- **`Call<ADDRESS, Return, Args...>::run(args)`** — `__cdecl` static/free functions
- **`runVirtual<VTABLE_OFFSET, Return, Args...>(args)`** — Virtual method via vtable lookup

Wrapper classes (CreatureObject, PlayerObject, UIWidget, etc.) expose methods like:
```cpp
int getAttribute(Attribute i) const {
    return getAttributesArray()[i];  // reads memory at known offset
}

static CreatureObject* getPlayerCreature() {
    return Call<0x425200, CreatureObject*>::run();  // calls client function at fixed address
}
```

### 3. Memory Layout Access

`BaseHookedObject::getMemoryReference<T>(offset)` casts `(this + offset)` to `T&`, allowing direct access to any field in a client object at a known byte offset. Example:

```cpp
// CreatureObject: mood byte is at offset 0x8D0 from the object base
uint8_t getMood() const {
    return getMemoryReference<uint8_t>(0x8D0);
}
```

### 4. Command Flow

1. Player types `/emu <command>` in chat
2. `CuiChatParser::parse` hook intercepts it (hooked at `0x9FF6F0`)
3. If text starts with `/emu`, routes to `EmuCommandParser::parse()`
4. EmuCommandParser matches the subcommand and executes
5. Output is appended to `resultUnicode` which displays in the chat window

### 5. SOE Type System

The project reimplements SOE's custom STL-like types because the client uses its own allocators:

- **`soe::string`** / **`soe::unicode`** — String types using SOE allocators at `0x012EA770`/`0x00AC15C0`
- **`soe::vector<T>`** — Vector with SOE allocator
- **`AutoDeltaVariable<T>`** — Server-synced variable (internal layout: value at offset `0xC`, last value at `0x10`)

---

## Key Memory Addresses (SwgClient_r.exe)

| Address | Function/Data |
|---------|--------------|
| `0x425140` | `Game::getPlayer()` |
| `0x425180` | `Game::getPlayerObject()` → returns `PlayerObject*` |
| `0x425200` | `Game::getPlayerCreature()` → returns `CreatureObject*` |
| `0x424810` | `Game::debugPrintUi(const char*)` — echoes text to chat |
| `0x0190885C` | `Game::ms_scene` static pointer |
| `0x9FF6F0` | `CuiChatParser::parse` — slash command entry point |
| `0x0087FF60` | `UIManager::gUIManager()` singleton accessor |
| `0x010F5020` | `UIBaseObject::GetObjectFromPath(name, type)` |
| `0x010F4FA0` | `UIBaseObject::GetObjectFromPath(name)` |
| `0x65FEA0` | `PlayerObject::speaksLanguage(int)` |
| `0x431970` | `CreatureObject::getEquippedObject(const char*)` |
| `0x0051A900` | `GroundScene::parseMessages` |
| `0x0110F580` | `UIText::SetLocalText(const soe::unicode&)` |
| `0x0110FB40` | `UIText::AppendLocalText(const soe::unicode&)` |
| `0x01112660` | `UIText::SetPreLocalized(bool)` |
| `0x00D58490` | `SwgCuiNetStatus::SwgCuiNetStatus()` constructor |
| `0x00D586C0` | `SwgCuiNetStatus::update(float)` |
| `0x015F1990` | `SwgCuiNetStatus` vtable |
| `0x00A68210` | `CuiMediator::getCodeDataObject()` |
| `0x00BBA510` | `SwgCuiMediatorFactorySetup::install` |
| `0x009BFF00` | `CuiMediator::ctor` |
| `0x008840D0` | `CuiMediatorFactory::activate` |
| `0x00883FB0` | `CuiMediatorFactory::get` |
| `0x01868A84` | Object RTTI pointer |
| `0x01869148` | CreatureObject RTTI pointer |
| `0x01868EF8` | Scene RTTI pointer |
| `0x01868F0C` | GroundScene RTTI pointer |
| `0x0189AEF0` | UIPage RTTI pointer |
| `0x0189A8A8` | UIBaseObject RTTI pointer |
| `0x01918970` | `soe::unicode::empty_string` static |
| `0x012EA770` | SOE string allocator (sizes ≤ 0x80) |
| `0x00AC15C0` | SOE general allocator (sizes > 0x80) |
| | |
| **Vehicle/Hover System** | |
| `0x011ab740` | `VehicleHoverDynamics` constructor |
| `0x011ac580` | `VehicleHoverDynamics::readParamsFromCustomizationData()` |
| `0x00b39870` | `CustomizationDataProperty::getClassPropertyId()` (static) |
| `0x00b23ee0` | `Object::getProperty(const PropertyId&)` |
| `0x00b399a0` | `CustomizationDataProperty::fetchCustomizationData()` |
| `0x00b32850` | `CustomizationData::findConstVariable(const std::string&)` |
| `0x00b33510` | `CustomizationData::registerModificationListener()` |

---

## Adding New Features — Pattern Guide

### Adding a new `/emu` command

1. Edit `EmuCommandParser.cpp`
2. Add an `else if (command == L"yourcommand")` block before the `help` case
3. Read game state via `Game::getPlayerCreature()`, `Game::getPlayerObject()`, etc.
4. Write output to `resultUnicode`
5. Update `showHelp()` with usage text

### Adding a new client function wrapper

1. Find the function's address in `SwgClient_r.exe` via Ghidra/IDA
2. Add a method to the appropriate wrapper class header:
   ```cpp
   ReturnType myMethod(ArgType arg) const {
       return runMethod<0xADDRESS, ReturnType>(arg);
   }
   ```

### Reading a client object field by memory offset

1. Find the byte offset of the field in the object (via debugger or source analysis)
2. Use `getMemoryReference`:
   ```cpp
   int myField() const {
       return getMemoryReference<int>(0x1A4);  // offset from object base
   }
   ```
3. For `AutoDeltaVariable<T>` fields (server-synced), the actual value is at `+0xC` inside the variable

### Hooking a client function

1. In the wrapper header, declare the hook:
   ```cpp
   static void myFunction(args...);
   DEFINE_HOOK(0xADDRESS, myFunction, originalMyFunction);
   ```
2. Implement the hook — call `originalMyFunction::run(args...)` to invoke the original
3. In `dllmain.cpp`, add `ATTACH_HOOK(ClassName::myFunction)` and `DETACH_HOOK(...)` 

### Navigating the UI widget tree

```cpp
UIPage* root = UIManager::gUIManager()->GetRootPage();
UIBaseObject* widget = root->GetObjectFromPath("path.to.widget");
// Cast using dynamicCast if needed, or just reinterpret for known types
```

Widget paths use `.` separators mirroring the `Name` attributes in `.inc` files.

### Writing to workspace mediator widgets (duplicateOnly=true)

Workspace mediators (like `WS_NetStatus`, `WS_ChatWindow`, `WS_Toolbar`) clone the template page. **Do not look up widgets from the root page** — you'll find the template (or nothing), and writes to the template are invisible on screen.

```cpp
// 1. Get the active mediator by workspace name
CuiMediator* mediator = CuiMediatorFactory::get("WS_NetStatus", false);
if (!mediator) return;  // panel not open

// 2. Access the CLONED page (UIPage* stored at mediator offset +0x14)
UIPage* page = mediator->getMemoryReference<UIPage*>(0x14);
if (!page) return;

// 3. Find widgets relative to the cloned page
UIBaseObject* widget = page->GetObjectFromPath("comp.food.text");

// 4. For dynamic text, disable localization first (once)
UIText* text = reinterpret_cast<UIText*>(widget);
text->SetPreLocalized(true);  // without this, raw text is treated as @string_id

// 5. Set the text
text->SetLocalText(soe::unicode("81/100"));
```

How to identify workspace mediators: In the forked client source, look for `CuiMediatorFactory::addConstructor` calls where the `Constructor` is created with `duplicate=true` (second parameter). The first parameter to `addConstructor` is the workspace name (e.g., `"WS_NetStatus"`).

---

## Food/Drink Feature

### Confirmed PlayerObject Field Offsets

| Field | ADV Base Offset | Current Value Offset | Status |
|-------|----------------|---------------------|--------|
| m_food | `0x0570` | `0x057C` | **Confirmed** |
| m_maxFood | `0x0584` (predicted) | `0x0590` | TODO |
| m_drink | `0x0598` | `0x05A4` | **Confirmed** |
| m_maxDrink | `0x05AC` (predicted) | `0x05B8` | TODO |

Offsets discovered via runtime `memscan` (snapshot → eat/drink → diff). See `food-drink-research.md` for full details.

### Net Status Integration

Food/drink values are displayed on the network monitor panel (alongside ping, FPS, etc.):

- **UI layout**: Modified `ui_pda_net_status.inc` adds food/drink rows below FPS
  - Source of truth: `SWGRootFiles\ui\ui_pda_net_status.inc` (in project repo)
  - Deploy to: `D:\Galaxies\SWGEmu_Clone\swgroot\ui\ui_pda_net_status.inc` (manual copy)
- **Update mechanism**: `FoodDrinkMonitor::updateNetStatusUI()` called per-frame from `GroundScene::parseMessages`
  - Only updates text when values change (caches last displayed values)
  - Uses `UIText::SetLocalText()` at `0x0110F580`

#### Workspace Mediator Clone Pattern (Critical)

The net status panel is a **workspace mediator** registered with `duplicateOnly=true`. This means the UI system **clones the template page** when creating the visible panel. The template (at path `pda.netStatus` in the UI tree) is just a blueprint — the on-screen widgets are cloned copies in a separate memory location.

**You cannot write to template widgets and see results on screen.** This was confirmed by:
1. Finding template widgets via `pda.netStatus.comp.food.text` from the root page — they resolve, calls succeed, but nothing renders
2. Looking up `netStatus.comp.food.text` from root — returns nullptr (template isn't at that path)

**Correct approach — access the clone via the mediator**:
```cpp
// 1. Get the active mediator (false = don't create if absent)
CuiMediator* mediator = CuiMediatorFactory::get("WS_NetStatus", false);

// 2. CuiMediator stores its UIPage* at offset +0x14
UIPage* page = mediator->getMemoryReference<UIPage*>(0x14);

// 3. Look up widgets relative to the CLONED page
UIBaseObject* foodObj = page->GetObjectFromPath("comp.food.text");
```

This pattern applies to **all workspace mediators** with `duplicateOnly=true` (e.g., `WS_ChatWindow`, `WS_Toolbar`, `WS_MiniMap`).

#### Widget Setup Requirements

- Call `SetPreLocalized(true)` on each UIText widget before setting dynamic text — without this, the widget treats the string as a localization key (`@ui:string_id`) and displays nothing
- Widget paths from the cloned page use `.` separators matching the `Name` attributes in `.inc` files: `comp.food.text` corresponds to the `comp` composite → `food` page → `text` UIText widget

### Chat Commands

| Command | Description |
|---------|-------------|
| `/emu food` | Show current food fill and percentage |
| `/emu drink` | Show current drink fill and percentage |
| `/emu stomach` | Show both food and drink |
| `/emu memscan snapshot` | Save PlayerObject memory state |
| `/emu memscan diff` | Show offsets changed since snapshot |
| `/emu memscan search <value>` | Find all offsets containing an int32 value |
| `/emu memscan delta <offset>` | Read AutoDeltaVariable at offset |
| `/emu memscan dump <offset> <length>` | Hex dump of raw memory |

### Future Work
- Confirm maxFood/maxDrink offsets (`/emu memscan dump 0x570 0x50`)
- Character sheet visual bar fill (hook SwgCuiCharacterSheet, widget offsets: foodBar=+0xAC, drinkBar=+0xB4)
- PLAY9 delta hook for push-based updates

---

## Vehicle Hover Height Feature

Allows adjusting the hover height of jetpacks and other hover vehicles at runtime via direct memory write to `VehicleHoverDynamics::m_hoverHeight` (offset `+0x68`).

### Object Layout Additions

| Object Offset | Type | Member |
|---------------|------|--------|
| `+0x30` | `Dynamics*` | `m_dynamics` — confirmed via runtime debug on mounted vehicles |

### Traversal: Player → Dynamics

```
Player.getAttachedTo() → Container.getAttachedTo() → Vehicle.getDynamics() → VehicleHoverDynamics
```

Two levels up from the player, not one — an intermediate container/slot object sits between the rider and the vehicle creature.

### Key Insight: std::string ABI Mismatch

The SWG client was built with an older MSVC runtime. Passing a modern MSVC 2022 `std::string` to client functions like `CustomizationData::findConstVariable()` causes ACCESS_VIOLATION due to incompatible memory layout. The workaround is direct memory access to known offsets instead of calling client string-parameter functions.

See [vehicle-hover-research.md](vehicle-hover-research.md) for full technical details, all confirmed offsets, and CustomizationData variable paths.

### Chat Commands

| Command | Description |
|---------|-------------|
| `/emu hover` | Show current hover height |
| `/emu hover <value>` | Set hover height (float, game units) |
| `/emu hover reset` | Restore original height |
| `/emu hover debug` | Dump mount object hierarchy for debugging |
