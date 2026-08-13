# Is the shipped client's UILowerString really the two-hash version from the
# fork source (get() resolves via a static hash->string map), or a different
# layout (e.g. embedded std::string)? Disassemble UILowerString::get()
# (0x10e5360 — called with this=Name at the top of UIList::SetProperty and
# UIBaseObject::GetProperty) and the UILowerString(const char*) constructor
# used by the client (find it via updateHash's CRC loop reference).

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

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)

def dump(va, n=80, label=""):
    print(f"\n----- {label} @ {va:#010x} -----")
    foff = va_to_foff(va)
    count = 0
    for ins in md.disasm(data[foff:foff + 0x400], va):
        print(f"  {ins.address:#010x}: {ins.mnemonic:8s} {ins.op_str}")
        count += 1
        if ins.mnemonic == "ret":
            break
        if count >= n:
            print("   ... (truncated)")
            break

dump(0x010E5360, 60, "UILowerString::get()? (this=Name)")

# the CRC tables found earlier: 0x15ea5d0, 0x15f9790, 0x15fe3f8 — find code
# referencing each (calculateHashWithLower inlined into updateHash / ctor)
TEXT_RADDR, TEXT_RSIZE = sections[0][3], sections[0][4]
for table_va in (0x15EA5D0, 0x15F9790, 0x15FE3F8):
    needle = struct.pack("<I", table_va)
    hits = []
    pos = data.find(needle)
    while pos >= 0:
        if TEXT_RADDR <= pos < TEXT_RADDR + TEXT_RSIZE:
            hits.append(pos)
        pos = data.find(needle, pos + 1)
    print(f"\ncrc table {table_va:#x}: {len(hits)} code refs")
    for h in hits[:3]:
        # walk back a bit and disassemble
        start_va = image_base + sections[0][1] + (h - TEXT_RADDR) - 0x40
        dump(start_va, 40, f"code near ref to crctable {table_va:#x}")
