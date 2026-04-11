# Vehicle Hover System — Research & Technical Reference

## Overview

SWG hover vehicles (jetpacks, speeders, swoops) all share the same physics system: `VehicleHoverDynamics`. There is no special "jetpack" class — all hover vehicles use game object type `GOT_vehicle_hover` and differ only in their customization parameters and visual appearance.

Hover height is **entirely client-side**. The server has no involvement in controlling vehicle altitude — it only tracks the creature's scale/height field and resets it on dismount.

---

## Object Hierarchy When Mounted

When a player mounts a hover vehicle, the object tree is:

```
Vehicle (CreatureObject)           ← the actual vehicle creature
├── +0x30 → VehicleHoverDynamics   ← physics handler (m_hoverHeight at +0x68)
├── Container/Slot (Object)        ← intermediate container (null template)
│   └── Player (CreatureObject)    ← the rider (isRidingMount() = true)
└── Child objects (saddle visuals, effects, etc.)
```

### Traversal from Player to Dynamics

```
Player → getAttachedTo() → Container → getAttachedTo() → Vehicle → getDynamics()
```

In code:
```cpp
CreatureObject* player = Game::getPlayerCreature();
Object* container = player->getAttachedTo();     // intermediate slot
Object* vehicle = container->getAttachedTo();     // the vehicle creature
VehicleHoverDynamics* dynamics = static_cast<VehicleHoverDynamics*>(vehicle->getDynamics());
```

This was confirmed via in-game `/emu hh debug` testing:
- `player->getAttachedTo()` returns a container with null template, 1 child (the player), no controller
- The container's `getAttachedTo()` returns the actual vehicle with dynamics at `+0x30`

---

## VehicleHoverDynamics Layout

Confirmed from Ghidra analysis of the constructor at `0x011ab740`:

| Offset | Type | Default Value | Member |
|--------|------|---------------|--------|
| `+0x08`–`+0x1c` | float×6 | 0.0f | Speed/turn rate parameters |
| `+0x28` | float | 2.0f | `m_dampFactorRoll` |
| `+0x2c` | float | 3.0f | `m_dampFactorPitch` |
| `+0x30` | float | 4.0f | `m_dampFactorGlide` |
| `+0x34` | float | 2.5f | `m_glideFactorMoving` |
| `+0x38`–`+0x64` | various | 0/identity | Orientation, position tracking, base transform |
| **`+0x68`** | **float** | **(from param)** | **`m_hoverHeight` — the hover offset above ground** |
| `+0x6c` | float | 0.1f | `m_hoverHeightStopped` |
| `+0x70`–`+0x9c` | Transform | identity matrix | `m_baseTransform_o2p` |
| `+0xa0` | float | 0.5f | `m_autoLevellingForce` |
| `+0xa8`–`+0xac` | int | 0 | Change tracking |
| **`+0xb0`** | **ptr** | — | **`m_customizationData` → CustomizationData pointer** |
| `+0xb4` | int | 0 | `m_hasAlteredCount` |
| `+0xb8` | bool | false | `m_customizationDataChanged` |
| `+0xbc` | ptr | 0 | Additional data |

### Physics Frame Update (alter method)

Each frame, `VehicleHoverDynamics::alter()`:
1. Checks if customization data has changed → reloads parameters
2. Raycasts ahead via `HoverPlaneHelper::findMinimumHoverHeight()` to find terrain height
3. Sets `m_targetY_w` from terrain sampling
4. Adds `m_hoverHeight` as vertical offset: `m_targetY_w += m_hoverHeight`
5. Interpolates Y position with damping: `y += (target - y) * timeFactorGlide`
6. Moves vehicle object to final position

---

## CustomizationData System

Vehicle physics parameters are normally loaded from the mount creature's `CustomizationData` via integer-valued customization variables. The constructor at `0x011ab740` fetches these from the mount (parent of the saddle).

### Customization Variable Paths

| Variable Path | Scale Factor | Controls |
|---------------|-------------|----------|
| `/private/index_hover_height` | × 0.1 | Height above ground |
| `/private/index_damp_height` | × 0.1 | Height change damping |
| `/private/index_glide` | × 0.1 | Glide/lift factor while moving |
| `/private/index_damp_roll` | × 0.1 | Roll damping |
| `/private/index_damp_pitch` | × 0.1 | Pitch damping |
| `/private/index_speed_min` | × 0.1 | Minimum speed |
| `/private/index_speed_max` | × 0.1 | Maximum speed |
| `/private/index_turn_rate_min` | × π/180 | Minimum turn rate |
| `/private/index_turn_rate_max` | × π/180 | Maximum turn rate |
| `/private/index_accel_min` | × 0.1 | Minimum acceleration |
| `/private/index_accel_max` | × 0.1 | Maximum acceleration |
| `/private/index_decel` | × 0.1 | Deceleration |
| `/private/index_slope_mod` | × 0.1 | Slope modifier |
| `/private/index_banking` | × π/180 | Banking angle (roll on turn) |
| `/private/index_auto_level` | × 0.01 | Auto-levelling force |
| `/private/index_strafe` | × 1.0 | Strafe capability |

### readParamsFromCustomizationData

