import struct, sys

EXE = r"D:\Galaxies\SWGEmu_Clone\SWGEmu.exe"
NEEDLE = b"Attempt to SwgCuiChatWindow::Tab::appendText empty string"

data = open(EXE, "rb").read()
print(f"file size: {len(data):#x}")

# --- parse PE headers ---
e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
assert data[e_lfanew:e_lfanew+4] == b"PE\0\0"
num_sections = struct.unpack_from("<H", data, e_lfanew + 6)[0]
opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
opt_off = e_lfanew + 24
image_base = struct.unpack_from("<I", data, opt_off + 28)[0]
print(f"image base: {image_base:#x}, sections: {num_sections}")

sec_off = opt_off + opt_size
sections = []
for i in range(num_sections):
    off = sec_off + i * 40
    name = data[off:off+8].rstrip(b"\0").decode(errors="replace")
    vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
    sections.append((name, vaddr, vsize, raddr, rsize))
    print(f"  {name:8s} VA {image_base+vaddr:#010x} vsize {vsize:#x} raw {raddr:#x} rsize {rsize:#x}")

def foff_to_va(foff):
    for name, vaddr, vsize, raddr, rsize in sections:
        if raddr <= foff < raddr + rsize:
            return image_base + vaddr + (foff - raddr)
    return None

def va_to_foff(va):
    rva = va - image_base
    for name, vaddr, vsize, raddr, rsize in sections:
        if vaddr <= rva < vaddr + rsize:
            return raddr + (rva - vaddr)
    return None

# --- find the string ---
pos = data.find(NEEDLE)
if pos < 0:
    print("STRING NOT FOUND — trying shorter fragments")
    for frag in (b"Tab::appendText", b"SwgCuiChatWindow::Tab", b"SwgCuiChatWindow"):
        p = 0
        hits = []
        while True:
            p = data.find(frag, p)
            if p < 0: break
            hits.append(p)
            p += 1
        print(f"  {frag!r}: {len(hits)} hits")
        for h in hits[:10]:
            start = data.rfind(b"\0", 0, h) + 1
            end = data.find(b"\0", h)
            print(f"    foff {h:#x} VA {foff_to_va(h)} :: {data[start:end][:100]!r}")
    sys.exit(0)

str_va = foff_to_va(pos)
print(f"\nstring found at file offset {pos:#x}, VA {str_va:#010x}")

# --- find code references to the string VA (any 4-byte LE occurrence in code sections) ---
target = struct.pack("<I", str_va)
code_secs = [s for s in sections if s[0] in (".text", "CODE") or s[1] == sections[0][1]]
refs = []
p = 0
while True:
    p = data.find(target, p)
    if p < 0: break
    va = foff_to_va(p)
    if va is not None:
        refs.append((p, va))
    p += 1

print(f"\nreferences to string VA: {len(refs)}")
for foff, va in refs:
    ctx = data[foff-8:foff+8]
    print(f"  at foff {foff:#x} VA {va:#010x}  bytes: {ctx.hex(' ')}")
    # walk back to find a function prologue
    # common MSVC prologues: 6A FF 68 xx xx xx xx 64 A1 00 00 00 00 (SEH), 55 8B EC, 51/83 EC after 55 8B EC
    for back in range(0, 0x800):
        q = foff - back
        if data[q:q+3] == b"\x55\x8b\xec":
            # check preceding bytes look like function end/padding (CC, C3, C2 xx xx, 90)
            prev = data[q-1]
            if prev in (0xCC, 0xC3, 0x90) or data[q-3] == 0xC2:
                print(f"    candidate prologue (55 8B EC) at VA {foff_to_va(q):#010x} (-{back:#x}) prev byte {prev:#04x}")
                break
        if data[q:q+3] == b"\x6a\xff\x68" and data[q+7:q+13] == b"\x64\xa1\x00\x00\x00\x00":
            prev = data[q-1]
            if prev in (0xCC, 0xC3, 0x90) or data[q-3] == 0xC2:
                print(f"    candidate SEH prologue (6A FF 68) at VA {foff_to_va(q):#010x} (-{back:#x}) prev byte {prev:#04x}")
                break
    else:
        print("    no prologue found within 0x800 bytes")
