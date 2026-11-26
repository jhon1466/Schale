//! Server manager: keeps a git checkout of the Schale repository, detects new commits on the
//! tracked branch, compiles SchaleServer with `dotnet publish` and runs / stops the process.

use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::fs;
use std::io::{BufRead, BufReader};
use std::net::{SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter, Manager as _, State};

#[cfg(windows)]
use std::os::windows::process::CommandExt;
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

const SERVER_EXE: &str = "SchaleServer.exe";
const COMMIT_MARKER: &str = ".schale-commit";
const SERVER_PROJECT: &str = "Schale-Server/Schale-Server.csproj";
/// Files that belong to the user's install and must survive a rebuild.
const PRESERVE: &[&str] = &[
    "appsettings.json",
    "schale.sqlite3",
    "BlueArchive.db",
    "Config/GatewayPrivateKey.pem",
    "Config/GatewayPublicKey.pem",
    "config/GatewayPrivateKey.pem",
    "config/GatewayPublicKey.pem",
];

// ───────────────────────── settings ─────────────────────────

#[derive(Serialize, Deserialize, Clone)]
#[serde(rename_all = "camelCase", default)]
pub struct AppSettings {
    pub api_url: String,
    pub repo_url: String,
    pub branch: String,
    /// Empty = managed clone in %LOCALAPPDATA%\Schale\source.
    pub source_dir: String,
    pub auto_update: bool,
    pub poll_minutes: u64,
    pub restart_after_update: bool,
    pub game_dir: String,
    pub offline_mode: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            api_url: "http://127.0.0.1:5000".into(),
            repo_url: "https://github.com/jhon1466/Schale".into(),
            branch: "main".into(),
            source_dir: String::new(),
            auto_update: true,
            poll_minutes: 5,
            restart_after_update: true,
            game_dir: r"C:\Program Files (x86)\Steam\steamapps\common\BlueArchive".into(),
            offline_mode: false,
        }
    }
}

fn base_dir() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(|p| PathBuf::from(p).join("Schale"))
        .unwrap_or_else(|| PathBuf::from("."))
}

fn settings_path() -> PathBuf {
    base_dir().join("gm-tools-settings.json")
}

pub fn load_settings() -> AppSettings {
    fs::read_to_string(settings_path())
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

fn server_dir() -> PathBuf {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            if dir.join(SERVER_EXE).is_file() {
                return dir.to_path_buf();
            }
        }
    }
    base_dir().join("server")
}

fn managed_source(s: &AppSettings) -> bool {
    s.source_dir.trim().is_empty()
}

fn source_dir(s: &AppSettings) -> PathBuf {
    if managed_source(s) {
        base_dir().join("source")
    } else {
        PathBuf::from(s.source_dir.trim())
    }
}

// ───────────────────────── state ─────────────────────────

#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
pub struct CommitInfo {
    pub sha: String,
    pub message: String,
    pub author: String,
    pub date: String,
}

#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    pub checked_at: u64,
    pub remote_sha: Option<String>,
    pub update_available: bool,
    pub commits: Vec<CommitInfo>,
    pub error: Option<String>,
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Progress {
    pub stage: String,
    pub pct: u8,
    pub message: String,
}

pub struct Manager {
    child: Mutex<Option<Child>>,
    proxy_child: Mutex<Option<Child>>,
    logs: Arc<Mutex<VecDeque<String>>>,
    busy: AtomicBool,
    started_at: Mutex<Option<Instant>>,
    last_update: Mutex<UpdateInfo>,
}

impl Default for Manager {
    fn default() -> Self {
        Self {
            child: Mutex::new(None),
            proxy_child: Mutex::new(None),
            logs: Arc::new(Mutex::new(VecDeque::with_capacity(800))),
            busy: AtomicBool::new(false),
            started_at: Mutex::new(None),
            last_update: Mutex::new(UpdateInfo::default()),
        }
    }
}

fn push_log(logs: &Arc<Mutex<VecDeque<String>>>, line: String) {
    if let Ok(mut q) = logs.lock() {
        if q.len() >= 800 {
            q.pop_front();
        }
        q.push_back(line);
    }
}

fn hidden(cmd: &mut Command) -> &mut Command {
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd
}

fn port_open(port: u16) -> bool {
    let addr: SocketAddr = ([127, 0, 0, 1], port).into();
    TcpStream::connect_timeout(&addr, Duration::from_millis(300)).is_ok()
}

