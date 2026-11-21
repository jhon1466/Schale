# Guía de Android para Schale (Blue Archive Global, `com.nexon.bluearchive`)

Esta guía explica cómo parchar tu propio APK oficial de Blue Archive Global para conectarlo a **Schale Server** usando **LSPatch** + [AnimeGamesProxy](https://github.com/Xuoos/AnimeGamesProxy) y los scripts de `Tools/AndroidApkPatcher/`.

---

## Requisitos

| Herramienta | Para qué se usa |
|---|---|
| Python 3.12+ | Ejecutar `patch_apk.py` / `build_updated_apk.py` |
| Android SDK Build-Tools (`zipalign`, `apksigner`) | Alinear y firmar el APK final |
| JDK (`keytool`) | Generar el keystore de firma (`~/.schale-android/release.jks`) la primera vez |
| APK / XAPK oficial de Blue Archive Global | Cliente base obtenido desde tu fuente oficial |
| [LSPatch](https://github.com/LSPosed/LSPatch) + [AnimeGamesProxy](https://github.com/Xuoos/AnimeGamesProxy) | Redirigir las peticiones HTTPS del juego al servidor y omitir SSL pinning |

---

## 1. Configurar y arrancar el servidor en modo Android

En `Config/Config.json` (junto al ejecutable del servidor) o mediante variables de entorno:

```json
"ServerConfiguration": {
  "ClientPlatform": "Android",
  "PublicAddress": "TU_IP_O_DOMINIO"
}
```

O por variables de entorno:

```bash
SCHALE_CLIENT_PLATFORM=Android SCHALE_PUBLIC_ADDRESS=TU_IP_O_DOMINIO dotnet run --project Schale-Server/Schale-Server.csproj
```

Asegúrate de tener abiertos los puertos TCP **5000** y **5100** en el firewall (y si usas HTTPS con proxy inverso como `nginx`, el puerto **443**).

Comprueba que el servidor responde antes de parchar el APK:

```bash
curl http://TU_IP_O_DOMINIO:5000/com.nexon.bluearchive/server_config/474931_Live_x.json
```

---

## 2. Parchar el APK

1. Toma el XAPK o los splits (`base.apk` + `split_config.arm64_v8a.apk`) y pásalos por **LSPatch** (`sigBypassLevel: 2`) incrustando el módulo **AnimeGamesProxy**.
2. Ejecuta el parcheador sobre el ZIP/APK generado por LSPatch:

```bash
python Tools/AndroidApkPatcher/patch_apk.py ruta/al/BlueArchive-lspatch.zip Tools/AndroidApkPatcher/out
```

*(Si partes directamente de un XAPK actualizado y quieres aplicar también el parche de compatibilidad de perfil/TTS `patch_profile_tts.py`, revisa `Tools/AndroidApkPatcher/build_updated_apk.py`.)*

### Qué hace el script automáticamente

1. Reemplaza la clave pública RSA del gateway en `global-metadata.dat` (tanto en `base.apk` como en `assets/lspatch/origin.apk`) usando `Schale-Server/config/GatewayPublicKey.pem`.
   - **Importante:** si regeneras las llaves RSA del servidor (`GatewayPrivateKey.pem` / `GatewayPublicKey.pem`), debes volver a parchar el APK.
2. Fusiona las librerías nativas `lib/arm64-v8a/*.so` dentro del APK base para generar un único archivo instalable.
3. Ajusta `AndroidManifest.xml` (`axml.py`) para quitar la dependencia de splits de Google Play.
4. Ejecuta `zipalign -p 4` y firma el APK (v1+v2+v3) con `~/.schale-android/release.jks`.

El resultado queda en `Tools/AndroidApkPatcher/out/BlueArchive-Schale.apk`.

---

## 3. Instalar y configurar en el teléfono

1. Si tienes instalada otra versión de Blue Archive, **desinstálala primero** (la firma cambia y Android no permite actualizar encima).
2. Copia `BlueArchive-Schale.apk` al teléfono e instálalo.
3. Abre el juego, toca el botón flotante de **AnimeGamesProxy** → **Proxy Server Settings**:
   - **Proxy Address:** `http://TU_IP_O_DOMINIO:5000` (o `https://TU_DOMINIO` si usas TLS en `nginx`).
   - Activa **Proxy Enabled**.
   - Activa **Bypass SSL Certificate Verification**.
   - Activa **Force Trust User Certificates**.
   - Activa **Bypass ACE Detection** (si aparece).
   - **Modo:** `Normal` (o `Strict` añadiendo `*.nexon.com`, `*.nexon.io`, `d2vaidpni345rp.cloudfront.net` para que las descargas pesadas del CDN `ba.dn.nexoncdn.co.kr` vayan directo sin pasar por el servidor).
4. Cierra la ventana de ajustes del módulo y reinicia el juego.

---

## 4. Cuentas y progreso

- Al pulsar **Play with Guest Account** o abrir el portal de cuentas integrado desde el juego, puedes crear o iniciar sesión con tu usuario de Schale para vincular tu dispositivo y conservar tu progreso entre teléfonos.
- Si el juego se queda en *"Unpacking game resources"*, verifica que el servidor esté usando la misma `GatewayPrivateKey.pem` correspondiente a la `GatewayPublicKey.pem` con la que se parchó el APK.
