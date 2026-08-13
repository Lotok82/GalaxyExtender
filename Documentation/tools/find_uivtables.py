# Hunt for the UI library vtables (UIBaseObject and descendants) in the SWG
# client binary, and identify the vtable slots of the property/child APIs the
# DLL needs to call generically:
#
#   virtual bool  SetProperty(const UILowerString&, const UIString&)
#   virtual bool  GetProperty(const UILowerString&, UIString&) const
#   virtual unsigned long GetChildCount() const
#
# Why this hunt is needed: UIBaseObject declares RemoveProperty / SetProperty /
# GetProperty twice each (public UILowerString forms at header lines 104/107/117,
# private const char* forms at 245/246/247). MSVC groups same-named virtual
# overloads at the first declaration's position in REVERSE declaration order,
# so the true slot numbers cannot be read off the header. We disassemble the
# real binary instead.
#
# Anchors used to identify slots, no single point of failure:
#   1. RTTI: each class's ".?AVUIList@@"-style TypeDescriptor -> COL (offset 0)
#      -> vtable. Gives us definitive per-class vtables.
#   2. GetTypeName (slot 1) returns a pointer to the class-name literal
#      ("List", "Page", "DataSource", ...) — one-instruction function, verifies
#      the vtable base alignment (slots 0-8 are unambiguous, and the DLL already
#      uses IsA=slot 0 / Attach=slot 4 in production).
#   3. The const char* property overloads construct a UILowerString, whose
#      case-insensitive CRC-32 (UILowerString.cpp calculateHashWithLower)
#      indexes a unique 256-dword table. Any slot whose code (or its first-level
#      callees) references that table is a const char* overload; the
#      UILowerString& overloads never touch it (they compare two dwords).
#   4. Sharing: the const char* overloads are private and never overridden, so
#      their pointers are identical across ALL class vtables; the UILowerString&
#      overloads are overridden per class, so those slots differ between e.g.
#      UIList and UIPage.
#
# Output: per-class vtable VA + slot table with classification, and the
# resolved addresses of the functions the DLL will call directly.

import struct
import capstone

EXE = r"D:\Galaxies\SWGEmu_Clone\SWGEmu.exe"

CLASSES = [
    "UIBaseObject", "UIWidget", "UIPage", "UIList",
    "UIText", "UITextbox", "UIData", "UIDataSource",
]

# first 8 entries of UILowerStringNamespace::crctable (UILowerString.cpp:21)
CRC_HEAD = struct.pack("<8I", 0x00000000, 0x04C11DB7, 0x09823B6E, 0x0D4326D9,
                       0x130476DC, 0x17C56B6B, 0x1A864DB2, 0x1E475005)

data = open(EXE, "rb").read()
print(f"file size: {len(data):#x}")

# --- PE headers ---
e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
assert data[e_lfanew:e_lfanew + 4] == b"PE\0\0"
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
    print(f"  section {name:8s} va {image_base+vaddr:#010x} vsize {vsize:#x} raw {raddr:#x} rsize {rsize:#x}")

def va_to_foff(va):
    rva = va - image_base
    for name, vaddr, vsize, raddr, rsize in sections:
        if vaddr <= rva < vaddr + vsize:
            return raddr + (rva - vaddr)
    return None

def foff_to_va(foff):
    for name, vaddr, vsize, raddr, rsize in sections:
        if raddr <= foff < raddr + rsize:
            return image_base + vaddr + (foff - raddr)
    return None

def section_of(va):
    rva = va - image_base
    for name, vaddr, vsize, raddr, rsize in sections:
        if vaddr <= rva < vaddr + vsize:
            return name
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

def find_all(needle, start=0):
    out = []
    pos = data.find(needle, start)
    while pos >= 0:
        out.append(pos)
        pos = data.find(needle, pos + 1)
    return out

# --- locate the UILowerString crctable ---
crc_hits = find_all(CRC_HEAD)
# the same table also exists for other CRC users; collect all VAs and treat a
# reference to ANY of them as "constructs a UILowerString / computes a CRC" —
# the discriminator only needs "touches a CRC table" vs "compares two dwords".
crc_vas = set()
for h in crc_hits:
    va = foff_to_va(h)
    if va:
        crc_vas.add(va)
print(f"crctable candidates: {[hex(v) for v in sorted(crc_vas)]}")
assert crc_vas, "no crc table found"

