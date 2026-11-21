"""Build a signed test APK that completes the lobby's offline purchase gate."""

from __future__ import annotations

import argparse
import hashlib
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

from patch_apk import open_zip, rewrite_apk, tool


LIB = "lib/arm64-v8a/libil2cpp.so"
RVA = 0x2F4997C
ORIGINAL = bytes.fromhex("a8060036")
PATCHED = bytes.fromhex("35000014")  # b 0x2f49a50: existing finish=true branch


def patch_library(data: bytes) -> bytes:
    if data[:6] != b"\x7fELF\x02\x01":
        raise ValueError("Expected ELF64 little-endian")
    phoff = struct.unpack_from("<Q", data, 32)[0]
    phentsize = struct.unpack_from("<H", data, 54)[0]
    phnum = struct.unpack_from("<H", data, 56)[0]
    offset = None
    for index in range(phnum):
        p_type, _, p_offset, p_vaddr, _, p_filesz, _, _ = struct.unpack_from(
            "<IIQQQQQQ", data, phoff + index * phentsize
        )
        if p_type == 1 and p_vaddr <= RVA < p_vaddr + p_filesz:
            offset = p_offset + RVA - p_vaddr
            break
    if offset is None or data[offset:offset + 4] != ORIGINAL:
        raise ValueError("Unexpected lobby instruction; refusing to patch")
    result = bytearray(data)
    result[offset:offset + 4] = PATCHED
    return bytes(result)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--keystore", type=Path, default=Path.home() / ".schale-android/release.jks")
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve():
        raise ValueError("Source and output must differ")
    password = args.keystore.with_suffix(".pass").read_text(encoding="utf-8").strip()
    with tempfile.TemporaryDirectory(prefix="schale_lobby_") as temp:
        temp = Path(temp)
        with open_zip(args.source) as apk:
            library = apk.read(LIB)
        rewritten = temp / "rewritten.apk"
        aligned = temp / "aligned.apk"
        rewrite_apk(args.source, rewritten, {LIB: patch_library(library)})
        subprocess.run([tool("zipalign"), "-f", "-p", "4", str(rewritten), str(aligned)], check=True)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run([
            tool("apksigner"), "sign", "--ks", str(args.keystore),
            "--ks-key-alias", "schale", "--ks-pass", f"pass:{password}",
            "--key-pass", f"pass:{password}", "--out", str(args.output), str(aligned),
        ], check=True)
        subprocess.run([tool("apksigner"), "verify", str(args.output)], check=True)
        args.output.with_suffix(".apk.idsig").unlink(missing_ok=True)
    print(hashlib.sha256(args.output.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
