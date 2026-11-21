#!/usr/bin/env python3
"""Patch Blue Archive 1.96.474931 XAPK with LSPatch + Schale Server key using release.jks."""

import hashlib
import io
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import patch_apk
import patch_profile_tts
from axml import Manifest

METADATA = "assets/bin/Data/Managed/Metadata/global-metadata.dat"
ORIGIN = "assets/lspatch/origin.apk"
CONFIG_JSON = "assets/lspatch/config.json"
LOADER_DEX = "assets/lspatch/loader.dex"
AGP_MODULE = "assets/lspatch/modules/xuoos.animegamesproxy.apk"
LSPATCH_SO_PREFIX = "assets/lspatch/so/"
MANIFEST = "AndroidManifest.xml"


def replace_axml_string(data: bytes, old_str: str, new_str: str) -> bytes:
    pool = 8
    chunk_type, header_size, old_pool_size = struct.unpack_from("<HHI", data, pool)
    count, style_count, flags, strings_start, styles_start = struct.unpack_from("<IIIII", data, pool + 8)
    assert style_count == 0, "styles not supported"
    assert flags & 0x100 == 0, "only UTF-16 supported"

    offsets = list(struct.unpack_from(f"<{count}I", data, pool + header_size))
    base = pool + strings_start

    strings = []
    for off in offsets:
        p = base + off
        length = struct.unpack_from("<H", data, p)[0]
        s = data[p + 2 : p + 2 + length * 2].decode("utf-16-le")
        strings.append(s)

    if old_str not in strings:
        raise ValueError(f"'{old_str}' not found in string pool")
    strings = [new_str if s == old_str else s for s in strings]

    new_str_bytes = bytearray()
    new_offsets = []
    for s in strings:
        new_offsets.append(len(new_str_bytes))
        new_str_bytes.extend(struct.pack("<H", len(s)))
        new_str_bytes.extend(s.encode("utf-16-le"))
        new_str_bytes.extend(b"\x00\x00")

    while len(new_str_bytes) % 4 != 0:
        new_str_bytes.append(0)

    new_pool_size = header_size + count * 4 + len(new_str_bytes)
    delta = new_pool_size - old_pool_size

    out = bytearray()
    old_file_size = struct.unpack_from("<I", data, 4)[0]
    new_file_size = old_file_size + delta
    out.extend(struct.pack("<HHI", 0x0003, 8, new_file_size))
    out.extend(struct.pack("<HHI", chunk_type, header_size, new_pool_size))
    out.extend(struct.pack("<IIIII", count, 0, 0, header_size + count * 4, 0))
    for off in new_offsets:
        out.extend(struct.pack("<I", off))
    out.extend(new_str_bytes)
    out.extend(data[pool + old_pool_size :])
    return bytes(out)


