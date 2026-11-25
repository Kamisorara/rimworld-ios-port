#!/usr/bin/env python3
"""Report the load commands, CodeDirectory and page hashes of a Mach-O.

The iOS kernel names a rejected code signature by its cdhash ("<0E435D2A-...>"), so the
number computed here from the IPA tells whether the device ran the CI's ad-hoc file
or something the installer re-signed.
"""
import hashlib
import struct
import sys
import uuid

LC_NAMES = {
    0x1: "LC_SEGMENT", 0x2: "LC_SYMTAB", 0x5: "LC_UNIXTHREAD", 0xB: "LC_DYSYMTAB", 0xC: "LC_LOAD_DYLIB",
    0xD: "LC_ID_DYLIB", 0xE: "LC_LOAD_DYLINKER", 0x18: "LC_LOAD_WEAK_DYLIB", 0x19: "LC_SEGMENT_64",
    0x1B: "LC_UUID", 0x1D: "LC_CODE_SIGNATURE", 0x1E: "LC_SEGMENT_SPLIT_INFO", 0x20: "LC_LAZY_LOAD_DYLIB",
    0x21: "LC_ENCRYPTION_INFO", 0x22: "LC_DYLD_INFO", 0x24: "LC_VERSION_MIN_MACOSX", 0x25: "LC_VERSION_MIN_IPHONEOS",
    0x26: "LC_FUNCTION_STARTS", 0x29: "LC_DATA_IN_CODE", 0x2A: "LC_SOURCE_VERSION", 0x2B: "LC_DYLIB_CODE_SIGN_DRS",
    0x2C: "LC_ENCRYPTION_INFO_64", 0x2E: "LC_LINKER_OPTIMIZATION_HINT", 0x31: "LC_NOTE", 0x32: "LC_BUILD_VERSION",
    0x8000001C: "LC_RPATH", 0x8000001F: "LC_REEXPORT_DYLIB", 0x80000022: "LC_DYLD_INFO_ONLY",
    0x80000023: "LC_LOAD_UPWARD_DYLIB", 0x80000028: "LC_MAIN", 0x80000033: "LC_DYLD_EXPORTS_TRIE",
    0x80000034: "LC_DYLD_CHAINED_FIXUPS", 0x80000035: "LC_FILESET_ENTRY",
}
PLATFORMS = {1: "macos", 2: "ios", 3: "tvos", 4: "watchos", 5: "bridgeos", 6: "maccat", 7: "iossim"}
SLOT_NAMES = {0: "CodeDirectory", 2: "Requirements", 3: "ResourceDir", 4: "ApplicationSpecific", 5: "Entitlements", 0x10000: "CMS"}


def cstr(buf, off):
    end = buf.index(b"\0", off)
    return buf[off:end].decode("utf-8", "replace")


def thin(data, base=0):
    magic, cputype, cpusub, filetype, ncmds, sizeofcmds, flags, _ = struct.unpack_from("<IiiIIIII", data, base)
    return magic, cputype, filetype, ncmds, sizeofcmds


