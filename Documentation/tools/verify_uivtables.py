# Verify the slot identification from find_uivtables.py by direct disassembly.
#
# Hard evidence sought:
#   A. slot 1  = GetTypeName        -> `mov eax, <literal>; ret` returning the
#                                      class name string ("List", "Page", ...)
#   B. slot 11 = SetProperty(UILowerString&, UIString&)
#                UIText's impl (0x01111810 expected) must call the known,
#                production-verified UIText::SetLocalText = 0x0110F580 (it is
#                the handler for the "LocalText" property) and/or SetText.
#   C. slot 13 = GetProperty(UILowerString&, UIString&) const
#                UIBaseObject's impl must handle "Name" (narrowToWide of mName)
#                and fall back to the mProperties map.
#   D. slot 21 = GetChildCount — UIBaseObject: `xor eax,eax; ret` (returns 0);
#                UIDataSource (0x01131ae0 expected): reads the list size.
#   E. slots 8/10/12 (0x010f57a0/b0/c0) — expected to be tiny stubs; determine
#                what they are (suspected non-property trivia, must NOT be the
#                property API).
#
# Prints annotated disassembly of each function so the evidence is reviewable.

import struct
import capstone

EXE = r"D:\Galaxies\SWGEmu_Clone\SWGEmu.exe"
data = open(EXE, "rb").read()

e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
num_sections = struct.unpack_from("<H", data, e_lfanew + 6)[0]
opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
opt_off = e_lfanew + 24
image_base = struct.unpack_from("<I", data, opt_off + 28)[0]
sec_off = opt_off + opt_size
sections = []
for i in range(num_sections):
    off = sec_off + i * 40
    name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
    vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
    sections.append((name, vaddr, vsize, raddr, rsize))

def va_to_foff(va):
    rva = va - image_base
    for name, vaddr, vsize, raddr, rsize in sections:
        if vaddr <= rva < vaddr + vsize:
            return raddr + (rva - vaddr)
    return None

def cstr_at_va(va, maxlen=100):
    foff = va_to_foff(va)
    if foff is None:
        return None
    end = data.find(b"\0", foff, foff + maxlen)
    if end < 0:
        return None
    s = data[foff:end]
    if s and all(32 <= c < 127 for c in s):
        return s.decode()
    return None

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)

def dump(va, n=60, label=""):
    print(f"\n----- {label} @ {va:#010x} -----")
    foff = va_to_foff(va)
    count = 0
    for ins in md.disasm(data[foff:foff + 0x600], va):
        note = ""
        # annotate immediates that point at ascii strings
        for tok in ins.op_str.replace(",", " ").split():
            if tok.startswith("0x"):
                try:
                    tv = int(tok, 16)
                except ValueError:
                    continue
                s = cstr_at_va(tv)
                if s:
                    note = f'   ; "{s}"'
        print(f"  {ins.address:#010x}: {ins.mnemonic:8s} {ins.op_str}{note}")
        count += 1
        if ins.mnemonic == "ret" or ins.mnemonic == "jmp" and count > 1:
            break
        if count >= n:
            print("   ... (truncated)")
            break

VT = {
    "UIBaseObject": 0x015f9cc4, "UIWidget": 0x015fa0cc, "UIPage": 0x015f9da4,
    "UIList": 0x015fb2b4, "UIText": 0x015fa1d4, "UITextbox": 0x015fa6f4,
    "UIData": 0x015faf9c, "UIDataSource": 0x015fae7c,
}

def slot(cls, i):
    return struct.unpack_from("<I", data, va_to_foff(VT[cls]) + i * 4)[0]

# A. GetTypeName probes
for cls in VT:
    dump(slot(cls, 1), 6, f"{cls} slot 1 (GetTypeName?)")

# B. SetProperty probes
dump(slot("UIText", 11), 120, "UIText slot 11 (SetProperty? should call 0x0110F580 SetLocalText)")
dump(slot("UIList", 11), 80, "UIList slot 11 (SetProperty?)")

# C. GetProperty probes
dump(slot("UIBaseObject", 13), 90, "UIBaseObject slot 13 (GetProperty? handles Name + mProperties map)")
dump(slot("UIText", 13), 60, "UIText slot 13 (GetProperty?)")

# D. GetChildCount probes
dump(slot("UIBaseObject", 21), 6, "UIBaseObject slot 21 (GetChildCount? -> 0)")
dump(slot("UIDataSource", 21), 10, "UIDataSource slot 21 (GetChildCount? -> list size)")
dump(slot("UIPage", 21), 12, "UIPage slot 21 (GetChildCount?)")

# E. the tiny shared stubs
for i in (8, 10, 12):
    dump(slot("UIBaseObject", i), 8, f"UIBaseObject slot {i} (tiny shared stub)")
