using System.Collections.Concurrent;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Schale_Server.Services;

namespace Schale_Server.Controllers.SDK;

/// <summary>
/// Serves the official Nexon / Blue Archive styled account authentication and registration
/// screen directly inside the game's embedded WebView, with automatic device linking.
/// </summary>
[ApiController]
[Route("/account/{lang}/global")]
[Route("/account/en/global")]
public sealed class AccountWebEntryController : ControllerBase
{
    private const string SessionCookie = "schale_account_session";
    private const string CsrfCookie = "schale_account_csrf";
    private static readonly Regex ValidUsername = new("^[A-Za-z0-9_.@+-]{3,254}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> FailedLogins = new(StringComparer.OrdinalIgnoreCase);

    private readonly LocalAccountStore _accounts;

    public AccountWebEntryController(LocalAccountStore accounts)
    {
        _accounts = accounts;
    }

    [HttpGet]
    [HttpGet("signup")]
    [HttpGet("login")]
    public IActionResult Index([FromRoute] string? lang, [FromQuery] string? notice, [FromQuery] string? error, [FromQuery] string? tab)
    {
        Response.Headers.CacheControl = "no-store";
        var csrf = EnsureCsrfToken();
        var session = _accounts.GetSessionAccount(Request.Cookies[SessionCookie]);
        var effectiveLang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;

        return Content(RenderPage(effectiveLang, "register", session, notice, error, csrf), "text/html; charset=utf-8");
    }

    [HttpPost("login")]
    public IActionResult Login([FromRoute] string? lang)
    {
        var effectiveLang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;
        return Index(effectiveLang, null, "El inicio de sesión se realiza directamente en el juego pulsando «Sign in with Nexon ID».", "register");
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromRoute] string? lang)
    {
        var effectiveLang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;
        var (valid, form) = await ValidateFormAsync();
        if (!valid)
            return Index(effectiveLang, null, "Sesión inválida o formulario vencido. Intenta de nuevo.", "register");

        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();

        if (!ValidUsername.IsMatch(username))
            return Index(effectiveLang, null, "Usuario no válido (3–254 letras, números o caracteres @ . _ - +).", "register");

        if (password.Length is < 8 or > 128)
            return Index(effectiveLang, null, "La contraseña debe tener entre 8 y 128 caracteres.", "register");

        if (!string.IsNullOrEmpty(confirmPassword) && password != confirmPassword)
            return Index(effectiveLang, null, "Las contraseñas no coinciden.", "register");

        if (!_accounts.Register(username, password))
            return Index(effectiveLang, null, "Ese usuario ya existe. Cierra esta ventana e inicia sesión en el juego con Nexon ID.", "register");

        var account = _accounts.GetAccount(username);
        var token = _accounts.CreateSession(username);
        SetSessionCookie(token);

        var deviceKey = GuestDeviceIdentity.RecentDeviceKey(Request);
        if (account != null && deviceKey != null)
        {
            _accounts.LinkDevice(username, deviceKey, account.PublisherId);
            GuestDeviceIdentity.BindDeviceAccount(deviceKey, account.PublisherId);
        }

        var session = account ?? _accounts.GetSessionAccount(token);
        return Content(RenderPage(effectiveLang, "session", session, null, null, EnsureCsrfToken()), "text/html; charset=utf-8");
    }