def report(path, data, base):
    magic, cputype, filetype, ncmds, sizeofcmds = thin(data, base)
    print(f"{path}: cputype=0x{cputype:x} filetype={filetype} ncmds={ncmds}")
    off = base + 32
    sig = None
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, off)
        name = LC_NAMES.get(cmd, hex(cmd))
        extra = ""
        if cmd == 0x19:
            segname = cstr(data, off + 8)
            vmaddr, vmsize, fileoff, filesize = struct.unpack_from("<QQQQ", data, off + 24)
            extra = f" {segname} fileoff=0x{fileoff:x} filesize=0x{filesize:x}"
        elif cmd in (0xD, 0xC, 0x18, 0x20, 0x8000001F, 0x80000023):
            extra = " " + cstr(data, off + struct.unpack_from("<I", data, off + 8)[0])
        elif cmd == 0x32:
            platform, minos, sdk, ntools = struct.unpack_from("<IIII", data, off + 8)
            extra = f" platform={platform}({PLATFORMS.get(platform, '?')}) minos=0x{minos:x} sdk=0x{sdk:x}"
        elif cmd == 0x1D:
            dataoff, datasize = struct.unpack_from("<II", data, off + 8)
            sig = (dataoff, datasize)
            extra = f" dataoff=0x{dataoff:x} datasize=0x{datasize:x}"
        print(f"  {name}{extra}")
        off += size
    if not sig:
        print("  no LC_CODE_SIGNATURE")
        return
    dataoff, datasize = sig
    blob = data[dataoff:dataoff + datasize]
    sb_magic, sb_len, sb_count = struct.unpack_from(">III", blob, 0)
    print(f"  superblob magic=0x{sb_magic:x} len={sb_len} count={sb_count} (last byte of file: 0x{len(data):x}; signature ends 0x{dataoff + datasize:x})")
    cd_off = None
    for i in range(sb_count):
        typ, boff = struct.unpack_from(">II", blob, 12 + 8 * i)
        blen = struct.unpack_from(">I", blob, boff + 4)[0]
        print(f"  slot {SLOT_NAMES.get(typ, hex(typ))}: offset=0x{boff:x} size=0x{blen:x}")
        if typ == 0:
            cd_off = boff
    if cd_off is None:
        print("  no CodeDirectory")
        return
    cd = blob[cd_off:]
    magic, length, version, flags, hash_off, ident_off, nspecial, ncode, code_limit, hash_size, hash_type, platform, page_size, _spare2 = struct.unpack_from(">IIIIIIIIIBBBBI", cd, 0)
    hash_name = {1: "sha1", 2: "sha256", 3: "sha256-trunc", 4: "sha384"}.get(hash_type, str(hash_type))
    ident = cstr(cd, ident_off)
    print(f"  CodeDirectory v=0x{version:x} len={length} flags=0x{flags:x} ident={ident!r} hash={hash_name} page={1 << page_size} platform={platform} ncode={ncode} codeLimit=0x{code_limit:x}")
    if version >= 0x20200:
        team_off = struct.unpack_from(">I", cd, 52)[0]
        print(f"  teamOffset=0x{team_off:x} team={cstr(cd, team_off) if team_off else ''!r}")
    if version >= 0x20400:
        base, limit, eflags = struct.unpack_from(">QQQ", cd, 68)
        print(f"  execSegBase=0x{base:x} execSegLimit=0x{limit:x} execSegFlags=0x{eflags:x}")
    for s in range(1, nspecial + 1):
        want = cd[hash_off - s * hash_size:hash_off - (s - 1) * hash_size]
        print(f"  special slot -{s}: {'zero' if want == bytes(hash_size) else want.hex()}")
    digest = hashlib.sha256(cd[:length]).digest() if hash_type in (2, 3) else hashlib.sha1(cd[:length]).digest()
    print(f"  cdhash={digest.hex().upper()}  dyld-uuid={str(uuid.UUID(bytes=digest[:16])).upper()}")
    ok = bad = 0
    for i in range(ncode):
        page = data[i << page_size:min((i + 1) << page_size, code_limit)]
        want = cd[hash_off + i * hash_size:hash_off + (i + 1) * hash_size]
        got = hashlib.sha256(page).digest() if hash_type in (2, 3) else hashlib.sha1(page).digest()
        if got[:hash_size] == want:
            ok += 1
        else:
            bad += 1
    print(f"  page hashes: {ok} ok, {bad} bad")


def main(path):
    with open(path, "rb") as f:
        data = f.read()
    magic = struct.unpack_from(">I", data, 0)[0]
    if magic in (0xCAFEBABE, 0xBEBAFECA):
        narch = struct.unpack_from(">I", data, 4)[0]
        for i in range(narch):
            cputype, cpusub, off, size, _align = struct.unpack_from(">IIIII", data, 8 + 20 * i)
            print(f"slice {i}: cputype=0x{cputype:x} offset=0x{off:x} size=0x{size:x}")
            report(f"{path}#arm64", data[off:off + size], 0)
    else:
        report(path, data, 0)


if __name__ == "__main__":
    for p in sys.argv[1:]:
        main(p)
