# Verification pass for the s_guildRoomId hunt (find_guildroomid.py found the
# candidates). Three independent checks:
#
# 1. Disassemble the three room-id setters called from receiveOnEnteredRoom
#    (0xa2e270 planet, 0xa2e510 group, 0xa2e7b0 guild) — each should store its
#    argument to a distinct .data dword; the guild one gives s_guildRoomId.
#
# 2. The compare chain loads each room name's c_str through a .data pointer
#    (0x1939fa0 / f7c / f64 / f58). Those are VC6-style std::string statics,
#    heap-filled at CRT init, so the on-disk value is useless — instead find
#    the literals ("system", "Planet", "GroupChat", "GuildChat") in .data and
#    the static-init code that copies each literal into its string object, and
#    match those objects to the compare chain's pointers.
#
# 3. Cross-reference: every other .text reference to the s_guildRoomId dword
#    should sit in functions consistent with the fork (getGuildRoomId reader,
#    the leave-room zeroing, isRoomIgnorable-style compares).

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

def foff_to_va(foff):
    for name, vaddr, vsize, raddr, rsize in sections:
        if raddr <= foff < raddr + rsize:
            return image_base + vaddr + (foff - raddr)
    return None

def va_to_foff(va):
    rva = va - image_base
    for name, vaddr, vsize, raddr, rsize in sections:
        if vaddr <= rva < vaddr + vsize:
            return raddr + (rva - vaddr)
    return None

def section_of(va):
    rva = va - image_base
    for name, vaddr, vsize, raddr, rsize in sections:
        if vaddr <= rva < vaddr + vsize:
            return name
    return None

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
md.detail = True

def disasm(va, length=0x120, label=""):
    print(f"\n--- {label} @ {va:#010x} ---")
    foff = va_to_foff(va)
    for insn in md.disasm(data[foff:foff + length], va):
        note = ""
        for op in insn.operands:
            v = None
            if op.type == capstone.x86.X86_OP_IMM:
                v = op.imm
            elif op.type == capstone.x86.X86_OP_MEM and op.mem.base == 0 and op.mem.index == 0:
                v = op.mem.disp
            if v and v > image_base and section_of(v) in (".data", ".rdata"):
                note += f"  [{section_of(v)} {v:#x}]"
        print(f"{insn.address:08x}  {insn.mnemonic:8s} {insn.op_str:36s} {note}")
        if insn.mnemonic == "ret":
            break

# --- 1. the setters ---
for va, label in ((0xa2e270, "setPlanetRoomId?"), (0xa2e510, "setGroupRoomId?"),
                  (0xa2e7b0, "setGuildRoomId?")):
    disasm(va, 0x80, label)

# --- 2. literal -> static string object mapping ---
print("\n\n=== literal references (static init) ===")
for lit in (b"system", b"Planet", b"GroupChat", b"GuildChat", b"CityChat"):
    # find the literal as a full C string
    positions = []
    p = 0
    while True:
        p = data.find(b"\0" + lit + b"\0", p)
        if p < 0:
            break
        positions.append(p + 1)
        p += 1
    for pos in positions:
        va = foff_to_va(pos)
        if section_of(va) not in (".data", ".rdata"):
            continue
        # code refs to the literal
        needle = struct.pack("<I", va)
        q = 0
        refs = []
        while True:
            q = data.find(needle, q)
            if q < 0:
                break
            rva = foff_to_va(q)
            if rva is not None and section_of(rva) == ".text":
                refs.append(rva)
            q += 1
        if refs:
            print(f"\n{lit!r} at VA {va:#010x}, code refs: {[hex(r) for r in refs]}")
            for r in refs[:4]:
                # show surrounding instructions to catch the string-object address
                foff = va_to_foff(r)
                start = foff - 0x20
                for insn in md.disasm(data[start:start + 0x60], foff_to_va(start)):
                    marker = " <== ref" if insn.address <= r < insn.address + insn.size else ""
                    print(f"    {insn.address:08x}  {insn.mnemonic:8s} {insn.op_str}{marker}")

# --- 3. all .text refs to the guild static (filled in after step 1 output) ---
import sys
if len(sys.argv) > 1:
    static_va = int(sys.argv[1], 16)
    needle = struct.pack("<I", static_va)
    print(f"\n\n=== .text references to {static_va:#x} ===")
    q = 0
    while True:
        q = data.find(needle, q)
        if q < 0:
            break
        rva = foff_to_va(q)
        if rva is not None and section_of(rva) == ".text":
            start = va_to_foff(rva) - 0x10
            print(f"\nref at {rva:#010x}:")
            for insn in md.disasm(data[start:start + 0x30], foff_to_va(start)):
                print(f"    {insn.address:08x}  {insn.mnemonic:8s} {insn.op_str}")
        q += 1
