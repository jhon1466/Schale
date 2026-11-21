<div align="center">

<img src="assets/arona.png" alt="Schale" width="160" />

# Schale

**自動バージョン検出・SQLite・管理ツールを備えた Steam / Android 対応 Blue Archive プライベートサーバー**

[![Release](https://img.shields.io/github/v/release/jhon1466/Schale?style=for-the-badge&color=128afa&label=release)](https://github.com/jhon1466/Schale/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jhon1466/Schale/total?style=for-the-badge&color=52525b)](https://github.com/jhon1466/Schale/releases)
[![Last commit](https://img.shields.io/github/last-commit/jhon1466/Schale?style=for-the-badge&color=a3a3a3)](https://github.com/jhon1466/Schale/commits)

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-server-239120?style=for-the-badge)
![SQLite](https://img.shields.io/badge/SQLite-database-003B57?style=for-the-badge&logo=sqlite&logoColor=white)
![Windows](https://img.shields.io/badge/Steam-Windows-0078D4?style=for-the-badge&logo=steam&logoColor=white)
![Android](https://img.shields.io/badge/Android-client-3DDC84?style=for-the-badge&logo=android&logoColor=white)

<a href="https://github.com/jhon1466/Schale/releases/latest"><img src="https://img.shields.io/badge/%E6%9C%80%E6%96%B0%E3%83%AA%E3%83%AA%E3%83%BC%E3%82%B9%E3%82%92%E3%83%80%E3%82%A6%E3%83%B3%E3%83%AD%E3%83%BC%E3%83%89-128afa?style=for-the-badge&logo=github&logoColor=white" alt="最新リリースをダウンロード" /></a>
<a href="https://discord.gg/sfPVdxpv98"><img src="https://img.shields.io/badge/Discord%20%E3%81%AB%E5%8F%82%E5%8A%A0-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord" /></a>

[Español](README.md) · [English](README.en.md) · [中文](README.zh.md) · **日本語**

</div>

---

## 💬 コミュニティ

<div align="center">

<a href="https://discord.gg/sfPVdxpv98"><img src="https://img.shields.io/badge/Discord-Discord%20%E3%81%AB%E5%8F%82%E5%8A%A0-5865F2?style=for-the-badge&logo=discord&logoColor=white&labelColor=404EED" alt="Discord" height="40" /></a>

質問・不具合・アイデアがあれば **Discord サーバー** へどうぞ。最新情報の告知、不具合の報告、導入のサポートを行っています。

</div>

---

C# / ASP.NET Core (.NET 10) とローカル SQLite データベースで構築された **Blue Archive** プライベートサーバーです。**Steam** および **Android** クライアント（現在 `1.96.474931`）に対応しています。

> 教育および保存目的の非営利プロジェクトです。Nexon・Yostar とは一切関係がなく、**ゲーム本体のファイルは含まれません**。ご自身のクライアントをご用意ください。

---

## 主な機能

| 項目 | 内容 |
|---|---|
| 自動バージョン検出 | `BlueArchiveVersionResolver` により最新ビルドと `ExcelDB.db` を自動取得し、`global-metadata.dat` からスキーマ差分を自動補正します。 |
| Steam パッチ適用 | バイトシグネチャを用いて `global-metadata.dat`、`gamescale.core.dll`、inface 設定、募集バナー、イベント日程を自動パッチします。 |
| Android 対応 | APK パッチャー（`Tools/AndroidApkPatcher`）、`ngs_x_init_response.bin` 応答、[ANDROID.md](ANDROID.md) に手順を収録。 |
| ゲームコンテンツ | ガチャ（通常/カスタム確率）、任務（Normal/Hard/Extra/掃討）、総力戦（Raid）、戦術対抗戦（Arena）、カフェ、モモトーク、クラフト、ショップ、ミッション、実績、過去イベント。 |
| 管理ツール | **Schale GM Tools**（`Schale.GMTools/`）、**Schale-Coordinator**（World Raid）、`/dev/api/*` エンドポイント。 |

---

## 推奨方法：ビルド済みリリースの利用

1. [Releases](https://github.com/jhon1466/Schale/releases/latest) から `Schale-Server-win-x64.zip` をダウンロードします。
2. 展開して `SchaleServer.exe` を起動します（**Schale GM Tools** を使うと自動インストール・自動更新も可能です）。
3. 起動時に Steam 版のインストール先を自動検出し、`ExcelDB.db` とクライアントパッチを準備してポート `5000` で待機します。

---

## ソースからビルド

### 必要環境

| 項目 | 詳細 |
|---|---|
| OS | Windows 10/11 または Linux (ARM64/x64) |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | `Schale-Server` と `Schale-Coordinator` のビルド・実行に必要 |
| Blue Archive クライアント | Steam 版または Android APK |

### 手順

1. **リポジトリをクローン：**
   ```powershell
   git clone https://github.com/jhon1466/Schale.git
   cd Schale
   ```
2. **メインサーバーを起動：**
   ```powershell
   dotnet run --project Schale-Server/Schale-Server.csproj
   ```
3. *（任意）* **World Raid コーディネーターを起動：**
   ```powershell
   dotnet run --project Schale-Coordinator/Schale-Coordinator.csproj
   ```

---

## Android 対応

Android APK のパッチスクリプトは [Tools/AndroidApkPatcher](Tools/AndroidApkPatcher) にあり、手順は [ANDROID.md](ANDROID.md) に記載しています：

- `patch_apk.py`：サーバーの RSA 公開鍵（`Schale-Server/config/GatewayPublicKey.pem`）と接続先を APK に適用します。
- `patch_profile_tts.py`：Android のプロフィール / TTS 互換性修正。
- `build_updated_apk.py` / `upgrade_lobby_apk.py`：更新済み APK の再構築と署名。

---

## プロジェクト構成

| フォルダ | 役割 |
|---|---|
| `Schale/` | 共有 FlatData・Crypto・モデル・テーブル定義ライブラリ。 |
| `Schale-Server/` | メイン HTTP / Gateway サーバー（.NET 10、SQLite、パッチ機能、GM API）。 |
| `Schale-Server.Tests/` | xUnit 単体テストスイート。 |
| `Schale-Coordinator/` | World Raid コーディネーター。 |
| `Schale.GMTools/` | Tauri 製デスクトップ管理ツール（サーバー制御・GM コマンド・自動更新）。 |
| `Tools/AndroidApkPatcher/` | Android APK パッチ・署名用 Python スクリプト。 |
| `Scripts/` | ネットワーク補助、`mitmproxy` スクリプト、`nginx` 設定テンプレート。 |
