<div align="center">

<img src="assets/arona.png" alt="Schale" width="160" />

# Schale

**Servidor privado de Blue Archive para Steam y Android con detección automática de versión, SQLite y herramientas de administración**

[![Release](https://img.shields.io/github/v/release/jhon1466/Schale?style=for-the-badge&color=128afa&label=release)](https://github.com/jhon1466/Schale/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jhon1466/Schale/total?style=for-the-badge&color=52525b)](https://github.com/jhon1466/Schale/releases)
[![Last commit](https://img.shields.io/github/last-commit/jhon1466/Schale?style=for-the-badge&color=a3a3a3)](https://github.com/jhon1466/Schale/commits)

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-server-239120?style=for-the-badge)
![SQLite](https://img.shields.io/badge/SQLite-database-003B57?style=for-the-badge&logo=sqlite&logoColor=white)
![Windows](https://img.shields.io/badge/Steam-Windows-0078D4?style=for-the-badge&logo=steam&logoColor=white)
![Android](https://img.shields.io/badge/Android-client-3DDC84?style=for-the-badge&logo=android&logoColor=white)

<a href="https://github.com/jhon1466/Schale/releases/latest"><img src="https://img.shields.io/badge/Descargar%20Última%20Release-128afa?style=for-the-badge&logo=github&logoColor=white" alt="Descargar Última Release" /></a>

**Español** · [English](README.en.md) · [中文](README.zh.md) · [日本語](README.ja.md)

</div>

---

Servidor privado de **Blue Archive** (clientes **Steam** y **Android**, actualmente `1.96.474931`), escrito en C# sobre ASP.NET Core (.NET 10) con base de datos SQLite local.

> Proyecto sin ánimo de lucro con fines educativos y de preservación. No está afiliado a Nexon ni a Yostar, y **no incluye archivos del juego**: necesitas tu propio cliente instalado.

---

## Características principales

| Área | Qué incluye |
|---|---|
| Actualización automática | Detecta la última versión del cliente (`BlueArchiveVersionResolver`), descarga `ExcelDB.db` y maneja *schema drift* leyendo `global-metadata.dat`. |
| Parcheo de cliente (Steam) | Parchea automáticamente `global-metadata.dat`, `gamescale.core.dll`, inface config, banners y calendario de eventos mediante firmas de bytes. |
| Soporte Android | Parcheo de APK (`Tools/AndroidApkPatcher`), bypass de certificados/host, soporte `ngs_x_init_response.bin` y guía completa en [ANDROID.md](ANDROID.md). |
| Progreso y contenido | Gacha (tasas reales o personalizadas), campañas (Normal/Hard/Extra/Sweep), Total Assault (Raid), Arena, Café, MomoTalk, Crafting, Tiendas, Misiones, Logros y Eventos pasados. |
| Administración | **Schale GM Tools** (`Schale.GMTools/`), **Schale-Coordinator** (World Raid) y API HTTP `/dev/api/*`. |

---

## Forma recomendada: usar la Release compilada

1. Descarga `Schale-Server-win-x64.zip` desde [Releases](https://github.com/jhon1466/Schale/releases/latest).
2. Descomprime la carpeta y ejecuta `SchaleServer.exe` (o usa **Schale GM Tools** para instalarlo y mantenerlo actualizado automáticamente).
3. Al iniciar, el servidor localiza tu instalación de Steam, prepara `ExcelDB.db`, aplica los parches necesarios y queda escuchando en el puerto `5000`.

---

## Compilar desde código fuente

### Requisitos

| Requisito | Detalle |
|---|---|
| Sistema | Windows 10/11 o Linux (ARM64/x64) |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Necesario para compilar y ejecutar `Schale-Server` y `Schale-Coordinator` |
| Cliente de Blue Archive | Instalación de Steam o APK de Android |

### Pasos

1. **Clona** el repositorio:
   ```powershell
   git clone https://github.com/jhon1466/Schale.git
   cd Schale
   ```
2. **Inicia el servidor principal:**
   ```powershell
   dotnet run --project Schale-Server/Schale-Server.csproj
   ```
3. *(Opcional)* **Inicia el coordinador de World Raid:**
   ```powershell
   dotnet run --project Schale-Coordinator/Schale-Coordinator.csproj
   ```

---

## Soporte para Android

El repositorio incluye las herramientas para parchar el cliente Android en [Tools/AndroidApkPatcher](Tools/AndroidApkPatcher) y las instrucciones detalladas en [ANDROID.md](ANDROID.md):

- `patch_apk.py`: parchea el APK con la llave RSA del servidor (`Schale-Server/config/GatewayPublicKey.pem`) y apunta al servidor configurado.
- `patch_profile_tts.py`: corrección de compatibilidad para perfil/TTS en Android.
- `build_updated_apk.py` / `upgrade_lobby_apk.py`: reconstrucción y firma de APK actualizado.

---

## Estructura del proyecto

| Carpeta | Contenido |
|---|---|
| `Schale/` | Librería compartida de FlatData, Crypto, modelos y tablas. |
| `Schale-Server/` | Servidor principal HTTP/Gateway (.NET 10, SQLite, parcheadores y API GM). |
| `Schale-Server.Tests/` | Suite de pruebas unitarias xUnit. |
| `Schale-Coordinator/` | Coordinador de World Raid. |
| `Schale.GMTools/` | App de escritorio en Tauri para gestionar el servidor, comandos GM y actualizaciones. |
| `Tools/AndroidApkPatcher/` | Scripts en Python para parchar y firmar el APK de Android. |
| `Scripts/` | Utilidades de red, `mitmproxy` y plantillas de `nginx`. |
