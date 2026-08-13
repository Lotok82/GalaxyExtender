# Which vtable pointer does the exe's `new UIData` actually install?
#
# /emu find's per-row vtable check (object's first dword == RTTI-derived
# vtable VA) appears to reject every row in-game. This script finds all code
# sites that store the RTTI-derived UIData/UIText vtable addresses (ctor
# candidates) and, conversely, all OTHER vtable-looking constants stored right
# next to them, to see if the constructor installs a different (duplicate)
# vtable than the one the RTTI scan found.

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

def find_all(needle):
    out = []
    pos = data.find(needle)
    while pos >= 0:
        out.append(pos)
        pos = data.find(needle, pos + 1)
    return out

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)

TEXT_RADDR = sections[0][3]
TEXT_RSIZE = sections[0][4]

def code_refs(va):
    """file offsets in .text where the 4-byte VA constant appears"""
    hits = []
    for h in find_all(struct.pack("<I", va)):
        if TEXT_RADDR <= h < TEXT_RADDR + TEXT_RSIZE:
            hits.append(h)
    return hits

def dump_around(foff, back=0x30, fwd=0x30, label=""):
    start = foff - back
    va = foff_to_va(start)
    print(f"\n--- {label} around {foff_to_va(foff):#010x} ---")
    for ins in md.disasm(data[start:start + back + fwd], va):
        mark = "  <== ref" if ins.address <= foff_to_va(foff) < ins.address + ins.size else ""
        print(f"  {ins.address:#010x}: {ins.mnemonic:8s} {ins.op_str}{mark}")

for cls, vt in [("UIData", 0x015FAF9C), ("UIText", 0x015FA1D4), ("UIList", 0x015FB2B4)]:
    refs = code_refs(vt)
    print(f"\n{cls} vtable {vt:#010x}: {len(refs)} code refs")
    for r in refs[:6]:
        dump_around(r, label=f"{cls} vtable store/use")
