<div align="center">

<img src="assets/arona.png" alt="Schale" width="160" />

# Schale

**支持 Steam 与 Android 的蔚蓝档案（Blue Archive）私人服务端，具备自动版本检测、SQLite 存储与管理工具**

[![Release](https://img.shields.io/github/v/release/jhon1466/Schale?style=for-the-badge&color=128afa&label=release)](https://github.com/jhon1466/Schale/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jhon1466/Schale/total?style=for-the-badge&color=52525b)](https://github.com/jhon1466/Schale/releases)
[![Last commit](https://img.shields.io/github/last-commit/jhon1466/Schale?style=for-the-badge&color=a3a3a3)](https://github.com/jhon1466/Schale/commits)

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-server-239120?style=for-the-badge)
![SQLite](https://img.shields.io/badge/SQLite-database-003B57?style=for-the-badge&logo=sqlite&logoColor=white)
![Windows](https://img.shields.io/badge/Steam-Windows-0078D4?style=for-the-badge&logo=steam&logoColor=white)
![Android](https://img.shields.io/badge/Android-client-3DDC84?style=for-the-badge&logo=android&logoColor=white)

<a href="https://github.com/jhon1466/Schale/releases/latest"><img src="https://img.shields.io/badge/%E4%B8%8B%E8%BD%BD%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC-128afa?style=for-the-badge&logo=github&logoColor=white" alt="下载最新版本" /></a>

[Español](README.md) · [English](README.en.md) · **中文** · [日本語](README.ja.md)

</div>

---

基于 C# / ASP.NET Core (.NET 10) 与本地 SQLite 数据库编写的 **Blue Archive（蔚蓝档案）** 私人服务端，支持 **Steam** 与 **Android** 客户端（当前版本 `1.96.474931`）。

> 本项目为非营利性质，仅用于学习与技术研究，与 Nexon 或 Yostar 无任何关联，且**不包含任何游戏本体文件**，请使用您自行安装的客户端。

---

## 核心功能

| 模块 | 说明 |
|---|---|
| 自动版本检测 | 通过 `BlueArchiveVersionResolver` 自动获取最新客户端版本与 `ExcelDB.db`，并读取 `global-metadata.dat` 自动处理表结构偏移（Schema Drift）。 |
| Steam 客户端补丁 | 基于字节特征自动修补 `global-metadata.dat`、`gamescale.core.dll`、Inface 配置、招募卡池与活动排期。 |
| Android 支持 | 提供 APK 修补脚本（`Tools/AndroidApkPatcher`）、`ngs_x_init_response.bin` 响应及 [ANDROID.md](ANDROID.md) 完整说明。 |
| 游戏玩法支持 | 招募（真实概率或自定义概率）、主线/困难/扫荡关卡、总力战（Raid）、战术大赛（Arena）、咖啡厅、MomoTalk、制造、商店、任务、成就与往期活动。 |
| 管理工具 | 内置 **Schale GM Tools**（`Schale.GMTools/`）、**Schale-Coordinator**（World Raid）及 `/dev/api/*` 管理接口。 |

---

## 推荐方式：直接下载预编译版本

1. 前往 [Releases](https://github.com/jhon1466/Schale/releases/latest) 下载 `Schale-Server-win-x64.zip`。
2. 解压后运行 `SchaleServer.exe`（或使用 **Schale GM Tools** 自动安装并保持最新）。
3. 服务端启动后会自动定位 Steam 客户端、准备 `ExcelDB.db`、应用客户端补丁并监听 `5000` 端口。

---

## 从源码编译

### 环境要求

| 项目 | 说明 |
|---|---|
| 操作系统 | Windows 10/11 或 Linux (ARM64/x64) |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 用于编译并运行 `Schale-Server` 与 `Schale-Coordinator` |
| Blue Archive 客户端 | 已安装的 Steam 客户端或 Android APK |

### 启动步骤

1. **克隆仓库：**
   ```powershell
   git clone https://github.com/jhon1466/Schale.git
   cd Schale
   ```
2. **启动主服务端：**
   ```powershell
   dotnet run --project Schale-Server/Schale-Server.csproj
   ```
3. *（可选）* **启动 World Raid 协调器：**
   ```powershell
   dotnet run --project Schale-Coordinator/Schale-Coordinator.csproj
   ```

---

## Android 客户端支持

Android APK 修补脚本位于 [Tools/AndroidApkPatcher](Tools/AndroidApkPatcher)，详细步骤请查看 [ANDROID.md](ANDROID.md)：

- `patch_apk.py`：写入服务端 RSA 公钥（`Schale-Server/config/GatewayPublicKey.pem`）与服务器地址。
- `patch_profile_tts.py`：修复 Android 个人资料 / TTS 兼容性问题。
- `build_updated_apk.py` / `upgrade_lobby_apk.py`：重新打包并签名更新后的 APK。

---

## 目录结构

| 目录 | 说明 |
|---|---|
| `Schale/` | 公共 FlatData、Crypto、数据模型与表定义库。 |
| `Schale-Server/` | 主 HTTP / Gateway 服务端（.NET 10、SQLite、客户端补丁与 GM API）。 |
| `Schale-Server.Tests/` | xUnit 单元测试套件。 |
| `Schale-Coordinator/` | World Raid 协调服务。 |
| `Schale.GMTools/` | 基于 Tauri 的桌面管理工具（服务控制、GM 指令与自动更新）。 |
| `Tools/AndroidApkPatcher/` | Android APK 修补与签名脚本。 |
| `Scripts/` | 网络工具、`mitmproxy` 重定向脚本及 `nginx` 配置模板。 |