# --- RTTI: TypeDescriptor -> COL(offset 0) -> vtable ---
def find_vtable(cls):
    name = f".?AV{cls}@@".encode() + b"\0"
    hits = [h for h in find_all(name)]
    # TypeDescriptor: {vftab_ptr, spare, name[]}; name begins at +8
    tds = []
    for h in hits:
        td_va = foff_to_va(h - 8)
        if td_va:
            tds.append(td_va)
    assert tds, f"{cls}: no TypeDescriptor"
    vtables = []
    for td_va in tds:
        for colref in find_all(struct.pack("<I", td_va)):
            col_foff = colref - 12  # sig, offset, cdOffset, pTD, pCHD
            if col_foff < 0:
                continue
            sig, offset, cdoff = struct.unpack_from("<III", data, col_foff)
            if sig != 0 or offset != 0 or cdoff != 0:
                continue  # want the primary (offset 0) COL
            col_va = foff_to_va(col_foff)
            if col_va is None:
                continue
            # vtable = dword after a pointer to this COL
            for vref in find_all(struct.pack("<I", col_va)):
                vt_va = foff_to_va(vref + 4)
                if vt_va is None:
                    continue
                slot0 = struct.unpack_from("<I", data, vref + 4)[0]
                if section_of(slot0) == ".text":
                    vtables.append(vt_va)
    return vtables

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
md.detail = False

def read_slots(vt_va, n=40):
    foff = va_to_foff(vt_va)
    slots = []
    for i in range(n):
        p = struct.unpack_from("<I", data, foff + i * 4)[0]
        if section_of(p) != ".text":
            break
        slots.append(p)
    return slots

def disasm_props(func_va, max_ins=200, depth=1):
    """Return (touches_crc, ret_string, n_instructions, callees)."""
    foff = va_to_foff(func_va)
    if foff is None:
        return (False, None, 0, [])
    touches_crc = False
    ret_string = None
    callees = []
    n = 0
    last_mov_eax_imm = None
    for ins in md.disasm(data[foff:foff + 0x800], func_va):
        n += 1
        if n > max_ins:
            break
        t = f"{ins.mnemonic} {ins.op_str}"
        for va in crc_vas:
            if f"{va:#x}" in ins.op_str:
                touches_crc = True
        if ins.mnemonic == "mov" and ins.op_str.startswith("eax, 0x"):
            last_mov_eax_imm = int(ins.op_str.split(", ")[1], 16)
        if ins.mnemonic == "call":
            try:
                callees.append(int(ins.op_str, 16))
            except ValueError:
                pass
        if ins.mnemonic == "ret":
            if n <= 3 and last_mov_eax_imm:
                ret_string = cstr_at_va(last_mov_eax_imm)
            break
        if ins.mnemonic in ("jmp",) and not ins.op_str.startswith("0x"):
            break
    if not touches_crc and depth > 0:
        for c in callees[:4]:
            tc, _, _, _ = disasm_props(c, max_ins=120, depth=depth - 1)
            if tc:
                touches_crc = True
                break
    return (touches_crc, ret_string, n, callees)

# --- dump everything ---
class_vts = {}
for cls in CLASSES:
    vts = sorted(set(find_vtable(cls)))
    print(f"\n{cls}: vtable candidates: {[hex(v) for v in vts]}")
    class_vts[cls] = vts

# pick the vtable with the most slots for each class (primary)
chosen = {}
for cls, vts in class_vts.items():
    best = None
    for vt in vts:
        slots = read_slots(vt, 64)
        if best is None or len(slots) > len(best[1]):
            best = (vt, slots)
    if best:
        chosen[cls] = best
        print(f"{cls}: chose vtable {best[0]:#010x} with {len(best[1])} text slots")

# slot-by-slot comparison table
maxslots = max(len(s) for _, s in chosen.values())
print("\n=== slot classification ===")
print("slot | " + " | ".join(f"{c:>12s}" for c in chosen))
base_slots = chosen["UIBaseObject"][1] if "UIBaseObject" in chosen else []
for i in range(min(maxslots, 36)):
    row = []
    notes = []
    ptrs = {}
    for cls, (vt, slots) in chosen.items():
        p = slots[i] if i < len(slots) else 0
        ptrs[cls] = p
        row.append(f"{p:#010x}" if p else "        --")
    # classify using UIList's pointer (most-derived interesting class)
    ref = ptrs.get("UIList") or ptrs.get("UIPage") or 0
    if ref:
        crc, rstr, n, _ = disasm_props(ref)
        shared = len(set(p for p in ptrs.values() if p)) == 1
        tag = []
        if crc:
            tag.append("CRC(char*-overload)")
        if rstr:
            tag.append(f'retstr="{rstr}"')
        if shared:
            tag.append("shared-all")
        if n <= 5:
            tag.append(f"tiny({n})")
        notes = " ".join(tag)
    print(f"{i:4d} | " + " | ".join(f"{r:>12s}" for r in row) + f"  {notes}")

print("\n=== GetTypeName check (slot 1 must return the class name) ===")
for cls, (vt, slots) in chosen.items():
    if len(slots) > 1:
        _, rstr, _, _ = disasm_props(slots[1])
        print(f"  {cls:14s} slot1 -> {slots[1]:#010x} returns {rstr!r}")