fn api_port(s: &AppSettings) -> u16 {
    s.api_url
        .rsplit(':')
        .next()
        .and_then(|p| p.trim_end_matches('/').parse().ok())
        .unwrap_or(5000)
}

fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

fn progress(app: &AppHandle, stage: &str, pct: u8, message: impl Into<String>) {
    let message = message.into();
    if let Some(m) = app.try_state::<Manager>() {
        push_log(&m.logs, format!("[gm-tools] {message}"));
    }
    let _ = app.emit("manager-progress", Progress { stage: stage.into(), pct, message });
}

// ───────────────────────── git / github ─────────────────────────

fn git(dir: Option<&Path>, args: &[&str]) -> Result<String, String> {
    let mut cmd = Command::new("git");
    if let Some(d) = dir {
        cmd.current_dir(d);
    }
    cmd.args(args).env("GIT_TERMINAL_PROMPT", "0");
    let out = hidden(&mut cmd)
        .output()
        .map_err(|e| format!("No se pudo ejecutar git (¿está instalado?): {e}"))?;
    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr).trim().to_string();
        return Err(if err.is_empty() { format!("git {} falló", args.join(" ")) } else { err });
    }
    Ok(String::from_utf8_lossy(&out.stdout).trim().to_string())
}

fn local_commit() -> String {
    fs::read_to_string(server_dir().join(COMMIT_MARKER))
        .unwrap_or_default()
        .trim()
        .to_string()
}

fn remote_head(s: &AppSettings) -> Result<String, String> {
    let out = git(None, &["ls-remote", s.repo_url.trim(), &format!("refs/heads/{}", s.branch.trim())])?;
    let sha = out.split_whitespace().next().unwrap_or("").to_string();
    if sha.len() != 40 {
        return Err(format!(
            "La rama '{}' no existe todavía en {} (¿repositorio vacío?).",
            s.branch, s.repo_url
        ));
    }
    Ok(sha)
}

fn github_slug(url: &str) -> Option<String> {
    let rest = url.trim().trim_end_matches('/').trim_end_matches(".git");
    let rest = rest.split("github.com").nth(1)?.trim_start_matches([':', '/']);
    let mut parts = rest.split('/');
    Some(format!("{}/{}", parts.next()?, parts.next()?))
}

/// Recent commits from the GitHub API (best effort, purely informative).
fn github_commits(s: &AppSettings) -> Vec<CommitInfo> {
    let Some(slug) = github_slug(&s.repo_url) else { return vec![] };
    let url = format!("https://api.github.com/repos/{slug}/commits?sha={}&per_page=15", s.branch.trim());
    let resp = ureq::get(&url)
        .set("User-Agent", "Schale-GM-Tools")
        .set("Accept", "application/vnd.github+json")
        .timeout(Duration::from_secs(12))
        .call();
    let Ok(resp) = resp else { return vec![] };
    let Ok(json) = resp.into_json::<serde_json::Value>() else { return vec![] };
    json.as_array()
        .map(|arr| {
            arr.iter()
                .map(|c| CommitInfo {
                    sha: c["sha"].as_str().unwrap_or("").into(),
                    message: c["commit"]["message"].as_str().unwrap_or("").lines().next().unwrap_or("").into(),
                    author: c["commit"]["author"]["name"].as_str().unwrap_or("").into(),
                    date: c["commit"]["author"]["date"].as_str().unwrap_or("").into(),
                })
                .collect()
        })
        .unwrap_or_default()
}

fn check_update_internal(s: &AppSettings) -> UpdateInfo {
    match remote_head(s) {
        Ok(sha) => {
            let installed = server_dir().join(SERVER_EXE).is_file();
            UpdateInfo {
                checked_at: now_secs(),
                update_available: !installed || local_commit() != sha,
                remote_sha: Some(sha),
                commits: github_commits(s),
                error: None,
            }
        }
        Err(e) => UpdateInfo { checked_at: now_secs(), error: Some(e), ..Default::default() },
    }
}

// ───────────────────────── build ─────────────────────────

