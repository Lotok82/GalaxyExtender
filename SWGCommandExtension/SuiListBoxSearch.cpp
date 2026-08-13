#include "stdafx.h"
#include "SuiListBoxSearch.h"

#include "UIManager.h"
#include "UIPage.h"
#include "UIList.h"
#include "UIData.h"
#include "UIDataSource.h"
#include "UIText.h"
#include "UILowerString.h"

#include <wchar.h>
#include <wctype.h>

// ============================================================================
// SEH helper — POD-only, MSVC forbids __try in functions with C++ objects.
// ============================================================================

namespace {

bool seh_readU32(const void* base, uint32_t& out) {
	__try {
		out = *reinterpret_cast<const uint32_t*>(base);
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {
		return false;
	}
}

// The direct-address wrapper calls below assume the object really is the
// class whose implementation we call, so gate every one behind its vtable.
bool isInstance(const void* obj, uint32_t vtableAddress) {
	if (!obj)
		return false;

	uint32_t vtable = 0;

	return seh_readU32(obj, vtable) && vtable == vtableAddress;
}

// property names the client's static initializers already registered
const UILowerString& propText()           { static UILowerString p("Text");           return p; }
const UILowerString& propLocalText()      { static UILowerString p("LocalText");      return p; }
const UILowerString& propSelectedRow()    { static UILowerString p("SelectedRow");    return p; }
const UILowerString& propScrollLocation() { static UILowerString p("ScrollLocation"); return p; }
const UILowerString& propScrollExtent()   { static UILowerString p("ScrollExtent");   return p; }
const UILowerString& propSize()           { static UILowerString p("Size");           return p; }

// parse "x,y" (UIUtils::FormatPoint output) — returns false unless both parse
bool parsePoint(const soe::unicode& value, long& x, long& y) {
	const wchar_t* s = value.c_str();

	if (!s || !*s)
		return false;

	wchar_t* end = nullptr;
	x = wcstol(s, &end, 10);

	if (!end || *end != L',')
		return false;

	y = wcstol(end + 1, nullptr, 10);

	return true;
}

bool parseLong(const soe::unicode& value, long& out) {
	const wchar_t* s = value.c_str();

	if (!s || !*s)
		return false;

	wchar_t* end = nullptr;
	out = wcstol(s, &end, 10);

	return end != s;
}

// case-insensitive substring test, no allocations
bool containsNoCase(const wchar_t* haystack, const wchar_t* needle) {
	if (!*needle)
		return true;

	for (; *haystack; ++haystack) {
		const wchar_t* h = haystack;
		const wchar_t* n = needle;

		while (*h && *n && towlower(*h) == towlower(*n)) {
			++h;
			++n;
		}

		if (!*n)
			return true;
	}

	return false;
}

// row UIData resolved by its Core3-assigned index name ("0", "1", ...)
UIData* getRow(UIDataSource* dataSource, long index) {
	char name[16];
	sprintf_s(name, sizeof(name), "%ld", index);

	UIBaseObject* row = dataSource->GetObjectFromPath(name);

	// GetObjectFromPath walks up the parent chain on a miss, so a stray
	// same-named widget elsewhere could come back — the vtable check rejects
	// anything that is not a UIData row.
	if (!isInstance(row, UIData::VTABLE))
		return nullptr;

	return reinterpret_cast<UIData*>(row);
}

// display text of a row: LocalText first, Text as fallback, mirroring
// UIList::Render — plain names only have "Text", localized rows "LocalText"
bool getRowText(UIData* row, soe::unicode& text) {
	if (row->GetProperty(propLocalText(), text) && text.size() > 0)
		return true;

	return row->GetProperty(propText(), text) && text.size() > 0;
}

void appendWindowTitle(UIPage* window, soe::unicode& result) {
	UIBaseObject* titleObj = window->GetObjectFromPath("bg.caption.lblTitle");

	if (!isInstance(titleObj, UIText::VTABLE))
		return;

	soe::unicode title;

	if (reinterpret_cast<UIText*>(titleObj)->GetProperty(propLocalText(), title) && title.size() > 0) {
		result += L" in \\#88ccff";
		result += title;
		result += L"\\#ffffff";
	}
}

} // anonymous namespace

// ============================================================================
// Window lookup
// ============================================================================

UIPage* SuiListBoxSearch::findListBoxWindow() {
	UIManager* manager = UIManager::gUIManager();
	if (!manager)
		return nullptr;

	UIPage* root = manager->GetRootPage();
	if (!root)
		return nullptr;

	// Server-created SUI pages are cloned from the "Script.listBox" template
	// and inserted as children of the game workspace page, keeping the
	// template's leaf name "listBox". New clones are inserted at the front of
	// the child list, so a name lookup returns the newest open window.
	static const char* const workspaces[] = { "GroundHUD", "HudSpace" };

	for (const char* workspace : workspaces) {
		UIBaseObject* hud = root->GetObjectFromPath(workspace);

		if (!hud)
			continue;

		UIBaseObject* page = hud->GetObjectFromPath("listBox");

		if (isInstance(page, UIPage::VTABLE))
			return reinterpret_cast<UIPage*>(page);
	}

	return nullptr;
}

// ============================================================================
// /emu find
// ============================================================================

void SuiListBoxSearch::find(const soe::unicode& query, soe::unicode& result) {
	UIPage* window = findListBoxWindow();

	if (!window) {
		result += L"\\#ff4444No list window found.\\#ffffff Open one first (e.g. the guild member list).";
		return;
	}

	UIBaseObject* listObj = window->GetObjectFromPath("List.lstList");
	UIBaseObject* dataObj = window->GetObjectFromPath("List.dataList");

	if (!isInstance(listObj, UIList::VTABLE) || !isInstance(dataObj, UIDataSource::VTABLE)) {
		result += L"\\#ff4444List widgets did not match the expected UI types.\\#ffffff";
		return;
	}

	UIList* list = reinterpret_cast<UIList*>(listObj);
	UIDataSource* dataSource = reinterpret_cast<UIDataSource*>(dataObj);

	const long count = static_cast<long>(dataSource->GetChildCount());

	if (count <= 0) {
		result += L"The list is empty.";
		return;
	}

	// cycle: start after the current selection so repeating the command
	// steps through multiple matches
	long selected = -1;
	{
		soe::unicode value;

		if (list->GetProperty(propSelectedRow(), value))
			parseLong(value, selected);
	}

	for (long step = 1; step <= count; ++step) {
		const long row = (selected + step) % count;

		UIData* data = getRow(dataSource, row);

		if (!data)
			continue;

		soe::unicode text;

		if (!getRowText(data, text))
			continue;

		if (!containsNoCase(text.c_str(), query.c_str()))
			continue;

		// Select the real row — the index the server will harvest on Ok stays
		// truthful, and no packet is sent by the selection itself.
		wchar_t buf[16];
		swprintf_s(buf, L"%ld", row);
		list->SetProperty(propSelectedRow(), soe::unicode(buf));

		// SelectRow never scrolls, so bring the row into view ourselves.
		// The scrollbar thumb follows the widget's scroll location on its own.
		soe::unicode extentValue, sizeValue;
		long extentX = 0, extentY = 0, sizeX = 0, sizeY = 0;

		if (list->GetProperty(propScrollExtent(), extentValue) && parsePoint(extentValue, extentX, extentY) &&
			list->GetProperty(propSize(), sizeValue) && parsePoint(sizeValue, sizeX, sizeY) &&
			extentY > sizeY && count > 0) {

			const long rowHeight = extentY / count;
			long y = row * rowHeight - (sizeY - rowHeight) / 2; // center the row

			const long maxScroll = extentY - sizeY;
			if (y > maxScroll)
				y = maxScroll;
			if (y < 0)
				y = 0;

			// this property write bypasses ScrollToPoint's clamping, hence
			// the manual clamp above
			swprintf_s(buf, L"0,%ld", y);
			list->SetProperty(propScrollLocation(), soe::unicode(buf));
		}

		char position[64];
		sprintf_s(position, sizeof(position), "\\#ffffff (row %ld of %ld)", row + 1, count);

		result += L"\\#00ff00Selected:\\#ffffff ";
		result += text;
		result += position;
		appendWindowTitle(window, result);
		return;
	}

	result += L"\\#ffcc00No entry matching\\#ffffff '";
	result += query;
	result += L"'";
	appendWindowTitle(window, result);
}

// ============================================================================
// /emu find (no arguments) — diagnostics
// ============================================================================

void SuiListBoxSearch::status(soe::unicode& result) {
	UIPage* window = findListBoxWindow();

	if (!window) {
		result += L"No open list window detected.";
		return;
	}

	result += L"Found a list window";
	appendWindowTitle(window, result);

	UIBaseObject* listObj = window->GetObjectFromPath("List.lstList");
	UIBaseObject* dataObj = window->GetObjectFromPath("List.dataList");

	if (!isInstance(listObj, UIList::VTABLE) || !isInstance(dataObj, UIDataSource::VTABLE)) {
		result += L"\n\\#ff4444List widgets did not match the expected UI types.\\#ffffff";
		return;
	}

	UIList* list = reinterpret_cast<UIList*>(listObj);
	UIDataSource* dataSource = reinterpret_cast<UIDataSource*>(dataObj);

	const long count = static_cast<long>(dataSource->GetChildCount());

	long selected = -1;
	soe::unicode value;

	if (list->GetProperty(propSelectedRow(), value))
		parseLong(value, selected);

	char info[128];
	sprintf_s(info, sizeof(info), "\n%ld rows, selected row: %ld", count, selected);
	result += info;

	if (count > 0 && selected >= 0) {
		UIData* data = getRow(dataSource, selected);
		soe::unicode text;

		if (data && getRowText(data, text)) {
			result += L" (";
			result += text;
			result += L")";
		}
	}
}
