# Hunt for CuiChatRoomManager's s_guildRoomId static in the SWG client binary.
#
# Anchor: the WARNING string unique to CuiChatRoomManager::receiveOnEnteredRoom
# ("received ChatOnEnteredRoom but room [%d] doesn't exist on client.", fork
# CuiChatRoomManager.cpp:1160). That function, on CHATRESULT_SUCCESS, walks an
# _stricmp chain over the room's short name in source order — "system",
# "Planet", "GroupChat", "GuildChat", "CityChat" — and each match stores the
# room id into that room type's static (setXxxRoomId, fork :1169-1198). The
# store after the 4th compare is s_guildRoomId.
#
# Output: annotated disassembly of the function with every CALL target,
# every store to a .data address, and every immediate that lands in .data
# (the static std::string objects the compares reference).

import struct
import capstone

EXE = r"D:\Galaxies\SWGEmu_Clone\SWGEmu.exe"
NEEDLE = b"received ChatOnEnteredRoom but room"

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

def cstr_at_va(va):
    foff = va_to_foff(va)
    if foff is None:
        return None
    end = data.find(b"\0", foff, foff + 200)
    if end < 0:
        return None
    s = data[foff:end]
    if s and all(32 <= c < 127 for c in s):
        return s
    return None

# --- find the anchor string ---
pos = data.find(NEEDLE)
assert pos >= 0, "anchor string not found"
str_va = foff_to_va(pos)
print(f"anchor string at foff {pos:#x}, VA {str_va:#010x}")
assert data.find(NEEDLE, pos + 1) < 0, "anchor string not unique"

# --- code references ---
target = struct.pack("<I", str_va)
refs = []
p = 0
while True:
    p = data.find(target, p)
    if p < 0:
        break
    va = foff_to_va(p)
    if va is not None and section_of(va) == ".text":
        refs.append((p, va))
    p += 1
print(f"code refs: {[hex(va) for _, va in refs]}")
assert len(refs) == 1, "expected exactly one code ref"

ref_foff = refs[0][0]

# --- walk back to the function prologue ---
func_foff = None
for back in range(0x2000):
    q = ref_foff - back
    if data[q:q + 3] == b"\x55\x8b\xec" and (data[q - 1] in (0xCC, 0xC3, 0x90) or data[q - 3] == 0xC2):
        func_foff = q
        break
    if data[q:q + 3] == b"\x6a\xff\x68" and data[q + 7:q + 13] == b"\x64\xa1\x00\x00\x00\x00" \
            and (data[q - 1] in (0xCC, 0xC3, 0x90) or data[q - 3] == 0xC2):
        func_foff = q
        break
assert func_foff is not None, "no prologue found"
func_va = foff_to_va(func_foff)
print(f"\nreceiveOnEnteredRoom candidate at VA {func_va:#010x}\n")

# --- disassemble ---
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
md.detail = True

LENGTH = 0x900
code = data[func_foff:func_foff + LENGTH]

call_counts = {}
lines = []
for insn in md.disasm(code, func_va):
    note = ""
    if insn.mnemonic == "call" and insn.op_str.startswith("0x"):
        tgt = int(insn.op_str, 16)
        call_counts[tgt] = call_counts.get(tgt, 0) + 1
        note = f"-> {tgt:#x}"
    # any immediate that lands in .data / .rdata: annotate, and show a C string if one is there
    for op in insn.operands:
        vals = []
        if op.type == capstone.x86.X86_OP_IMM:
            vals.append(op.imm)
        elif op.type == capstone.x86.X86_OP_MEM and op.mem.base == 0 and op.mem.index == 0:
            vals.append(op.mem.disp)
        for v in vals:
            sec = section_of(v) if v and v > image_base else None
            if sec in (".data", ".rdata"):
                s = cstr_at_va(v)
                note += f"  [{sec} {v:#x}" + (f" = {s!r}" if s else "") + "]"
    lines.append(f"{insn.address:08x}  {insn.mnemonic:8s} {insn.op_str:40s} {note}")

print("\n".join(lines))

print("\ncall targets by frequency:")
for tgt, n in sorted(call_counts.items(), key=lambda kv: -kv[1]):
    print(f"  {tgt:#010x} called {n}x")