fn run_streamed(app: &AppHandle, mut cmd: Command, stage: &str, pct: u8) -> Result<(), String> {
    cmd.stdout(Stdio::piped()).stderr(Stdio::piped());
    hidden(&mut cmd);
    let mut child = cmd.spawn().map_err(|e| format!("No se pudo iniciar el proceso: {e}"))?;
    let logs = app.state::<Manager>().logs.clone();
    let mut handles = vec![];
    for stream in [child.stdout.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>),
                   child.stderr.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>)].into_iter().flatten() {
        let logs = logs.clone();
        let app = app.clone();
        let stage = stage.to_string();
        handles.push(std::thread::spawn(move || {
            for line in BufReader::new(stream).lines().map_while(Result::ok) {
                let line = line.trim_end().to_string();
                if line.is_empty() { continue; }
                push_log(&logs, line.clone());
                let _ = app.emit("server-log", line.clone());
                let _ = app.emit("manager-progress", Progress { stage: stage.clone(), pct, message: line });
            }
        }));
    }
    let status = child.wait().map_err(|e| e.to_string())?;
    for h in handles { let _ = h.join(); }
    if status.success() { Ok(()) } else { Err(format!("El proceso terminó con código {:?}", status.code())) }
}

fn sync_source(app: &AppHandle, s: &AppSettings) -> Result<String, String> {
    let dir = source_dir(s);
    let branch = s.branch.trim();
    if !dir.join(".git").exists() {
        if !managed_source(s) {
            return Err(format!("{} no es un repositorio git.", dir.display()));
        }
        progress(app, "sync", 8, format!("Clonando {} …", s.repo_url));
        fs::create_dir_all(dir.parent().unwrap_or(&dir)).map_err(|e| e.to_string())?;
        let mut cmd = Command::new("git");
        cmd.args(["clone", "--depth", "50", "--branch", branch, s.repo_url.trim()])
            .arg(&dir)
            .env("GIT_TERMINAL_PROMPT", "0");
        run_streamed(app, cmd, "sync", 12)?;
    } else {
        progress(app, "sync", 8, format!("Descargando cambios de {branch} …"));
        git(Some(&dir), &["fetch", "--depth", "50", "origin", branch])?;
        if managed_source(s) {
            git(Some(&dir), &["checkout", "-B", branch, &format!("origin/{branch}")])?;
            git(Some(&dir), &["reset", "--hard", &format!("origin/{branch}")])?;
        } else {
            // User's own working copy: never discard their work, only fast-forward.
            git(Some(&dir), &["merge", "--ff-only", &format!("origin/{branch}")])
                .map_err(|e| format!("No se pudo avanzar tu copia local (¿cambios sin subir?): {e}"))?;
        }
    }
    git(Some(&dir), &["rev-parse", "HEAD"])
}

fn backup_user_files(dest: &Path, bak: &Path) {
    for rel in PRESERVE {
        let p = dest.join(rel);
        if p.is_file() {
            let b = bak.join(rel);
            if let Some(parent) = b.parent() { let _ = fs::create_dir_all(parent); }
            let _ = fs::copy(&p, &b);
        }
    }
}

fn restore_user_files(dest: &Path, bak: &Path) {
    for rel in PRESERVE {
        let b = bak.join(rel);
        if b.is_file() {
            let p = dest.join(rel);
            if let Some(parent) = p.parent() { let _ = fs::create_dir_all(parent); }
            let _ = fs::copy(&b, &p);
        }
    }
    let _ = fs::remove_dir_all(bak);
}

