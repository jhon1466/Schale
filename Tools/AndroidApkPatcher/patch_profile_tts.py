#!/usr/bin/env python3
"""Disable only the Android TTS hash-download coroutine that crashes in AnimeGamesProxy.

This is pinned to the currently inspected Blue Archive 1.93 ARM64 libil2cpp.so.
The coroutine immediately returns false (completed), leaving the optional call-name
audio unavailable while allowing profile UI to continue.
"""

from __future__ import annotations

import hashlib
import struct
import sys
import zipfile
from pathlib import Path

from patch_apk import open_zip, rewrite_apk

ENTRY = "lib/arm64-v8a/libil2cpp.so"
TARGETS = {
    "1d51644a6c6cb42419387ca82502b74da9be755967d5c3924f7298d683024ff0": ("1.93", 0x3151B64),
    "8952213ac1fde927c24c8edb8057dad42ee59275aedaea52ad8f9d59ceab16d5": ("1.96.474931", 0x4212448),
}
EXPECTED_ENTRY = bytes.fromhex("ff 43 01 d1 fe 57 03 a9")
# ARM64: mov w0, #0; ret. IEnumerator.MoveNext immediately completes.
DISABLED_ENTRY = bytes.fromhex("00 00 80 52 c0 03 5f d6")


def rva_to_offset(elf: bytes, rva: int) -> int:
    if elf[:4] != b"\x7fELF" or elf[4] != 2 or elf[5] != 1:
        raise SystemExit("libil2cpp.so no es ELF64 little-endian")
    phoff = struct.unpack_from("<Q", elf, 32)[0]
    phentsize = struct.unpack_from("<H", elf, 54)[0]
    phnum = struct.unpack_from("<H", elf, 56)[0]
    for i in range(phnum):
        p_type, _, p_offset, p_vaddr, _, p_filesz, _, _ = struct.unpack_from(
            "<IIQQQQQQ", elf, phoff + i * phentsize
        )
        if p_type == 1 and p_vaddr <= rva < p_vaddr + p_filesz:
            return p_offset + rva - p_vaddr
    raise SystemExit(f"RVA 0x{rva:x} no está respaldada por datos ELF")


