# Guild Member List — Sort Order & Search Research

Motivation: the guild terminal's GUILD MEMBERS window lists names in a
seemingly random order, and long member lists are painful to work with.
This document records why the order is what it is, what can and cannot be
changed from the client side, and the design behind `/emu find`.

Sources: Core3 server source (`d:\Galaxies\Core3`), forked client source
(`d:\Galaxies\SWGClient\client-tools`), reference exe
(`D:\Galaxies\SWGEmu_Clone\SWGEmu.exe`).

---

## 1. Why the list is "sorted by character age"

The window is a generic server-driven SUI list box. The server builds it in
`GuildManagerImplementation::sendGuildMemberListTo()`
(`MMOCoreORB/src/server/zone/managers/guild/GuildManagerImplementation.cpp:1061-1097`),
iterating `GuildMemberList` — a `VectorMap<uint64, GuildMemberInfo>` keyed by
**player object ID**. A VectorMap iterates in ascending key order, and object
IDs come from a monotonically increasing counter assigned at character
creation, so the list arrives oldest-character-first. The client renders rows
exactly in arrival order (`UIList::Render` walks the data source positionally;
`UIList`/`UIDataSource` have **no** sort or filter capability — only
`UITable`/`UITableModel` can sort, and the SUI list box uses `UIList`).

The sponsored-members window (`GuildManagerImplementation.cpp:1537-1568`) has
the same artifact.

A server-side alphabetical sort would be trivial (the display name is resolved
before `addMenuItem`, and the Ok callback reads the object ID stored on the
menu item, not the map order) — but we have no access to change Core3.

## 2. The index-desync trap (why we never reorder/filter client-side)

Core3's `SuiListBoxImplementation` subscribes exactly two events, both on the
page itself (`SuiBoxImplementation::generateHeader`): `SET_onClosedOk` (9) and
`SET_onClosedCancel` (10), harvesting `List.lstList → SelectedRow` and
`bg.caption.lblTitle → Text` when the window closes. `SelectedRow` is a **row
index**; the server maps it into its own `menuItems` vector
(`GuildMemberListSuiCallback` reads `getMenuObjectID(index)`).

Consequence: if the DLL reordered or hid rows, the index the client reports
would no longer correspond to the server's vector — Ok would act on the
**wrong member** (kick the wrong person). Any client-side sort/filter would
need to translate the selection back before the harvest. `/emu find` therefore
only ever **selects a real row**: the index stays truthful by construction.

Two more safety facts, verified in client source:

- Programmatic selection sends **nothing**: `UIList::SelectRow` fires
  `OnGenericSelectionChanged` only to callbacks registered *on the list
  widget*; the SUI listener is registered on the page, Core3 registers no
  widget for its events, and the page even early-returns non-close events when
  inactive (`CuiDataDrivenPage::onEvent`). Traffic happens only on Ok/Cancel.
- Never synthesize a double-click on the list: `UIList::ProcessMessage` fakes
  an Enter keypress, which triggers the default button → `onClosedOk` packet
  and the window closes.

## 3. Finding the window at runtime

`CuiDataDrivenPageManager::createPage` clones the `Script.listBox` template
(`DuplicateObject` copies the name — the clone is called `listBox`) and
`CuiWorkspace::addMediator` reparents it under the game workspace page:
`/GroundHUD` on the ground, `/HudSpace` in space. `InsertChildAfter(page, 0)`
puts the **newest clone at the front** of the child list, and name lookups
return the first match, so `hud->GetObjectFromPath("listBox")` yields the most
recently opened list window. The `Script.listBox` template itself lives under
`/Script` and is never matched by a lookup that starts at the HUD page.

Sibling clones collide by name (no uniquifier), so with several list windows
open only the newest is reachable this way — acceptable for this feature.
Every candidate object is verified by its **vtable pointer** before use
(`UIPage`/`UIList`/`UIDataSource`/`UIData`/`UIText`), which also guards the
`GetObjectFromPath` parent-chain walk-up on misses.