fn update_internal(app: &AppHandle) -> Result<String, String> {
    let state = app.state::<Manager>();
    if state.busy.swap(true, Ordering::SeqCst) {
        return Err("Ya hay una actualización en progreso.".into());
    }
    let s = load_settings();
    let result = (|| {
        progress(app, "sync", 3, "Preparando actualización…");
        let sha = sync_source(app, &s)?;
        let src = source_dir(&s);
        let project = src.join(SERVER_PROJECT);
        if !project.is_file() {
            return Err(format!("No se encontró {} en el código fuente.", SERVER_PROJECT));
        }

        let dest = server_dir();
        fs::create_dir_all(&dest).map_err(|e| e.to_string())?;
        let was_running = server_running(&state, &s);
        progress(app, "build", 30, format!("Compilando commit {} …", &sha[..7]));
        let staging = base_dir().join("build-staging");
        let _ = fs::remove_dir_all(&staging);
        let mut cmd = Command::new("dotnet");
        cmd.current_dir(&src)
            .args(["publish", SERVER_PROJECT, "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-nologo", "-o"])
            .arg(&staging)
            .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
            .env("DOTNET_NOLOGO", "1");
        run_streamed(app, cmd, "build", 55)
            .map_err(|e| format!("La compilación falló: {e}. Revisa el registro."))?;

        progress(app, "install", 85, "Instalando la nueva versión…");
        if was_running { stop_internal(&state); std::thread::sleep(Duration::from_millis(700)); }
        let bak = base_dir().join("preserve-bak");
        backup_user_files(&dest, &bak);
        copy_dir(&staging, &dest).map_err(|e| format!("No se pudo copiar la compilación: {e}"))?;
        restore_user_files(&dest, &bak);
        let _ = fs::remove_dir_all(&staging);
        fs::write(dest.join(COMMIT_MARKER), &sha).map_err(|e| e.to_string())?;

        if let Ok(mut u) = state.last_update.lock() {
            u.update_available = false;
        }
        if was_running && s.restart_after_update {
            progress(app, "install", 95, "Reiniciando servidor…");
            start_internal(app)?;
        }
        Ok(format!("Servidor actualizado al commit {}", &sha[..7]))
    })();
    state.busy.store(false, Ordering::SeqCst);
    match &result {
        Ok(m) => progress(app, "done", 100, m.clone()),
        Err(e) => progress(app, "error", 100, e.clone()),
    }
    result
}

fn copy_dir(from: &Path, to: &Path) -> std::io::Result<()> {
    fs::create_dir_all(to)?;
    for entry in fs::read_dir(from)? {
        let entry = entry?;
        let target = to.join(entry.file_name());
        if entry.file_type()?.is_dir() {
            copy_dir(&entry.path(), &target)?;
        } else {
            fs::copy(entry.path(), target)?;
        }
    }
    Ok(())
}

// ───────────────────────── process ─────────────────────────

fn server_running(state: &Manager, s: &AppSettings) -> bool {
    let mut alive = false;
    if let Ok(mut g) = state.child.lock() {
        if let Some(c) = g.as_mut() {
            match c.try_wait() {
                Ok(None) => alive = true,
                _ => *g = None,
            }
        }
    }
    alive || port_open(api_port(s))
}

const HOSTS_MARKER: &str = "# schale-gm-tools";
const REDIRECT_HOSTS: &[&str] = &[
    "d2vaidpni345rp.cloudfront.net",
    "nxm-eu-bagl.nexon.com",
    "nxm-ios-bagl.nexon.com",
    "nxm-kr-bagl.nexon.com",
    "nxm-tw-bagl.nexon.com",
    "nxm-th-bagl.nexon.com",
    "nxm-or-bagl.nexon.com",
    "public.api.nexon.com",
    "api-pub.nexon.com",
    "sandbox.api.nexon.com",
    "store.steampowered.com",
    "signin.nexon.com",
    "pre-signin.nexon.com",
    "test-signin.nexon.com",
    "dev-signin.nexon.com",
    "psm-log.ngs.nexon.com",
    "config.na.nexon.com",
    "gtable.inface.nexon.com",
    "test-gtable.inface.nexon.com",
    "dev-gtable.inface.nexon.com",
    "platform.gamescale.nexon.com",
    "sdk-push.mp.nexon.com",
    "toy.log.nexon.io",
    "x-init.ngs.nexon.com",
    "x-phaethon.ngs.nexon.com",
    "x-csauth.ngs.nexon.com",
    "bolo7yechd.execute-api.ap-northeast-1.amazonaws.com",
];

fn find_mitmdump() -> Option<PathBuf> {
    if has_tool("mitmdump", "--version") {
        return Some(PathBuf::from("mitmdump"));
    }
    #[cfg(windows)]
    {
        let fallback = PathBuf::from(r"C:\Program Files\mitmproxy\bin\mitmdump.exe");
        if fallback.is_file() {
            return Some(fallback);
        }
    }
    None
}

fn find_redirect_script(s: &AppSettings) -> Option<PathBuf> {
    let rel = Path::new("Scripts")
        .join("redirect_server_mitmproxy")
        .join("redirect_server.py");
    let mut candidates = vec![source_dir(s).join(&rel), server_dir().join(&rel)];
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join(&rel));
            if let Some(parent) = dir.parent() {
                candidates.push(parent.join(&rel));
            }
        }
    }
    candidates.into_iter().find(|p| p.is_file())
}

#[cfg(windows)]
fn hosts_path() -> PathBuf {
    let root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into());
    PathBuf::from(root).join("System32").join("drivers").join("etc").join("hosts")
}