def _resolve_movenext_rva_from_metadata(elf: bytes, metadata: bytes) -> int | None:
    """Resolve TTSService.<co_DownloadHash>d__36.MoveNext RVA dynamically from global-metadata.dat + libil2cpp.so."""
    if len(metadata) < 256 or metadata[:4] != b"\xaf\x1b\xb1\xfa":
        return None
    hdr = struct.unpack_from("<64I", metadata, 8)
    str_off, str_sz = hdr[4], hdr[5]
    m_off, m_sz = hdr[10], hdr[11]
    tdef_off, tdef_sz = hdr[38], hdr[39]

    def get_str(idx: int) -> str:
        end = metadata.find(b"\x00", str_off + idx)
        return metadata[str_off + idx : end].decode("utf-8", "ignore")

    tts_token = None
    for ti in range(tdef_sz // 88):
        nidx = struct.unpack_from("<I", metadata, tdef_off + ti * 88)[0]
        if get_str(nidx) == "<co_DownloadHash>d__36":
            # Verify parent/adjacent type is TTSService or has MoveNext
            for mi in range(0, m_sz, 36):
                m_nidx, decl_type, _, _, _, _, tok, _, _ = struct.unpack_from("<9I", metadata, m_off + mi)
                if decl_type == ti and get_str(m_nidx) == "MoveNext":
                    tts_token = tok
                    break
            if tts_token is not None:
                break

    if tts_token is None:
        return None

    # Find BlueArchive.dll CodeGenModule in ELF by locating pointer to "BlueArchive.dll\0"
    ba_str_off = elf.find(b"BlueArchive.dll\x00")
    if ba_str_off == -1:
        return None

    phoff = struct.unpack_from("<Q", elf, 32)[0]
    phentsize = struct.unpack_from("<H", elf, 54)[0]
    phnum = struct.unpack_from("<H", elf, 56)[0]
    loads = []
    for i in range(phnum):
        pt, _, po, pv, _, pf, _, _ = struct.unpack_from("<IIQQQQQQ", elf, phoff + i * phentsize)
        if pt == 1:
            loads.append((po, pv, pf))

    def off_to_va(off: int) -> int | None:
        for po, pv, pf in loads:
            if po <= off < po + pf:
                return pv + (off - po)
        return None

    def va_to_off(va: int) -> int | None:
        for po, pv, pf in loads:
            if pv <= va < pv + pf:
                return po + (va - pv)
        return None

    ba_str_va = off_to_va(ba_str_off)
    if ba_str_va is None:
        return None

    rid = (tts_token & 0xFFFFFF) - 1
    needle = struct.pack("<Q", ba_str_va)
    pos = 0
    while True:
        hit = elf.find(needle, pos)
        if hit == -1:
            break
        if hit + 24 <= len(elf):
            _, method_count, method_ptrs_va = struct.unpack_from("<QQQ", elf, hit)
            if 50000 < method_count < 300000 and method_ptrs_va > 0:
                mp_off = va_to_off(method_ptrs_va)
                if mp_off is not None and rid < method_count:
                    fn_va = struct.unpack_from("<Q", elf, mp_off + rid * 8)[0]
                    if fn_va > 0:
                        return fn_va
        pos = hit + 8
    return None


def patch_il2cpp_bytes(elf: bytes, metadata: bytes | None = None) -> tuple[bytes, int, str]:
    """Patch TTSService.<co_DownloadHash>d__36.MoveNext in ARM64 libil2cpp.so bytes."""
    digest = hashlib.sha256(elf).hexdigest()
    if digest in TARGETS:
        version, move_next_rva = TARGETS[digest]
    elif metadata is not None:
        resolved = _resolve_movenext_rva_from_metadata(elf, metadata)
        if resolved is None:
            raise SystemExit(f"Build ARM64 inesperado ({digest}) y no se pudo resolver co_DownloadHash; no se parcheó")
        version, move_next_rva = ("auto", resolved)
    else:
        raise SystemExit(f"Build ARM64 inesperado ({digest}); no se parcheó")

    offset = rva_to_offset(elf, move_next_rva)
    actual = elf[offset : offset + len(EXPECTED_ENTRY)]
    if actual == DISABLED_ENTRY:
        return elf, move_next_rva, f"{version} (ya parcheado)"
    if actual != EXPECTED_ENTRY:
        raise SystemExit(f"Bytes inesperados en MoveNext (RVA 0x{move_next_rva:x}): {actual.hex(' ')}; no se parcheó")

    patched = bytearray(elf)
    patched[offset : offset + len(DISABLED_ENTRY)] = DISABLED_ENTRY
    return bytes(patched), move_next_rva, version


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "Tools/AndroidApkPatcher/out/BlueArchive-Schale.apk"
    output = Path(sys.argv[2]) if len(sys.argv) > 2 else source.with_name("BlueArchive-Schale-ProfileFix-unsigned.apk")

    with zipfile.ZipFile(source) as apk:
        elf = apk.read(ENTRY)
        meta = apk.read("assets/bin/Data/Managed/Metadata/global-metadata.dat") if "assets/bin/Data/Managed/Metadata/global-metadata.dat" in apk.namelist() else None
    digest = hashlib.sha256(elf).hexdigest()
    patched_elf, move_next_rva, version = patch_il2cpp_bytes(elf, meta)
    if patched_elf == elf:
        raise SystemExit("El TTS coroutine ya está desactivado")

    rewrite_apk(source, output, {ENTRY: patched_elf})
    print(f"Parche aplicado ({version}) a {ENTRY} RVA 0x{move_next_rva:x}; SHA256 previo {digest}")
    print(f"APK sin firmar: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
