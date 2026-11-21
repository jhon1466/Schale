#!/usr/bin/env python3
"""Prepare an LSPatch + AnimeGamesProxy Blue Archive APK set for Schale Server.

The client RSA-encrypts the gateway handshake (protocol 50001) with Nexon's public key
embedded in assets/bin/Data/Managed/Metadata/global-metadata.dat. The server can only
decrypt it with its own key pair, so the official key is replaced by the server's
(Schale-Server/config/GatewayPublicKey.pem), the same way ClientMetadataPatchService
patches the Steam client. Every split is then zipaligned and re-signed with one key,
because Android refuses split APKs signed with different certificates.

  python Tools/AndroidApkPatcher/patch_apk.py Blue_Archive-apks.zip out_dir
      [--public-key Schale-Server/config/GatewayPublicKey.pem] [--keystore path.jks]

The splits are folded into one APK (out_dir/BlueArchive-Schale.apk) that installs by tapping it.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import secrets
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

from axml import Manifest

METADATA = "assets/bin/Data/Managed/Metadata/global-metadata.dat"
ORIGIN = "assets/lspatch/origin.apk"
MANIFEST = "AndroidManifest.xml"
OUTPUT_NAME = "BlueArchive-Schale.apk"
# Official Nexon gateway RSA-4096 key, byte-identical to its copy in global-metadata.dat
# (Schale-Server/Services/ClientMetadataPatchService.cs).
OFFICIAL_KEY = (
    "-----BEGIN PUBLIC KEY-----\n"
    "MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAz1oODtVINPQsTIbFDiL2\n"
    "IIj3YqDGxZ1WqJWfJrpo7ZqdnSIMXaVqVyM07Zguc9wczPrluDNowheP8NXe8uTR\n"
    "prO7jpF9bOcrrit9Xyxf57NA5rIMqOj6/XgWzzptvvt3oUrOQJRTvEjusQCX79Ds\n"
    "fhhbPRfdPzZUOPYa49MM385Z05ndNiTB9/yhxP5+5zm7TS3f6/o1Ntw9yXoRYmgR\n"
    "8+JYGL6TpqtZHkqPTp6kIJrnsLyT1wRteh3oQKJaze0CM0DUUzFiqieRE8c8JAoV\n"
    "jk/yRQUqVMoKbdpINYlWhF+GFE/ccSpqQymepHxfR0alspynqJwJgODCeHtwuno5\n"
    "HEOwdbA8NanLVnE9qlvdeNkbODdt/XTHKOaXx+SUMHezUY0rEXp8WMim0i9IQTGa\n"
    "YWeW25RjXIZswKdg0gLRJXYZXUgF3nVcqr0hi4kv1pgrFPsjI7yIThxLY0UfomkR\n"
    "oe6sSBBtQUDI85s+1pZRcl8xygxOxMfFSk5RB3EntFmcf/AKaLYQbXN9RxgrvqRS\n"
    "9koBBEnnrIN0zXDBioZUDOWVBeBZwAydpPGKu2mkqu3BI/Al92noEHc6fmkG5+Qm\n"
    "b237xSh1DFbbQO6lxg4ABdsqgvZskYS+7BcQPjs1z2zftrnesFkqD7BhbqXUBuDY\n"
    "D80B8Be5I483qSSy8DsW458CAwEAAQ==\n"
    "-----END PUBLIC KEY-----"
)
# The literal is stored as three string literals split at these lengths (see ClientMetadataPatchService).
CHUNKS = (272, 272, 255)


def split(pem: str) -> list[bytes]:
    out, pos = [], 0
    for n in CHUNKS:
        out.append(pem[pos : pos + n].encode())
        pos += n
    return out


def patch_metadata(data: bytes, public_key: str) -> bytes:
    """Replace each official key chunk with the server's; refuse on any ambiguity."""
    ours = public_key.replace("\r\n", "\n").strip("\n")
    if len(ours) != len(OFFICIAL_KEY):
        raise SystemExit(f"La clave del servidor mide {len(ours)} caracteres; debe ser RSA-4096 PEM de {len(OFFICIAL_KEY)}")
    buf = bytearray(data)
    for official, replacement in zip(split(OFFICIAL_KEY), split(ours)):
        if buf.count(replacement) == 1 and buf.count(official) == 0:
            continue  # already patched
        if buf.count(official) != 1:
            raise SystemExit(f"Fragmento de la clave oficial encontrado {buf.count(official)} veces; no se parchea")
        at = buf.find(official)
        buf[at : at + len(official)] = replacement
    return bytes(buf)


