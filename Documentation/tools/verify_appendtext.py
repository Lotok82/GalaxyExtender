import struct

EXE = r"D:\Galaxies\SWGEmu_Clone\SWGEmu.exe"
data = open(EXE, "rb").read()

IMAGE_BASE = 0x400000
SECTIONS = [(".text", 0x1000, 0x11dac9d, 0x1000, 0x11db000),
            (".rdata", 0x11dc000-0x1000+0x1000, 0, 0x11dc000, 0x271000),  # unused
            ]

def va_to_foff(va):
    rva = va - IMAGE_BASE
    # .text
    if 0x1000 <= rva < 0x1000 + 0x11db000:
        return 0x1000 + (rva - 0x1000)
    # .rdata VA 0x015dc000
    if 0x15dc000-0x400000 <= rva < 0x15dc000-0x400000 + 0x271000:
        return 0x11dc000 + (rva - (0x15dc000-0x400000))
    # .data VA 0x0184d000 raw 0x144d000 rsize 0xbb000
    if 0x184d000-0x400000 <= rva < 0x184d000-0x400000 + 0xbb000:
        return 0x144d000 + (rva - (0x184d000-0x400000))
    return None

def foff_to_va(foff):
    if 0x1000 <= foff < 0x1000+0x11db000:
        return IMAGE_BASE + foff
    if 0x11dc000 <= foff < 0x11dc000+0x271000:
        return 0x15dc000 + (foff - 0x11dc000)
    if 0x144d000 <= foff < 0x144d000+0xbb000:
        return 0x184d000 + (foff - 0x144d000)
    return None

FUNC = 0x0102DA80

# 1) dump first 0x60 bytes of candidate function
foff = va_to_foff(FUNC)
print(f"bytes at {FUNC:#010x}:")
b = data[foff:foff+0x60]
for i in range(0, 0x60, 16):
    print(f"  {FUNC+i:08x}: {b[i:i+16].hex(' ')}")

# 2) find all E8 rel32 calls targeting FUNC in .text
text_raw, text_size = 0x1000, 0x11db000
callers = []
for p in range(text_raw, text_raw + text_size - 5):
    if data[p] == 0xE8:
        rel = struct.unpack_from("<i", data, p+1)[0]
        src_va = foff_to_va(p)
        if src_va is not None and (src_va + 5 + rel) == FUNC:
            callers.append(src_va)
print(f"\ncallers of {FUNC:#x}: {len(callers)}")
for c in callers:
    # find enclosing function prologue
    q = va_to_foff(c)
    proto = "?"
    for back in range(0x1000):
        r = q - back
        if data[r:r+3] == b"\x55\x8b\xec" and (data[r-1] in (0xCC, 0xC3, 0x90) or data[r-3] == 0xC2):
            proto = f"{foff_to_va(r):#010x}"
            break
        if data[r:r+3] == b"\x6a\xff\x68" and data[r+7:r+13] == b"\x64\xa1\x00\x00\x00\x00" and (data[r-1] in (0xCC, 0xC3, 0x90) or data[r-3] == 0xC2):
            proto = f"{foff_to_va(r):#010x} (SEH)"
            break
    print(f"  call at {c:#010x}, enclosing function starts ~{proto}")

# 3) useful nearby strings for later (settings keys, chat window names)
print("\nstrings of interest:")
for needle in (b"WS_ChatWindow", b"ChatWindow", b"tabId", b"tabs.", b"chatlog", b"_chatlog"):
    p, hits = 0, []
    while len(hits) < 8:
        p = data.find(needle, p)
        if p < 0: break
        start = data.rfind(b"\0", 0, p) + 1
        end = data.find(b"\0", p)
        s = data[start:end]
        if len(s) < 120:
            hits.append((p, s))
        p = end
    for h, s in hits:
        va = foff_to_va(h)
        print(f"  {needle!r} foff {h:#x} VA {va if va is None else hex(va)} :: {s!r}")