#[cfg(windows)]
fn set_hosts_redirect(enable: bool) -> Result<(), String> {
    let path = hosts_path();
    let raw = fs::read_to_string(&path).map_err(|e| format!("hosts read: {e}"))?;
    let mut lines: Vec<String> = raw
        .lines()
        .filter(|l| !l.contains(HOSTS_MARKER))
        .map(|l| l.to_string())
        .collect();
    if enable {
        for host in REDIRECT_HOSTS {
            lines.push(format!("127.0.0.2 {host} {HOSTS_MARKER}"));
        }
    }
    let mut out = lines.join("\r\n");
    if !out.ends_with('\n') {
        out.push_str("\r\n");
    }
    fs::write(&path, out).map_err(|e| format!("hosts write: {e}"))?;
    let _ = hidden(Command::new("ipconfig.exe").arg("/flushdns")).output();
    Ok(())
}

#[cfg(not(windows))]
fn set_hosts_redirect(_enable: bool) -> Result<(), String> {
    Ok(())
}

#[cfg(windows)]
fn reset_old_system_proxy() {
    let ps_cmd =
        "Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings' -Name ProxyEnable -Value 0; \
         Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class W { [DllImport(\"wininet.dll\", SetLastError=true)] public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l); }'; \
         [W]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0) | Out-Null; \
         [W]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0) | Out-Null";
    let _ = hidden(Command::new("powershell.exe").args(["-NoProfile", "-Command", ps_cmd])).output();
    let _ = hidden(Command::new("netsh.exe").args(["winhttp", "reset", "proxy"])).output();
}

#[cfg(not(windows))]
fn reset_old_system_proxy() {}

fn stop_proxy(state: &Manager) {
    if let Ok(mut g) = state.proxy_child.lock() {
        if let Some(mut c) = g.take() {
            let _ = c.kill();
            let _ = c.wait();
        }
    }
    #[cfg(windows)]
    {
        let _ = hidden(Command::new("taskkill.exe").args(["/IM", "mitmdump.exe", "/F"])).output();
    }
    let _ = set_hosts_redirect(false);
    reset_old_system_proxy();
}

fn start_proxy_if_available(state: &Manager, s: &AppSettings) {
    reset_old_system_proxy();
    let Some(mitmdump) = find_mitmdump() else {
        push_log(&state.logs, "[gm-tools] mitmdump no encontrado; proxy automático omitido.".into());
        return;
    };
    let Some(script) = find_redirect_script(s) else {
        push_log(&state.logs, "[gm-tools] redirect_server.py no encontrado; proxy automático omitido.".into());
        return;
    };

    #[cfg(windows)]
    {
        let _ = hidden(Command::new("taskkill.exe").args(["/IM", "mitmdump.exe", "/F"])).output();
    }

    let mut cmd = Command::new(&mitmdump);
    let script_dir = script.parent().map(Path::to_path_buf).unwrap_or_default();
    cmd.current_dir(&script_dir)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if s.offline_mode {
        if let Err(e) = set_hosts_redirect(true) {
            push_log(&state.logs, format!("[gm-tools] No se pudo escribir hosts para modo offline (ejecuta GM Tools como Administrador): {e}"));
        }
        cmd.env("SCHALE_OFFLINE_MODE", "1").args([
            "--mode",
            "reverse:http://127.0.0.3:5000@127.0.0.2:443",
            "--mode",
            "reverse:http://127.0.0.3:5000@127.0.0.2:80",
            "--mode",
            "reverse:http://127.0.0.3:5000@127.0.0.2:5000",
            "--mode",
            "reverse:http://127.0.0.3:5100@127.0.0.2:5100",
            "--set",
            "keep_host_header=true",
            "--set",
            "connection_strategy=lazy",
            "--no-http2",
            "--set",
            "termlog_verbosity=warn",
            "-s",
        ]);
    } else {
        // Intercept only BlueArchive.exe's traffic via WinDivert (no hosts file or admin required).
        cmd.env_remove("SCHALE_OFFLINE_MODE").args([
            "--mode",
            "local:BlueArchive.exe",
            "--no-http2",
            "--set",
            "termlog_verbosity=warn",
            "-s",
        ]);
    }
    cmd.arg(&script);
    hidden(&mut cmd);
    match cmd.spawn() {
        Ok(mut child) => {
            let log_path = base_dir().join("proxy.log");
            let _ = fs::write(&log_path, "");
            for (stream, prefix) in [
                (child.stdout.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>), "[proxy] "),
                (child.stderr.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>), "[proxy ERR] "),
            ] {
                if let Some(stream) = stream {
                    let logs = state.logs.clone();
                    let lp = log_path.clone();
                    std::thread::spawn(move || {
                        use std::io::Write;
                        for line in BufReader::new(stream).lines().map_while(Result::ok) {
                            let formatted = format!("{prefix}{line}");
                            if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(&lp) {
                                let _ = writeln!(f, "{formatted}");
                            }
                            if line.contains("TLS") || line.contains("error") || line.contains("failed") || line.contains("502") {
                                push_log(&logs, formatted);
                            }
                        }
                    });
                }
            }
            if let Ok(mut g) = state.proxy_child.lock() {
                *g = Some(child);
            }
            if !s.offline_mode {
                let _ = set_hosts_redirect(false);
                push_log(&state.logs, "[gm-tools] mitmproxy iniciado en modo local:BlueArchive.exe.".into());
            } else {
                push_log(&state.logs, "[gm-tools] mitmproxy iniciado en modo FULL OFFLINE (127.0.0.2:443 y :80).".into());
            }
        }
        Err(e) => {
            push_log(&state.logs, format!("[gm-tools] No se pudo iniciar mitmdump: {e}"));
        }
    }
}