    [HttpPost("link")]
    public async Task<IActionResult> LinkDevice([FromRoute] string? lang)
    {
        var effectiveLang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;
        var (valid, _) = await ValidateFormAsync();
        if (!valid)
            return Index(effectiveLang, null, "Solicitud inválida. Recarga la página.", "session");

        var session = _accounts.GetSessionAccount(Request.Cookies[SessionCookie]);
        if (session == null)
            return Index(effectiveLang, null, "Inicia sesión antes de vincular tu teléfono.", "login");

        var deviceKey = GuestDeviceIdentity.RecentDeviceKey(Request);
        if (deviceKey == null)
            return Index(effectiveLang, null, "No se detectó la sesión del juego. Asegúrate de tener Blue Archive abierto y reintenta.", "session");

        _accounts.LinkDevice(session.Username, deviceKey, session.PublisherId);
        var notice = $"¡Teléfono vinculado exitosamente a <b>{HtmlEncoder.Default.Encode(session.Username)}</b>!<br>Toca la <b>X</b> arriba a la derecha y reinicia el juego.";
        return Content(RenderPage(effectiveLang, "session", session, notice, null, EnsureCsrfToken()), "text/html; charset=utf-8");
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromRoute] string? lang)
    {
        var effectiveLang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;
        var (valid, _) = await ValidateFormAsync();
        if (valid)
        {
            _accounts.RevokeSession(Request.Cookies[SessionCookie]);
            Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        }
        return Index(effectiveLang, "Sesión cerrada correctamente.", null, "login");
    }

    private string EnsureCsrfToken()
    {
        var existing = Request.Cookies[CsrfCookie];
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Response.Cookies.Append(CsrfCookie, token, new CookieOptions
        {
            Secure = Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(30)
        });
        return token;
    }

    private void SetSessionCookie(string token)
    {
        Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            Secure = Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });
    }

    private async Task<(bool Valid, Microsoft.AspNetCore.Http.IFormCollection Form)> ValidateFormAsync()
    {
        var form = await Request.ReadFormAsync();
        var cookie = Request.Cookies[CsrfCookie];
        var token = form["csrf"].ToString();
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(token))
            return (false, form);

        var left = System.Text.Encoding.UTF8.GetBytes(cookie);
        var right = System.Text.Encoding.UTF8.GetBytes(token);
        return (left.Length == right.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right), form);
    }

    private static string RenderPage(string lang, string activeTab, LocalAccountStore.LocalAccount? session, string? notice, string? error, string csrf)
    {
        var basePath = $"/account/{HtmlEncoder.Default.Encode(lang)}/global";
        var csrfInput = $"<input type=\"hidden\" name=\"csrf\" value=\"{HtmlEncoder.Default.Encode(csrf)}\">";

        var noticeHtml = string.IsNullOrWhiteSpace(notice)
            ? ""
            : $"<div class=\"status-banner success\">{notice}</div>";

        var errorHtml = string.IsNullOrWhiteSpace(error)
            ? ""
            : $"<div class=\"status-banner error\">{HtmlEncoder.Default.Encode(error)}</div>";

        string formSection;
        if (session != null)
        {
            formSection = $"""
                <div class="session-box">
                    <div class="session-avatar">
                        <svg viewBox="0 0 24 24" fill="#00d2ff" width="44" height="44">
                            <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 3c1.66 0 3 1.34 3 3s-1.34 3-3 3-3-1.34-3-3 1.34-3 3-3zm0 14.2c-2.5 0-4.71-1.28-6-3.22.03-1.99 4-3.08 6-3.08 1.99 0 5.97 1.09 6 3.08-1.29 1.94-3.5 3.22-6 3.22z"/>
                        </svg>
                    </div>
                    <div class="session-title">CUENTA REGISTRADA</div>
                    <div class="session-user">{HtmlEncoder.Default.Encode(session.Username)}</div>
                    <p class="session-desc">Tu cuenta de Schale Server se creó y vinculó a este teléfono exitosamente.</p>
                    <div class="session-steps">
                        <div class="step-item"><span class="step-num">1</span> Toca la <b>X</b> arriba a la derecha para cerrar esta ventana.</div>
                        <div class="step-item"><span class="step-num">2</span> En la pantalla del juego, pulsa <b>Sign in with Nexon ID</b>.</div>
                        <div class="step-item"><span class="step-num">3</span> Introduce tu correo y contraseña para entrar a jugar.</div>
                    </div>
                </div>
                """;
        }
        else
        {
            formSection = $"""
                <!-- REGISTER FORM -->
                <form id="form-register" method="post" action="{basePath}/register" class="auth-form">
                    {csrfInput}
                    <div class="input-group">
                        <input id="reg-user" name="username" type="text" autocomplete="username" required minlength="3" maxlength="254" placeholder="Email or Username" pattern="[A-Za-z0-9_.@+-]+" oninput="updateButtons()">
                        <button type="button" class="btn-clear" onclick="clearInput('reg-user')" tabindex="-1">X</button>
                    </div>
                    <div class="input-group">
                        <input id="reg-pass" name="password" type="password" autocomplete="new-password" required minlength="8" maxlength="128" placeholder="Password (min 8 characters)" oninput="updateButtons()">
                        <button type="button" class="btn-eye" onclick="togglePass('reg-pass', this)" tabindex="-1">VER</button>
                    </div>
                    <div class="input-group">
                        <input id="reg-pass2" name="confirmPassword" type="password" autocomplete="new-password" placeholder="Confirm Password" oninput="updateButtons()">
                        <button type="button" class="btn-eye" onclick="togglePass('reg-pass2', this)" tabindex="-1">VER</button>
                    </div>
                    <button id="btn-reg-submit" type="submit" class="btn btn-nexon">CREATE ACCOUNT</button>
                    <div class="auth-footer">
                        <span class="auth-hint">¿Ya tienes cuenta? Cierra esta ventana (toca la <b>X</b> arriba) y pulsa <b>Sign in with Nexon ID</b> en el juego.</span>
                    </div>
                </form>
                """;
        }

        return $$"""
            <!doctype html>
            <html lang="es">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
                <title>Nexon Login - Schale Server</title>
                <style>
                    * { box-sizing: border-box; margin: 0; padding: 0; }
                    html, body {
                        width: 100%;
                        height: 100%;
                        background-color: #000000;
                        color: #ffffff;
                        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
                        overflow-x: hidden;
                        display: flex;
                        justify-content: center;
                        align-items: center;
                    }
                    /* Background subtle Blue Archive grid & halo glow */
                    body::before {
                        content: "";
                        position: fixed;
                        top: 50%;
                        left: 50%;
                        width: 500px;
                        height: 500px;
                        transform: translate(-50%, -50%);
                        background: radial-gradient(circle, rgba(18, 138, 250, 0.08) 0%, rgba(0, 0, 0, 0) 70%);
                        pointer-events: none;
                        z-index: 0;
                    }
                    .auth-container {
                        position: relative;
                        z-index: 1;
                        width: 100%;
                        max-width: 410px;
                        padding: 16px 24px;
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                    }
                    /* Official Nexon Logo SVG */
                    .logo-wrap {
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                        margin-bottom: 24px;
                    }
                    .nexon-logo {
                        width: 135px;
                        height: auto;
                        display: block;
                    }
                    .ba-tag {
                        font-size: 10px;
                        letter-spacing: 2px;
                        color: #128afa;
                        margin-top: 6px;
                        font-weight: 700;
                        text-transform: uppercase;
                        opacity: 0.9;
                    }
                    /* Status alerts */
                    .status-banner {
                        width: 100%;
                        padding: 10px 14px;
                        border-radius: 6px;
                        font-size: 13px;
                        line-height: 1.45;
                        margin-bottom: 16px;
                        text-align: center;
                    }
                    .status-banner.success {
                        background: rgba(18, 138, 250, 0.15);
                        border: 1px solid #128afa;
                        color: #80c4ff;
                    }
                    .status-banner.error {
                        background: rgba(255, 68, 68, 0.15);
                        border: 1px solid #ff4444;
                        color: #ff9999;
                    }
                    /* Input fields: underline Nexon style */
                    .auth-form {
                        width: 100%;
                        display: flex;
                        flex-direction: column;
                    }
                    .auth-form.hidden {
                        display: none;
                    }
                    .input-group {
                        position: relative;
                        width: 100%;
                        border-bottom: 1px solid #444444;
                        margin-bottom: 22px;
                        transition: border-color 0.2s ease;
                        display: flex;
                        align-items: center;
                    }
                    .input-group:focus-within {
                        border-bottom-color: #ffffff;
                    }
                    .input-group input {
                        width: 100%;
                        background: transparent;
                        border: none;
                        outline: none;
                        color: #ffffff;
                        font-size: 15px;
                        padding: 10px 32px 10px 2px;
                        font-family: inherit;
                    }
                    .input-group input::placeholder {
                        color: #555555;
                        font-size: 14px;
                    }
                    .btn-clear {
                        position: absolute;
                        right: 4px;
                        background: transparent;
                        border: none;
                        color: #666666;
                        font-size: 13px;
                        font-weight: 700;
                        cursor: pointer;
                        padding: 4px;
                        line-height: 1;
                        user-select: none;
                    }
                    .btn-clear:hover {
                        color: #cccccc;
                    }
                    .btn-eye {
                        position: absolute;
                        right: 4px;
                        background: transparent;
                        border: none;
                        color: #777777;
                        font-size: 11px;
                        font-weight: 700;
                        letter-spacing: 0.5px;
                        cursor: pointer;
                        padding: 4px 6px;
                        line-height: 1;
                        user-select: none;
                    }
                    .btn-eye:hover {
                        color: #ffffff;
                    }
                    /* Action Button: Nexon rectangular style */
                    .btn-nexon {
                        width: 100%;
                        height: 46px;
                        background: #444444;
                        color: #888888;
                        border: none;
                        border-radius: 2px;
                        font-size: 14px;
                        font-weight: 700;
                        letter-spacing: 1.5px;
                        cursor: pointer;
                        margin-top: 14px;
                        transition: all 0.2s ease;
                        text-transform: uppercase;
                    }
                    .btn-nexon.active {
                        background: #ffffff;
                        color: #000000;
                    }
                    .btn-nexon.active:hover {
                        background: #e0e0e0;
                    }
                    .btn-nexon.active:active {
                        background: #cccccc;
                    }
                    /* Footer Links */
                    .auth-footer {
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        gap: 14px;
                        margin-top: 24px;
                        font-size: 13px;
                    }
                    .auth-footer a {
                        color: #aaaaaa;
                        text-decoration: none;
                        transition: color 0.15s ease;
                        font-weight: 500;
                    }
                    .auth-footer a:hover {
                        color: #ffffff;
                        text-decoration: underline;
                    }
                    .auth-footer .divider {
                        color: #333333;
                    }
                    /* Session view card */
                    .session-box {
                        width: 100%;
                        background: #0d141e;
                        border: 1px solid #1a2d42;
                        border-radius: 8px;
                        padding: 22px;
                        text-align: center;
                    }
                    .session-avatar {
                        margin-bottom: 8px;
                    }
                    .session-title {
                        font-size: 12px;
                        letter-spacing: 1.5px;
                        color: #128afa;
                        text-transform: uppercase;
                        font-weight: 700;
                        margin-bottom: 6px;
                    }
                    .session-user {
                        font-size: 18px;
                        font-weight: 700;
                        color: #ffffff;
                        margin-bottom: 10px;
                        word-break: break-all;
                    }
                    .session-desc {
                        font-size: 13px;
                        color: #8899aa;
                        line-height: 1.45;
                        margin-bottom: 14px;
                    }
                    .session-steps {
                        background: #060b11;
                        border: 1px solid #142232;
                        border-radius: 6px;
                        padding: 14px;
                        text-align: left;
                    }
                    .step-item {
                        font-size: 13px;
                        color: #cfd8e3;
                        line-height: 1.5;
                        margin-bottom: 10px;
                        display: flex;
                        align-items: flex-start;
                        gap: 8px;
                    }
                    .step-item:last-child {
                        margin-bottom: 0;
                    }
                    .step-num {
                        display: inline-flex;
                        align-items: center;
                        justify-content: center;
                        width: 20px;
                        height: 20px;
                        background: #128afa;
                        color: #ffffff;
                        border-radius: 50%;
                        font-size: 11px;
                        font-weight: 700;
                        flex-shrink: 0;
                    }
                    .auth-hint {
                        color: #8899aa;
                        font-size: 13px;
                        line-height: 1.5;
                        text-align: center;
                    }
                    .auth-hint b {
                        color: #ffffff;
                    }
                    .btn-link {
                        background: transparent;
                        border: none;
                        color: #888888;
                        font-size: 13px;
                        cursor: pointer;
                        text-decoration: underline;
                        padding: 6px;
                    }
                    .btn-link:hover {
                        color: #ffffff;
                    }
                </style>
                <script>
                    function clearInput(id) {
                        var el = document.getElementById(id);
                        if (el) {
                            el.value = '';
                            el.focus();
                            updateButtons();
                        }
                    }

                    function togglePass(id, btn) {
                        var el = document.getElementById(id);
                        if (el) {
                            var isPass = el.type === 'password';
                            el.type = isPass ? 'text' : 'password';
                            btn.textContent = isPass ? 'OCULTAR' : 'VER';
                        }
                    }

                    function updateButtons() {
                        var regUser = document.getElementById('reg-user');
                        var regPass = document.getElementById('reg-pass');
                        var btnReg = document.getElementById('btn-reg-submit');
                        if (regUser && regPass && btnReg) {
                            var canReg = regUser.value.trim().length >= 3 && regPass.value.length >= 8;
                            btnReg.classList.toggle('active', canReg);
                        }
                    }

                    window.addEventListener('DOMContentLoaded', updateButtons);
                </script>
            </head>
            <body>
                <div class="auth-container">
                    <div class="logo-wrap">
                        <!-- Official Nexon Logo Vector -->
                        <svg viewBox="208 42 792 123" class="nexon-logo" fill="#FFFFFF" xmlns="http://www.w3.org/2000/svg">
                            <polygon points="241.42,90.499 318.555,164.106 343.135,164.106 343.135,42.383 310.586,42.383 310.586,116.001 233.446,42.383 208.876,42.383 208.876,164.106 241.42,164.106"/>
                            <polygon points="502.617,138.306 410.801,138.306 410.801,114.927 502.617,114.927 502.617,89.039 410.801,89.039 410.801,68.194 502.617,68.194 502.617,42.383 378.213,42.383 378.213,164.106 502.617,164.106"/>
                            <polygon points="599.619,126.495 632.959,164.106 674.082,164.106 620.166,103.287 674.209,42.383 632.998,42.383 599.619,80.083 566.191,42.383 524.99,42.383 579.033,103.287 525.117,164.106 566.279,164.106"/>
                            <path d="M830.693,42.383H696.436v121.723h134.258V42.383z M798.164,138.306h-69.17V68.194h69.17V138.306z"/>
                            <polygon points="898.301,90.499 975.469,164.106 1000.001,164.106 1000.001,42.383 967.461,42.383 967.461,116.001 890.322,42.383 865.771,42.383 865.771,164.106 898.301,164.106"/>
                        </svg>
                        <div class="ba-tag">SCHALE SERVER</div>
                    </div>

                    {{noticeHtml}}
                    {{errorHtml}}
                    {{formSection}}
                </div>
            </body>
            </html>
            """;
    }
}
