<div align="center">

<img src="assets/arona.png" alt="Schale" width="160" />

# Schale

**Blue Archive private server for Steam and Android with automatic version detection, SQLite, and admin tools**

[![Release](https://img.shields.io/github/v/release/jhon1466/Schale?style=for-the-badge&color=128afa&label=release)](https://github.com/jhon1466/Schale/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jhon1466/Schale/total?style=for-the-badge&color=52525b)](https://github.com/jhon1466/Schale/releases)
[![Last commit](https://img.shields.io/github/last-commit/jhon1466/Schale?style=for-the-badge&color=a3a3a3)](https://github.com/jhon1466/Schale/commits)

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-server-239120?style=for-the-badge)
![SQLite](https://img.shields.io/badge/SQLite-database-003B57?style=for-the-badge&logo=sqlite&logoColor=white)
![Windows](https://img.shields.io/badge/Steam-Windows-0078D4?style=for-the-badge&logo=steam&logoColor=white)
![Android](https://img.shields.io/badge/Android-client-3DDC84?style=for-the-badge&logo=android&logoColor=white)

<a href="https://github.com/jhon1466/Schale/releases/latest"><img src="https://img.shields.io/badge/Download%20Latest%20Release-128afa?style=for-the-badge&logo=github&logoColor=white" alt="Download Latest Release" /></a>
<a href="https://discord.gg/sfPVdxpv98"><img src="https://img.shields.io/badge/Join%20Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord" /></a>

[Español](README.md) · **English** · [中文](README.zh.md) · [日本語](README.ja.md)

</div>

---

## 💬 Community

<div align="center">

<a href="https://discord.gg/sfPVdxpv98"><img src="https://img.shields.io/badge/Discord-Join%20Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white&labelColor=404EED" alt="Discord" height="40" /></a>

Questions, bugs or ideas? Join the **Discord server**: news are announced there, bugs get reported and you can get help with the setup.

</div>

---

Private server for **Blue Archive** (**Steam** and **Android** clients, currently `1.96.474931`), written in C# on ASP.NET Core (.NET 10) with a local SQLite database.

> Non-profit project made for educational and preservation purposes. Not affiliated with Nexon or Yostar, and **includes no game files**: you need your own installed client.

---

## Main features

| Area | Included |
|---|---|
| Auto-versioning | Detects the latest client build (`BlueArchiveVersionResolver`), downloads `ExcelDB.db`, and handles schema drift using `global-metadata.dat`. |
| Steam client patching | Automatically patches `global-metadata.dat`, `gamescale.core.dll`, inface config, recruitment banners, and event schedules via byte signatures. |
| Android support | APK patching (`Tools/AndroidApkPatcher`), `ngs_x_init_response.bin` support, and full setup notes in [ANDROID.md](ANDROID.md). |
| Gameplay & progression | Gacha (real or custom rates), Campaign (Normal/Hard/Extra/Sweep), Total Assault (Raid), Arena, Cafe, MomoTalk, Crafting, Shops, Missions, Achievements, and Past Events. |
| Administration | **Schale GM Tools** (`Schale.GMTools/`), **Schale-Coordinator** (World Raid), and HTTP `/dev/api/*` endpoints. |

---

## Recommended: prebuilt Release

1. Download `Schale-Server-win-x64.zip` from [Releases](https://github.com/jhon1466/Schale/releases/latest).
2. Extract the archive and launch `SchaleServer.exe` (or use **Schale GM Tools** to install and keep it updated automatically).
3. On startup, the server locates your Steam installation, prepares `ExcelDB.db`, applies the required client patches, and listens on port `5000`.

---

## Building from source

### Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10/11 or Linux (ARM64/x64) |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Required to build and run `Schale-Server` and `Schale-Coordinator` |
| Blue Archive client | Steam installation or Android APK |

### Steps

1. **Clone** the repository:
   ```powershell
   git clone https://github.com/jhon1466/Schale.git
   cd Schale
   ```
2. **Start the main server:**
   ```powershell
   dotnet run --project Schale-Server/Schale-Server.csproj
   ```
3. *(Optional)* **Start the World Raid coordinator:**
   ```powershell
   dotnet run --project Schale-Coordinator/Schale-Coordinator.csproj
   ```

---

## Android support

Tools for patching the Android APK live in [Tools/AndroidApkPatcher](Tools/AndroidApkPatcher) with detailed instructions in [ANDROID.md](ANDROID.md):

- `patch_apk.py`: patches the APK with the server's RSA public key (`Schale-Server/config/GatewayPublicKey.pem`) and target endpoint.
- `patch_profile_tts.py`: Android profile/TTS compatibility fix.
- `build_updated_apk.py` / `upgrade_lobby_apk.py`: repackages and signs updated APKs.

---

## Project layout

| Folder | Purpose |
|---|---|
| `Schale/` | Shared FlatData, Crypto, models, and table definitions. |
| `Schale-Server/` | Main HTTP/Gateway server (.NET 10, SQLite, client patchers, and GM API). |
| `Schale-Server.Tests/` | xUnit test suite. |
| `Schale-Coordinator/` | World Raid coordinator service. |
| `Schale.GMTools/` | Tauri desktop app for server control, GM commands, and auto-updates. |
| `Tools/AndroidApkPatcher/` | Python scripts for patching and signing the Android APK. |
| `Scripts/` | Network helpers, `mitmproxy` redirector, and `nginx` templates. |