fn ensure_grap64_stub(s: &AppSettings, dir: &Path) {
    let dst = dir.join("grap64.dll");
    if dst.is_file() {
        return;
    }
    let rel = Path::new("Scripts").join("grap64.dll");
    let mut candidates = vec![source_dir(s).join(&rel)];
    if let Ok(exe) = std::env::current_exe() {
        if let Some(p) = exe.parent() {
            candidates.push(p.join(&rel));
            if let Some(pp) = p.parent() {
                candidates.push(pp.join(&rel));
            }
        }
    }
    if let Some(src) = candidates.into_iter().find(|p| p.is_file()) {
        let _ = fs::copy(src, dst);
    }
}

pub fn cleanup_on_exit(app: &AppHandle) {
    if let Some(state) = app.try_state::<Manager>() {
        stop_internal(&state);
    }
}

fn stop_internal(state: &Manager) {
    stop_proxy(state);
    if let Ok(mut g) = state.child.lock() {
        if let Some(mut c) = g.take() {
            let _ = c.kill();
            let _ = c.wait();
        }
    }
    #[cfg(windows)]
    {
        let _ = hidden(Command::new("taskkill.exe").args(["/IM", SERVER_EXE, "/F"])).output();
    }
    if let Ok(mut t) = state.started_at.lock() { *t = None; }
}

fn start_internal(app: &AppHandle) -> Result<String, String> {
    let state = app.state::<Manager>();
    let s = load_settings();
    let dir = server_dir();
    let exe = dir.join(SERVER_EXE);
    if !exe.is_file() {
        return Err("El servidor no está instalado. Pulsa «Actualizar y compilar» primero.".into());
    }
    if server_running(&state, &s) {
        ensure_grap64_stub(&s, &dir);
    start_proxy_if_available(&state, &s);
        return Ok("El servidor ya está en ejecución.".into());
    }
    ensure_grap64_stub(&s, &dir);
    start_proxy_if_available(&state, &s);
    let mut cmd = Command::new(&exe);
    cmd.current_dir(&dir)
        .env("NO_PROXY", "*")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let game_dir = s.game_dir.trim();
    if !game_dir.is_empty() && Path::new(game_dir).is_dir() {
        cmd.env("SCHALE_CLIENT_INSTALL_DIR", game_dir);
    }
    if s.offline_mode {
        cmd.env("SCHALE_OFFLINE_MODE", "1")
            .env("SCHALE_AUTO_PATCH_STEAM_OFFLINE", "true");
    } else {
        cmd.env_remove("SCHALE_OFFLINE_MODE")
            .env_remove("SCHALE_AUTO_PATCH_STEAM_OFFLINE");
    }
    hidden(&mut cmd);
    let mut child = cmd.spawn().map_err(|e| format!("No se pudo iniciar {SERVER_EXE}: {e}"))?;
    for (stream, prefix) in [
        (child.stdout.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>), ""),
        (child.stderr.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>), "[ERR] "),
    ] {
        if let Some(stream) = stream {
            let logs = state.logs.clone();
            let app = app.clone();
            std::thread::spawn(move || {
                for line in BufReader::new(stream).lines().map_while(Result::ok) {
                    let line = format!("{prefix}{line}");
                    push_log(&logs, line.clone());
                    let _ = app.emit("server-log", line);
                }
            });
        }
    }
    *state.child.lock().map_err(|_| "lock")? = Some(child);
    *state.started_at.lock().map_err(|_| "lock")? = Some(Instant::now());
    push_log(&state.logs, "[gm-tools] Servidor iniciado.".into());
    Ok("Servidor iniciado.".into())
}