def tool(name: str) -> str:
    sdk = Path(os.environ.get("ANDROID_HOME") or os.environ.get("LOCALAPPDATA", "") + "/Android/Sdk")
    build_tools = sorted((sdk / "build-tools").glob("*"))
    for directory in reversed(build_tools):
        for candidate in (directory / f"{name}.exe", directory / f"{name}.bat", directory / name):
            if candidate.is_file():
                return str(candidate)
    found = shutil.which(name)
    if not found:
        raise SystemExit(f"No encuentro {name}; instala Android SDK build-tools o define ANDROID_HOME")
    return found


def keytool() -> str:
    for candidate in (
        Path(os.environ.get("JAVA_HOME", "")) / "bin/keytool.exe",
        Path(r"C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe"),
    ):
        if candidate.is_file():
            return str(candidate)
    found = shutil.which("keytool")
    if not found:
        raise SystemExit("No encuentro keytool (JDK)")
    return found


def open_zip(path: Path) -> zipfile.ZipFile:
    """LSPatch stores origin.apk so that its data region IS the original APK's entries, which the outer
    central directory also lists: the overlap is deliberate. Python 3.12+ rejects overlapping entries as
    a zip bomb, so the end-offset guard is cleared for these files."""
    z = zipfile.ZipFile(path)
    for info in z.infolist():
        info._end_offset = None
    return z


def single_apk_manifest(data: bytes) -> bytes:
    """Make the base manifest installable on its own: drop the split requirements
    (requiredSplitTypes/splitTypes, the com.android.vending.splits resource) and tell Play Core
    that splits are not required, so it does not ask to reinstall from Google Play."""
    m = Manifest(data)
    root = next(e for e in m.elements if e.name == "manifest")
    for name in ("requiredSplitTypes", "splitTypes"):
        attr = next((a for a in root.attrs if a.name == name), None)
        if attr:
            m.remove_attr(root, attr)
            root = next(e for e in m.elements if e.name == "manifest")
    for el in list(m.elements):
        if el.name != "meta-data":
            continue
        name = next((m.string(a) for a in el.attrs if a.name == "name"), None)
        if name == "com.android.vending.splits.required":
            m.set_boolean(next(a for a in el.attrs if a.name == "value"), False)
    splits = next((e for e in m.elements if e.name == "meta-data"
                   and any(a.name == "name" and m.string(a) == "com.android.vending.splits" for a in e.attrs)), None)
    if splits:
        m.remove_element(splits)
    return bytes(m.data)


def rewrite_apk(src: Path, dst: Path, replace: dict[str, bytes], keep_signature: bool = False,
                add: dict[str, bytes] | None = None) -> None:
    """Copy src to dst replacing entries, keeping each entry's compression and dropping old signatures.
    Entries in `add` are appended deflated: the manifest sets extractNativeLibs=true, so Android unpacks them on install."""
    with open_zip(src) as zin, zipfile.ZipFile(dst, "w") as zout:
        for name, data in (add or {}).items():
            if name not in zin.namelist():
                zout.writestr(zipfile.ZipInfo(name, date_time=(1981, 1, 1, 1, 1, 2)), data, compress_type=zipfile.ZIP_DEFLATED)
        for info in zin.infolist():
            signature = info.filename.startswith("META-INF/") and info.filename.upper().endswith((".SF", ".RSA", ".EC", ".DSA", "MANIFEST.MF"))
            if signature and not keep_signature:
                continue
            data = replace.get(info.filename)
            if data is None:
                data = zin.read(info)
            new = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            new.compress_type = info.compress_type
            new.external_attr = info.external_attr
            zout.writestr(new, data)


