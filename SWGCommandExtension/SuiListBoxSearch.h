#pragma once

#include "soewrappers.h"

class UIPage;

/*
 * Find/jump search for server-created SUI list box windows ("Script.listBox"
 * clones — the guild members window, sponsored list, etc.).
 *
 * /emu find <text> selects and scrolls to the next row containing <text>
 * (case-insensitive, cycles from the current selection). It deliberately does
 * NOT reorder or hide rows: the server maps the harvested "SelectedRow" index
 * into its own menu-item vector when the window closes with Ok, so any
 * client-side reordering/filtering would make Ok act on the wrong entry
 * (e.g. kick the wrong guild member). Selecting a real row keeps the index
 * truthful, and changing the selection sends no network traffic — Core3's
 * list box only subscribes to the Ok/Cancel close events.
 *
 * See Documentation/guild-list-search-research.md for the full analysis.
 */
class SuiListBoxSearch {
public:
	// Selects and scrolls to the next row containing `query`. Appends
	// user-facing feedback to `result`.
	static void find(const soe::unicode& query, soe::unicode& result);

	// Diagnostics for `/emu find` with no arguments: reports whether a list
	// box window is open, its title, row count and current selection.
	static void status(soe::unicode& result);

private:
	// Newest open Script.listBox clone under the game workspace, or nullptr.
	static UIPage* findListBoxWindow();
};