// ───────────────────────── watcher ─────────────────────────

/// Polls the repository in the background and installs new commits when auto-update is on.
pub fn spawn_watcher(app: AppHandle) {
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_secs(4));
        loop {
            let s = load_settings();
            let info = check_update_internal(&s);
            if let Some(m) = app.try_state::<Manager>() {
                if let Ok(mut u) = m.last_update.lock() { *u = info.clone(); }
            }
            let _ = app.emit("update-info", info.clone());
            if s.auto_update && info.update_available && info.error.is_none() {
                let short = info.remote_sha.as_deref().map(|x| &x[..7]).unwrap_or("?").to_string();
                let _ = app.emit("auto-update", format!("Nuevo commit {short}: actualizando…"));
                let _ = update_internal(&app);
            }
            std::thread::sleep(Duration::from_secs(s.poll_minutes.clamp(1, 240) * 60));
        }
    });
}

// ───────────────────────── commands ─────────────────────────

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusInfo {
    pub app_version: String,
    pub installed: bool,
    pub running: bool,
    pub busy: bool,
    pub uptime_seconds: Option<u64>,
    pub server_dir: String,
    pub source_dir: String,
    pub local_commit: String,
    pub game_found: bool,
    pub tools: Tools,
    pub settings: AppSettings,
    pub update: UpdateInfo,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Tools {
    pub git: bool,
    pub dotnet: bool,
}

fn has_tool(name: &str, arg: &str) -> bool {
    hidden(Command::new(name).arg(arg)).output().map(|o| o.status.success()).unwrap_or(false)
}

#[tauri::command]
pub fn manager_status(app: AppHandle, state: State<Manager>) -> StatusInfo {
    let s = load_settings();
    let running = server_running(&state, &s);
    StatusInfo {
        app_version: app.package_info().version.to_string(),
        installed: server_dir().join(SERVER_EXE).is_file(),
        running,
        busy: state.busy.load(Ordering::SeqCst),
        uptime_seconds: state.started_at.lock().ok().and_then(|t| t.map(|i| i.elapsed().as_secs())),
        server_dir: server_dir().to_string_lossy().into(),
        source_dir: source_dir(&s).to_string_lossy().into(),
        local_commit: local_commit(),
        game_found: Path::new(&s.game_dir).join("BlueArchive.exe").is_file(),
        tools: Tools { git: has_tool("git", "--version"), dotnet: has_tool("dotnet", "--version") },
        update: state.last_update.lock().map(|u| u.clone()).unwrap_or_default(),
        settings: s,
    }
}

#[tauri::command]
pub async fn manager_check(app: AppHandle) -> UpdateInfo {
    tauri::async_runtime::spawn_blocking(move || {
        let info = check_update_internal(&load_settings());
        if let Ok(mut u) = app.state::<Manager>().last_update.lock() { *u = info.clone(); }
        let _ = app.emit("update-info", info.clone());
        info
    })
    .await
    .unwrap_or_default()
}

#[tauri::command]
pub async fn manager_update(app: AppHandle) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || update_internal(&app))
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub fn manager_start(app: AppHandle) -> Result<String, String> {
    start_internal(&app)
}

#[tauri::command]
pub fn manager_stop(state: State<Manager>) -> String {
    stop_internal(&state);
    push_log(&state.logs, "[gm-tools] Servidor detenido.".into());
    "Servidor detenido.".into()
}

#[tauri::command]
pub fn manager_restart(app: AppHandle) -> Result<String, String> {
    stop_internal(&app.state::<Manager>());
    std::thread::sleep(Duration::from_millis(700));
    start_internal(&app)
}

#[tauri::command]
pub fn manager_logs(state: State<Manager>) -> Vec<String> {
    state.logs.lock().map(|q| q.iter().cloned().collect()).unwrap_or_default()
}