def patch_android_profile_and_purchase_popups(libil2cpp: bytes) -> bytes:
    """Apply the verified Android 1.93 workarounds to the merged ARM64 game library."""
    expected_hash = "1d51644a6c6cb42419387ca82502b74da9be755967d5c3924f7298d683024ff0"
    digest = hashlib.sha256(libil2cpp).hexdigest()
    if digest != expected_hash:
        raise SystemExit(f"libil2cpp.so ARM64 no inspeccionada ({digest}); no se parcheó")
    if libil2cpp[:6] != b"\x7fELF\x02\x01":
        raise SystemExit("libil2cpp.so no es ELF64 little-endian")

    phoff = struct.unpack_from("<Q", libil2cpp, 32)[0]
    phentsize = struct.unpack_from("<H", libil2cpp, 54)[0]
    phnum = struct.unpack_from("<H", libil2cpp, 56)[0]

    def file_offset(rva: int) -> int:
        for index in range(phnum):
            p_type, _, p_offset, p_vaddr, _, p_filesz, _, _ = struct.unpack_from(
                "<IIQQQQQQ", libil2cpp, phoff + index * phentsize
            )
            if p_type == 1 and p_vaddr <= rva < p_vaddr + p_filesz:
                return p_offset + rva - p_vaddr
        raise SystemExit(f"RVA 0x{rva:x} no está respaldada por datos ELF")

    patched = bytearray(libil2cpp)
    tts = file_offset(0x3151B64)
    if patched[tts : tts + 8] != bytes.fromhex("ff 43 01 d1 fe 57 03 a9"):
        raise SystemExit("Bytes inesperados en el coroutine TTS del perfil")
    patched[tts : tts + 8] = bytes.fromhex("00 00 80 52 c0 03 5f d6")  # mov w0,#0; ret

    # Purchase verification/recovery is unavailable offline. Skip its result
    # stage and notice callbacks; the lobby/account data are independent.
    for rva, prologue in (
        (0x2D42148, bytes.fromhex("ff c3 00 d1 fe 0b 00 f9")),  # daily-record purchase notice
        (0x426D5A4, bytes.fromhex("fe 57 be a9 f4 4f 01 a9")),  # invisible token
        (0x426EC40, bytes.fromhex("fe 0f 1e f8 f4 4f 01 a9")),  # result mailed
        (0x426D6A8, bytes.fromhex("fe 57 be a9 f4 4f 01 a9")),  # error order
        (0x2F47D20, bytes.fromhex("fe 57 be a9 f4 4f 01 a9")),  # lobby repurchase notice
    ):
        offset = file_offset(rva)
        if patched[offset : offset + len(prologue)] != prologue:
            raise SystemExit(f"Bytes inesperados en recuperación de compra RVA 0x{rva:x}")
        patched[offset : offset + 4] = bytes.fromhex("c0 03 5f d6")  # ret

    # UILobby.AddPopups waits for isCheckPurchaseStateFinish before it unlocks
    # input. Offline billing cannot complete that check reliably. Reuse the
    # method's existing no-purchase branch, which sets the finish flag.
    gate = file_offset(0x2F4997C)
    if patched[gate : gate + 4] != bytes.fromhex("a8 06 00 36"):
        raise SystemExit("Bytes inesperados en espera de compras del lobby")
    patched[gate : gate + 4] = bytes.fromhex("35 00 00 14")
    return bytes(patched)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("apks", type=Path, help="ZIP con base.apk y splits, o una carpeta con ellos")
    parser.add_argument("output", type=Path)
    root = Path(__file__).resolve().parents[2]
    parser.add_argument("--public-key", type=Path, default=root / "Schale-Server/config/GatewayPublicKey.pem")
    parser.add_argument("--keystore", type=Path, default=Path.home() / ".schale-android/release.jks")
    args = parser.parse_args()

    work = Path(tempfile.mkdtemp(prefix="ba_apk_"))
    try:
        if args.apks.is_dir():
            sources = sorted(args.apks.glob("*.apk"))
        else:
            with zipfile.ZipFile(args.apks) as z:
                z.extractall(work / "in")
            sources = sorted((work / "in").rglob("*.apk"))
        if not any(p.name == "base.apk" for p in sources):
            raise SystemExit("No hay base.apk en la entrada")

        public_key = args.public_key.read_text(encoding="utf-8")
        args.output.mkdir(parents=True, exist_ok=True)

        # One signing key for every split.
        args.keystore.parent.mkdir(parents=True, exist_ok=True)
        password_file = args.keystore.with_suffix(".pass")
        if not args.keystore.is_file():
            password_file.write_text(secrets.token_urlsafe(24), encoding="utf-8")
            subprocess.run([keytool(), "-genkeypair", "-keystore", str(args.keystore), "-alias", "schale",
                            "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
                            "-storepass", password_file.read_text().strip(), "-keypass", password_file.read_text().strip(),
                            "-dname", "CN=Schale Android"], check=True, capture_output=True)
        password = password_file.read_text(encoding="utf-8").strip()

        base = next(p for p in sources if p.name == "base.apk")
        replace: dict[str, bytes] = {}
        added: dict[str, bytes] = {}
        # Splits (config.arm64_v8a, ...) carry only native libs: fold them into the base so the result is a single APK.
        for split_apk in (p for p in sources if p is not base):
            with open_zip(split_apk) as z:
                for name in z.namelist():
                    if name.startswith("lib/"):
                        added[name] = z.read(name)
            print(f"{split_apk.name}: {sum(n.startswith('lib/') for n in added)} librerías añadidas al APK único")

        arm64_library = "lib/arm64-v8a/libil2cpp.so"
        if arm64_library in added:
            added[arm64_library] = patch_android_profile_and_purchase_popups(added[arm64_library])
            print("libil2cpp.so: TTS de perfil y popups de compra/aviso de recompra del lobby corregidos")

        for apk in [base]:
            if apk.name == "base.apk":
                with open_zip(apk) as z:
                    replace[MANIFEST] = single_apk_manifest(z.read(MANIFEST))
                    replace[METADATA] = patch_metadata(z.read(METADATA), public_key)
                    # LSPatch (sigBypassLevel 2) redirects reads of the installed APK to this embedded original,
                    # so its copy of the metadata needs the same key. Its signature is never re-checked.
                    if ORIGIN in z.namelist():
                        origin_in = work / "origin.apk"
                        origin_out = work / "origin.patched.apk"
                        origin_in.write_bytes(z.read(ORIGIN))
                        with open_zip(origin_in) as zo:
                            metadata = patch_metadata(zo.read(METADATA), public_key)
                        rewrite_apk(origin_in, work / "origin.unaligned.apk", {METADATA: metadata}, keep_signature=True)
                        subprocess.run([tool("zipalign"), "-f", "-p", "4", str(work / "origin.unaligned.apk"), str(origin_out)], check=True)
                        replace[ORIGIN] = origin_out.read_bytes()
                print("global-metadata.dat: clave del gateway sustituida (APK y origin.apk de LSPatch)")
            unsigned = work / "single.unsigned.apk"
            aligned = work / "single.aligned.apk"
            rewrite_apk(apk, unsigned, replace, add=added)
            # -p page-aligns the stored .so files so they can be mapped straight from the APK.
            subprocess.run([tool("zipalign"), "-f", "-p", "4", str(unsigned), str(aligned)], check=True)
            out = args.output / OUTPUT_NAME
            subprocess.run([tool("apksigner"), "sign", "--ks", str(args.keystore), "--ks-key-alias", "schale",
                            "--ks-pass", f"pass:{password}", "--key-pass", f"pass:{password}",
                            "--out", str(out), str(aligned)], check=True)
            subprocess.run([tool("apksigner"), "verify", str(out)], check=True, capture_output=True)
            out.with_suffix(".apk.idsig").unlink(missing_ok=True)
            print(f"{out} firmado: cópialo al móvil e instálalo tocándolo")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