Rows: `List.dataList` children are `UIData` objects Core3 names by menu index
("0", "1", ...). Row text is the `Text` property; `LocalText` exists only when
localization changed the value (plain player names: removed), so read
`LocalText` first, then `Text` — mirroring `UIList::Render`.
(`UIList::GetText` returns the row *name*, and `UIList::GetLocalText` returns
empty-with-`true` for plain rows — both are traps.)

Scrolling: `SelectRow` never scrolls, and `UIList::ScrollToRow` is not
virtual/addressable, so the module computes `y = row * (ScrollExtent.y /
rowCount)`, centers and clamps it, and writes the `ScrollLocation` property
(the property path bypasses `ScrollToPoint`'s clamping — clamp manually).
The `UIScrollbar` thumb recomputes from the list's scroll location each frame,
so it follows automatically.

## 4. Calling the client's property API (the vtable hunt)

`UIBaseObject` declares `RemoveProperty`/`SetProperty`/`GetProperty` twice
each (public `UILowerString` forms, private `const char*` forms declared ~140
lines later). MSVC groups same-named virtual overloads at the first
declaration's slot, so header order is misleading. The hunt scripts
(`tools/find_uivtables.py`, `tools/verify_uivtables.py`) resolve each class's
vtable via RTTI (`.?AVUIList@@` TypeDescriptor → offset-0 COL → vtable) and
identify slots by disassembly:

- slots 8/10/12: the private `const char*` stubs — literally `xor al,al; ret
  4/8/8` (return false), shared by every class
- slot 11 = `SetProperty(const UILowerString&, const UIString&)` — confirmed
  by UIList's `"SelectedItem."` dotted-property prologue (`'.'` scan +
  `_strnicmp`, `GetLastSelectedRow`/`GetDataAtRow` calls)
- slot 13 = `GetProperty(...)` — same prologue + `mProperties` map fallback;
  UIData/UIDataSource share `UIBaseObject::GetProperty` (no override), the
  widget classes override it
- slot 21 = `GetChildCount()` — `UIBaseObject` returns 0, `UIPage`/
  `UIDataSource` count their child list nodes

The DLL calls the **per-class implementation addresses directly** (no slot
arithmetic at runtime) and checks each object's vtable pointer first, so a
mismatched client build degrades to an error message instead of a wild call.
Resolved addresses are in the ARCHITECTURE.md table.

`UILowerString` is reimplemented DLL-side (`UILowerString.h`). **The shipped
client differs from the fork source here**: the fork's `UILowerString` carries
two hashes `{m_hashQuick, m_hashEqu}`, but the live binary's is a **single
case-insensitive CRC-32** at offset 0 — `updateHash` (0x010E51A0) computes
only the CRC and stores it at `[this+0]`, `get()` (0x010E5360) keys the
hash→string map off `[this+0]`, and `operator==` is one dword compare
(`tools/verify_uilowerstring.py`). The first in-game test shipped the fork's
two-field layout, which made every property lookup miss silently (all
`GetProperty` returned false → "No entry matching" for names visibly in the
list, and no window title in the output) while everything that doesn't touch
`UILowerString` — vtable checks, `GetChildCount` — worked. Lesson: the fork
source is a *restoration* and can postdate the shipped binary; verify object
layouts against the exe, not just function addresses. The CRC algorithm
itself matches the fork instruction-for-instruction.

The hash→string map the client keeps for fallback paths is already populated
for every name we use (the client's own static initializers register them),
so generic map lookups (row `Text` on `UIData`) work too. Avoid names the
client never constructs and dotted pseudo-properties.

## 5. What was deliberately left out

- **Client-side sorting/filtering** — needs selection-index translation at
  harvest time (hook `CuiDataDrivenPage::onEvent` or the listener's
  `OnButtonPressed`); high blast radius if wrong (acts on the wrong member).
- **An injected search textbox in the window** — feasible (clone
  `Script.inputBox.txtInput` via virtual `DuplicateObject`/`AddChild`/`Link`,
  poll its `LocalText` per frame; a focused textbox automatically blocks game
  keybinds per `CuiIoWin::processEvent`), but widget positioning/packing can't
  be iterated without the game running. Possible follow-up once `/emu find`
  has proven the property API in-game.