def patch_xapk(xapk_path: Path, existing_apk: Path, output_apk: Path, public_key_path: Path, keystore_path: Path):
    zipalign = patch_apk.tool("zipalign")
    apksigner = patch_apk.tool("apksigner")
    password = keystore_path.with_suffix(".pass").read_text(encoding="utf-8").strip()
    public_key = public_key_path.read_text(encoding="utf-8").replace("\r\n", "\n").strip("\n")

    work = Path(tempfile.mkdtemp(prefix="ba_patch_"))
    try:
        print("[1/6] Extrayendo componentes de la APK actual y el nuevo XAPK...")
        # 1. LSPatch assets from existing_apk
        lspatch_files: dict[str, bytes] = {}
        lspatch_compress: dict[str, int] = {}
        with zipfile.ZipFile(existing_apk) as z_exist:
            stub_classes_dex = z_exist.read("classes.dex")
            for item in z_exist.infolist():
                if item.filename.startswith("assets/lspatch/") and item.filename != ORIGIN:
                    lspatch_files[item.filename] = z_exist.read(item)
                    lspatch_compress[item.filename] = item.compress_type

        print(f"      Extraídos {len(lspatch_files)} archivos de LSPatch y stub classes.dex ({len(stub_classes_dex)} bytes)")

        # 2. XAPK contents
        with zipfile.ZipFile(xapk_path) as z_xapk:
            base_apk_bytes = z_xapk.read("com.nexon.bluearchive.apk")
            config_arm64_bytes = z_xapk.read("config.arm64_v8a.apk")

        # 3. Native libs from config_arm64_v8a
        native_libs: dict[str, bytes] = {}
        native_compress: dict[str, int] = {}
        with zipfile.ZipFile(io.BytesIO(config_arm64_bytes)) as z_split:
            for item in z_split.infolist():
                if item.filename.startswith("lib/"):
                    native_libs[item.filename] = z_split.read(item)
                    native_compress[item.filename] = item.compress_type
        print(f"      Cargadas {len(native_libs)} librerías nativas ARM64")

        # 4. Patch global-metadata.dat
        print("[2/6] Parcheando clave RSA del gateway en global-metadata.dat...")
        with zipfile.ZipFile(io.BytesIO(base_apk_bytes)) as z_base:
            orig_metadata = z_base.read(METADATA)
            orig_manifest = z_base.read(MANIFEST)
            base_infos = {info.filename: info for info in z_base.infolist()}
            base_data = {info.filename: z_base.read(info) for info in z_base.infolist()}

        patched_metadata = patch_apk.patch_metadata(orig_metadata, public_key)
        print("      Clave pública de Schale inyectada con éxito en metadata")

        # 5. Build origin.apk
        print("[3/6] Construyendo y alineando origin.apk (1.96.474931)...")
        origin_unaligned = work / "origin.unaligned.apk"
        origin_aligned = work / "origin.aligned.apk"
        with zipfile.ZipFile(origin_unaligned, "w") as zo:
            for filename, info in base_infos.items():
                if filename.startswith("META-INF/") and filename.upper().endswith((".SF", ".RSA", ".EC", ".DSA", "MANIFEST.MF")):
                    continue
                to_write = patched_metadata if filename == METADATA else base_data[filename]
                new_info = zipfile.ZipInfo(filename, date_time=(1981, 1, 1, 1, 1, 2))
                new_info.compress_type = zipfile.ZIP_STORED if filename == "resources.arsc" else info.compress_type
                new_info.external_attr = info.external_attr
                zo.writestr(new_info, to_write)

        subprocess.run([zipalign, "-f", "-p", "4", str(origin_unaligned), str(origin_aligned)], check=True)
        lspatch_files[ORIGIN] = origin_aligned.read_bytes()
        lspatch_compress[ORIGIN] = zipfile.ZIP_STORED
        print(f"      origin.apk alineado ({len(lspatch_files[ORIGIN])} bytes)")

        # 6. Build outer manifest
        print("[4/6] Modificando AndroidManifest.xml para LSPatch y APK único...")
        manifest_with_stub = replace_axml_string(
            orig_manifest,
            "androidx.core.app.CoreComponentFactory",
            "org.lsposed.lspatch.metaloader.LSPAppComponentFactoryStub"
        )
        outer_manifest = patch_apk.single_apk_manifest(manifest_with_stub)

        # 7. Assemble outer APK
        print("[5/6] Ensamblando APK final...")
        outer_unaligned = work / "outer.unaligned.apk"
        outer_aligned = work / "outer.aligned.apk"

        with zipfile.ZipFile(outer_unaligned, "w") as z_out:
            # Write native libs (patching ARM64 libil2cpp.so TTS coroutine automatically)
            for name, data in native_libs.items():
                if name == patch_profile_tts.ENTRY:
                    data, tts_rva, tts_ver = patch_profile_tts.patch_il2cpp_bytes(data, orig_metadata)
                    print(f"      TTS profile fix aplicado ({tts_ver}) en {name} RVA 0x{tts_rva:x}")
                zinfo = zipfile.ZipInfo(name, date_time=(1981, 1, 1, 1, 1, 2))
                zinfo.compress_type = native_compress.get(name, zipfile.ZIP_DEFLATED)
                z_out.writestr(zinfo, data)

            # Write LSPatch assets
            for name, data in lspatch_files.items():
                zinfo = zipfile.ZipInfo(name, date_time=(1981, 1, 1, 1, 1, 2))
                zinfo.compress_type = lspatch_compress.get(name, zipfile.ZIP_DEFLATED)
                z_out.writestr(zinfo, data)

            # Write base entries (filtering out inner dex and old signatures)
            for filename, info in base_infos.items():
                if filename.startswith("META-INF/") and filename.upper().endswith((".SF", ".RSA", ".EC", ".DSA", "MANIFEST.MF")):
                    continue
                data = base_data[filename]
                compress = info.compress_type

                if filename.startswith("classes") and filename.endswith(".dex"):
                    if filename != "classes.dex":
                        continue  # classes2..7.dex stay inside origin.apk only
                    data = stub_classes_dex
                    compress = zipfile.ZIP_DEFLATED
                elif filename == MANIFEST:
                    data = outer_manifest
                    compress = zipfile.ZIP_DEFLATED
                elif filename == METADATA:
                    data = patched_metadata
                    compress = zipfile.ZIP_DEFLATED
                elif filename == "resources.arsc":
                    compress = zipfile.ZIP_STORED

                if filename not in z_out.namelist():
                    zinfo = zipfile.ZipInfo(filename, date_time=(1981, 1, 1, 1, 1, 2))
                    zinfo.compress_type = compress
                    zinfo.external_attr = info.external_attr
                    z_out.writestr(zinfo, data)

        # 8. Align and Sign
        print("[6/6] Alineando (zipalign 4) y firmando con release.jks...")
        subprocess.run([zipalign, "-f", "-p", "4", str(outer_unaligned), str(outer_aligned)], check=True)

        output_apk.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run([
            apksigner, "sign",
            "--ks", str(keystore_path),
            "--ks-key-alias", "schale",
            "--ks-pass", f"pass:{password}",
            "--key-pass", f"pass:{password}",
            "--out", str(output_apk),
            str(outer_aligned)
        ], check=True)

        # Verify signature
        verify_res = subprocess.run([apksigner, "verify", "--verbose", str(output_apk)], capture_output=True, text=True, check=True)
        print("      Verificación de firma:")
        for line in verify_res.stdout.splitlines():
            if "Verified using" in line or "Number of signers" in line:
                print(f"        {line.strip()}")

        output_apk.with_suffix(".apk.idsig").unlink(missing_ok=True)
        sha256 = hashlib.sha256(output_apk.read_bytes()).hexdigest()
        size_mb = output_apk.stat().st_size / (1024 * 1024)
        print(f"\n[ÉXITO] APK creado correctamente:")
        print(f"  Ruta:   {output_apk}")
        print(f"  Tamaño: {size_mb:.2f} MB")
        print(f"  SHA256: {sha256}")

    finally:
        shutil.rmtree(work, ignore_errors=True)

if __name__ == "__main__":
    patch_xapk(
        xapk_path=Path(r"E:\Documentos\Schale-Server\Blue+Archive-Free+Fest+Choice_1.96.474931_APKPure.xapk"),
        existing_apk=Path(r"E:\Documentos\Schale-Server\BlueArchive-Schale.apk"),
        output_apk=Path(r"E:\Documentos\Schale-Server\BlueArchive-Schale-1.96.474931.apk"),
        public_key_path=Path(r"E:\Documentos\Schale-Server\Schale-Server\config\GatewayPublicKey.pem"),
        keystore_path=Path(r"C:\Users\jhon\.schale-android\release.jks"),
    )

