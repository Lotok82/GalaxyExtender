# Reverse Engineering Guide — Reading Assembly to Identify Functions and Data

## Context

When working with a compiled binary (like `SwgClient_r.exe`) that has no source code or debug symbols, we use a disassembler (Ghidra) to read the machine instructions. This guide explains the techniques used to identify what functions do and what data they operate on, using real examples from the SWG client.

---

## Key Concepts

### Registers
Registers are small, fast storage slots inside the CPU. Think of them as local variables the processor uses while executing instructions. The important ones for 32-bit x86:

| Register | Common Use |
|----------|-----------|
| **ECX** | In `__thiscall` convention (C++ method calls), this holds the `this` pointer — the object the method is being called on |
| **ESI** | Often used to hold a "working" pointer across a function — frequently `this` is copied here early on |
| **EAX** | Return values, general purpose |
| **EDI** | General purpose, often used as a secondary pointer |
| **EBP** | Base pointer for the current function's stack frame (local variables) |
| **ESP** | Stack pointer (top of the stack) |

### Common Instructions
| Instruction | What It Does | Plain English |
|------------|-------------|---------------|
| `MOV EAX, [ESI + 0x84]` | Load the value at memory address (ESI + 0x84) into EAX | "Read the data stored 0x84 bytes into the object that ESI points to" |
| `LEA EDI, [ESI + 0x84]` | Calculate the address (ESI + 0x84) and store it in EDI | "Get the address of the field at offset 0x84 — don't read it, just point to it" |
| `PUSH EAX` | Push EAX onto the stack | "Pass this value as an argument to the next function call" |
| `CALL FUN_00xxxxxx` | Call a function | "Jump to this function and execute it" |
| `CMP EAX, EDX` / `JZ` | Compare two values, jump if zero (equal) | "If these are equal, skip ahead" |
| `XOR EBX, EBX` | XOR a register with itself | "Set this to zero" (a common shorthand) |

### Calling Conventions
When C++ code calls a method like `widget->SetLocalText(myString)`, the compiler translates it to:
1. Put the `widget` pointer in ECX (because it's a `__thiscall` — a method on an object)
2. Push `myString` onto the stack
3. Execute `CALL <address of SetLocalText>`

So when you see this pattern in assembly:
```
MOV ECX, <something>    ; "this" pointer — the object
PUSH <argument>         ; the argument(s)
CALL <address>          ; the function
```
You're looking at a method call: `something->function(argument)`.

---

## Worked Example: Finding `SetLocalText`

### The Goal
We know the SWG client has a network status panel showing ping, FPS, etc. We want to find the function that **sets text on a UI widget** (`SetLocalText`) so we can call it ourselves to display food/drink values.

### Step 1: Find a Starting Point

We search for the string `"SwgCuiNetStatus"` in Ghidra. Strings are easy to find and often lead directly to constructors. We find it referenced by one function: `FUN_00D58490`.

### Step 2: Read the Constructor

The constructor sets up the object. Here's the key section (simplified):

```
LEA EDI, [ESI + 0x84]          ; Point EDI to offset 0x84 in this object
PUSH s_textPing_018c36b4       ; Push the string "textPing"
PUSH EDI                        ; Push the destination address (this+0x84)
PUSH 0x23                       ; Push the widget type (0x23 = UIText)
PUSH ECX                        ; Push the UI page to search in
CALL FUN_00a68210               ; Call getCodeDataObject()
```

**What this tells us:**
- The function `getCodeDataObject` looks up a child widget by name
- It's looking for a widget named `"textPing"` of type UIText (`0x23`)
- The result (a pointer to the widget) is stored at `this + 0x84`

The constructor repeats this pattern for each widget:
```
this + 0x84  ←  widget named "textPing"         (the ping text)
this + 0x88  ←  widget named "textPacketLoss"    (the packet loss text)
this + 0x8C  ←  widget named "textBandwidth"     (the bandwidth text)
this + 0x90  ←  widget named "textFps"           (the FPS text)
```

**This is the Rosetta Stone** — the constructor gives us a mapping between memory offsets and human-readable names.

### Step 3: Find the Update Method

From the constructor, we find the vtable (a table of function pointers that defines what methods the class has). One of the entries points to `FUN_00D586C0`. We examine it and see:

```
; --- Format the ping value as a string ---
PUSH EDI                        ; push the ping value
PUSH s_format                   ; push format string "%3d (host id %d)"
PUSH 0x100                      ; buffer size
PUSH DAT_01962e80               ; output buffer
CALL FUN_00aa5980               ; this is snprintf()

; --- Convert narrow string to wide string ---
PUSH DAT_01962e80               ; the formatted buffer
PUSH ECX                        ; destination
CALL FUN_004248a0               ; narrow-to-wide conversion

; --- Call SetLocalText on the ping widget ---
MOV ECX, [ESI + 0x84]          ; ECX = this->m_textPing (the widget pointer)
PUSH EAX                        ; EAX = the formatted wide string
CALL FUN_0110f580               ; This is SetLocalText!
```

### Step 4: Connect the Dots

The reasoning chain:
1. **Constructor** stores a widget named `"textPing"` at offset `+0x84`
2. **Update method** reads from offset `+0x84`, getting that same widget pointer
3. It formats a number into a string, then calls `FUN_0110f580` on the widget
4. Since this function *sets the display text* of the ping widget, it must be `SetLocalText`

We see the same function called for packet loss (`+0x88`) and FPS (`+0x90`) with different formatted values, confirming it's a general-purpose text-setting function.

### Result
`SetLocalText` = address `0x0110F580`

---

## General Techniques

### 1. String References Are Your Best Friend
Compiled code strips function names, but it keeps string literals. Searching for known strings (class names, widget names, error messages) leads you to the functions that use them.

### 2. Constructors Label Everything
Constructors store named data at known offsets. Once you've read a constructor, you know *what lives where* in that object's memory. Every other method that reads those offsets is now understandable.

### 3. The Constructor → Vtable → Methods Pipeline
1. Find a constructor (via string reference)
2. The constructor sets the vtable: `MOV [this], <vtable_address>`
3. Navigate to the vtable — it's a list of function pointers
4. Each pointer is a method on that class
5. Methods in the same address range (e.g., `0x00D5xxxx`) are overrides specific to this class

### 4. Pattern Matching
Once you identify a pattern (e.g., "MOV ECX, [widget] / PUSH string / CALL function" = `widget->SetText(string)`), you can recognise it everywhere in the binary.

### 5. Cross-Referencing (XREFs)
Ghidra tracks every reference to every address. If you're looking at a function, you can see every place that calls it (callers), and if you're at a data address, you can see every instruction that reads/writes it. This lets you trace data flow in both directions.

---

## Tips for Beginners in Ghidra

- **Right-click → References → Find references to** — shows everywhere something is used
- **Double-click an address** — navigates to it
- **Rename functions** — when you identify what something does, rename it (press `L`). This makes the rest of the analysis much easier.
- **Add comments** — press `;` to add notes to lines you've figured out
- **Save often** — `Ctrl+S` persists all your analysis work
- **Look for familiar constants** — known values (100 for max food, widget type IDs, format strings like `"%d"`) help identify what code is doing