Located at `0x011ac580`. Iterates over 14 CdLookupData entries, each containing:
- A pointer to the customization variable path string
- A float conversion factor
- A pointer to the output member variable

For each entry, calls:
1. `CustomizationData::findConstVariable(path)` → returns `CustomizationVariable*`
2. `__RTDynamicCast` to `RangedIntCustomizationVariable*`
3. `getValue()` via vtable offset `0x1c`
4. Multiplies by conversion factor
5. Stores result in the member variable

---

## Key Addresses (Ghidra-Verified)

### Vehicle/Hover System

| Address | Function | Convention |
|---------|----------|------------|
| `0x011ab740` | `VehicleHoverDynamics::VehicleHoverDynamics()` constructor | __thiscall |
| `0x011ac580` | `VehicleHoverDynamics::readParamsFromCustomizationData()` | __thiscall |
| `0x011ac520` | `readParamsFromCustomizationData()` caller (instance) | __thiscall |
| `0x011aca20` | Second readParams variant (instance with args) | __thiscall |
| `0x0070eec9` | Caller of VehicleHoverDynamics constructor (SaddleManager context) | — |
| `0x011aa6c0` | CustomizationData modification callback | — |

### CustomizationData System

| Address | Function | Convention |
|---------|----------|------------|
| `0x00b39870` | `CustomizationDataProperty::getClassPropertyId()` | static (__cdecl) |
| `0x00b23ee0` | `Object::getProperty(const PropertyId&)` | __thiscall |
| `0x00b399a0` | `CustomizationDataProperty::fetchCustomizationData()` | __thiscall |
| `0x00b32850` | `CustomizationData::findConstVariable(const std::string&)` | __thiscall |
| `0x00b33510` | `CustomizationData::registerModificationListener()` | __thiscall |

### RangedIntCustomizationVariable Vtable

| Vtable Offset | Method |
|---------------|--------|
| `0x1c` | `getValue()` → returns int |
| `0x20` | `setValue(int)` → returns bool |

### RTTI Pointers

| Address | Type |
|---------|------|
| `0x0186afe4` | `VehicleHoverDynamics` RTTI Type Descriptor |
| `0x0188bd64` | `VehicleHoverDynamicsClient` RTTI Type Descriptor |
| `0x0187a08c` | `RangedIntCustomizationVariable` RTTI Type Descriptor |
| `0x0187a068` | `CustomizationVariable` RTTI Type Descriptor |

---

## Object Memory Layout (Additions)

| Object Offset | Type | Member |
|---------------|------|--------|
| `+0x2C` | `Controller*` | `m_controller` (confirmed) |
| `+0x30` | `Dynamics*` | `m_dynamics` (confirmed via in-game debug — vehicle has non-null dynamics here) |
| `+0x34` | `Object*` | `m_attachedToObject` / parent (confirmed) |
| `+0x38` | `AttachedObjects*` | `m_attachedObjects` (confirmed) |

---

## Implementation Notes

### Approach A: Direct Memory Write (Implemented)

The current implementation writes directly to `m_hoverHeight` at offset `0x68` within the `VehicleHoverDynamics` object. This is simple and reliable.

**Limitation**: The value is overwritten by the physics system if customization data changes. In practice this doesn't happen during normal gameplay — the customization data is set once at mount time and doesn't change.

### Approach B: CustomizationData Variable (Attempted, Failed)

The original plan was to modify `/private/index_hover_height` via `CustomizationData::findConstVariable()`. This crashed because:
- `findConstVariable()` takes a `const std::string&` parameter
- The SWG client was built with an older MSVC version (likely MSVC 6/7)
- The `std::string` ABI (memory layout) differs between old MSVC and our MSVC 2022 build
- Passing a modern `std::string` to the client's function corrupts memory → ACCESS_VIOLATION at `0x00b2535f`

**To use Approach B in the future**, you would need to:
1. Reverse-engineer the old MSVC `std::string` layout (likely `{ allocator, ptr, size, capacity }` or similar)
2. Create a compatibility wrapper that matches the client's `std::string` ABI
3. Alternatively, find the non-const `findVariable()` address and check if it has a different signature

### Client-Side Only Limitation

Hover height changes are only visible to the local player. Other players see the vehicle at whatever position the server reports. The server never sends or receives hover height data — it's computed entirely in `VehicleHoverDynamics::alter()` on each client independently.

---

## Server Side (Core3) — Reference

The server has minimal involvement with hover height:

- `DismountCommand.h` resets player height via `CreatureObjectDeltaMessage3` (field `0x0E`) on dismount
- `MountCommand.h` handles speed/acceleration mods but does NOT set hover height
- `VehicleObject.idl` defaults to `SceneObjectType.HOVERVEHICLE` with no height field
- Vehicle template (`VehicleObjectTemplate.h`) has decayRate/decayCycle but no hoverHeight

---

## Future Possibilities

- **Expose other physics parameters**: glide, damping, banking, turn rate, speed could all be adjusted with the same direct memory write pattern at their known offsets
- **Per-vehicle profiles**: Store preferred heights per vehicle template name
- **Lua bindings**: Expose hover height get/set to the Lua scripting engine
- **ABI-compatible std::string**: Reverse-engineer the old MSVC string layout to unlock full CustomizationData access for any variable