#[tauri::command]
pub fn manager_clear_logs(state: State<Manager>) {
    if let Ok(mut q) = state.logs.lock() { q.clear(); }
}

#[tauri::command]
pub fn manager_save_settings(settings: AppSettings) -> Result<(), String> {
    let p = settings_path();
    if let Some(parent) = p.parent() { fs::create_dir_all(parent).map_err(|e| e.to_string())?; }
    fs::write(p, serde_json::to_string_pretty(&settings).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn manager_pick_folder(initial: Option<String>) -> Result<Option<String>, String> {
    #[cfg(windows)]
    {
        return tauri::async_runtime::spawn_blocking(move || {
            let init = initial.unwrap_or_default().replace('\'', "''");
            let script = format!(
                "Add-Type -AssemblyName System.Windows.Forms; \
                 $d = New-Object System.Windows.Forms.FolderBrowserDialog; \
                 $d.ShowNewFolderButton = $false; \
                 $init = '{init}'; \
                 if ($init -and (Test-Path -LiteralPath $init)) {{ $d.SelectedPath = $init }}; \
                 if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{ [Console]::Out.Write($d.SelectedPath) }}"
            );
            let mut cmd = Command::new("powershell.exe");
            cmd.args(["-NoProfile", "-STA", "-Command", &script]);
            hidden(&mut cmd);
            let out = cmd.output().map_err(|e| e.to_string())?;
            let path = String::from_utf8_lossy(&out.stdout).trim().to_string();
            Ok(if path.is_empty() { None } else { Some(path) })
        })
        .await
        .map_err(|e| e.to_string())?;
    }
    #[cfg(not(windows))]
    {
        let _ = initial;
        Ok(None)
    }
}

#[tauri::command]
pub fn manager_open(which: String) -> Result<(), String> {
    let s = load_settings();
    let dir = match which.as_str() {
        "source" => source_dir(&s),
        "game" => PathBuf::from(&s.game_dir),
        _ => server_dir(),
    };
    let _ = fs::create_dir_all(&dir);
    Command::new("explorer.exe").arg(&dir).spawn().map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
pub fn manager_play() -> Result<(), String> {
    let s = load_settings();
    let exe = Path::new(&s.game_dir).join("BlueArchive.exe");
    if !exe.is_file() {
        return Err("No se encontró BlueArchive.exe en la carpeta configurada.".into());
    }
    #[cfg(windows)]
    {
        if let Ok(out) = hidden(Command::new("tasklist.exe").args(["/FI", "IMAGENAME eq BlueArchive.exe", "/NH"])).output() {
            let txt = String::from_utf8_lossy(&out.stdout);
            if txt.to_ascii_lowercase().contains("bluearchive.exe") {
                return Err("Blue Archive ya está en ejecución.".into());
            }
        }
        // Launch via Steam URI so SteamAPI_RestartAppIfNecessary doesn't spawn a second instance in Offline Mode.
        if hidden(Command::new("cmd.exe").args(["/C", "start", "", "steam://rungameid/3557620"])).spawn().is_ok() {
            return Ok(());
        }
    }
    Command::new(&exe).current_dir(&s.game_dir).spawn().map_err(|e| e.to_string())?;
    Ok(())
}

/// Proxy to the server's HTTP API (avoids CORS in the webview).
#[tauri::command]
pub async fn api_request(method: String, path: String, body: Option<serde_json::Value>) -> Result<serde_json::Value, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let s = load_settings();
        let url = format!("{}/{}", s.api_url.trim_end_matches('/'), path.trim_start_matches('/'));
        let req = ureq::request(&method.to_uppercase(), &url).timeout(Duration::from_secs(20));
        let resp = match body {
            Some(b) => req.send_json(b),
            None => req.call(),
        };
        let text = match resp {
            Ok(r) => r.into_string().map_err(|e| e.to_string())?,
            Err(ureq::Error::Status(code, r)) => {
                let t = r.into_string().unwrap_or_default();
                return Err(format!("HTTP {code}: {}", if t.is_empty() { "sin respuesta" } else { &t }));
            }
            Err(e) => return Err(format!("No se pudo conectar con el servidor ({e}).")),
        };
        Ok(serde_json::from_str(&text).unwrap_or(serde_json::Value::String(text)))
    })
    .await
    .map_err(|e| e.to_string())?
}

