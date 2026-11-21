using System.Collections.Concurrent;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Schale_Server.Controllers.SDK;
using Schale_Server.Services;

namespace Schale_Server.Controllers;

[ApiController]
[Route("account")]
public sealed class AccountPortalController : ControllerBase
{
    private const string SessionCookie = "schale_account_session";
    private const string CsrfCookie = "schale_account_csrf";
    private static readonly Regex ValidUsername = new("^[A-Za-z0-9_.@+-]{3,254}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> FailedLogins = new(StringComparer.OrdinalIgnoreCase);
    private readonly LocalAccountStore _accounts;

    public AccountPortalController(LocalAccountStore accounts) => _accounts = accounts;

    [HttpGet]
    public IActionResult Index([FromQuery] string? notice)
    {
        if (!RequireHttps())
            return StatusCode(StatusCodes.Status426UpgradeRequired, "Abre https://<dominio-del-servidor>/account. El login por contraseña solo se acepta con TLS.");

        Response.Headers.CacheControl = "no-store";
        var csrf = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Response.Cookies.Append(CsrfCookie, csrf, new CookieOptions
        {
            Secure = Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/account",
            MaxAge = TimeSpan.FromMinutes(20)
        });

        var session = _accounts.GetSessionAccount(Request.Cookies[SessionCookie]);
        var escapedNotice = string.IsNullOrWhiteSpace(notice) ? "" : $"<p class=notice>{HtmlEncoder.Default.Encode(notice)}</p>";
        var csrfInput = $"<input type=hidden name=csrf value=\"{HtmlEncoder.Default.Encode(csrf)}\">";
        var accountPanel = session == null
            ? $"""
                <section><h2>Crear cuenta</h2>
                  <form method=post action=/account/register>{csrfInput}
                    <label>Correo o usuario<input name=username autocomplete=username minlength=3 maxlength=254 required pattern="[A-Za-z0-9_.@+-]+"></label>
                    <label>Contraseña<input name=password type=password autocomplete=new-password minlength=10 maxlength=128 required></label>
                    <button>Crear cuenta y entrar</button>
                  </form>
                </section>
                <section><h2>Ya tengo cuenta</h2>
                  <form method=post action=/account/login>{csrfInput}
                    <label>Correo o usuario<input name=username autocomplete=username required></label>
                    <label>Contraseña<input name=password type=password autocomplete=current-password required></label>
                    <button>Entrar</button>
                  </form>
                </section>
                """
            : $"""
                <section><h2>Sesión iniciada</h2>
                  <p>Cuenta: <strong>{HtmlEncoder.Default.Encode(session.Username)}</strong></p>
                  <p>Abre el juego en este teléfono y vuelve aquí para vincularlo. La vinculación conserva la misma cuenta del juego entre teléfonos.</p>
                  <form method=post action=/account/link>{csrfInput}<button>Vincular este teléfono</button></form>
                  <form method=post action=/account/logout>{csrfInput}<button class=secondary>Cerrar sesión web</button></form>
                </section>
                """;

        var html = $$"""
            <!doctype html><html lang=es><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
            <title>Cuenta Schale</title>
            <style>
              *{box-sizing:border-box}body{margin:0;background:#eef5fb;color:#18324f;font:16px system-ui,sans-serif}
              main{max-width:520px;margin:7vh auto;padding:24px}h1{margin:0 0 8px}.sub{color:#58718b;margin:0 0 24px}
              section{background:white;border:1px solid #cfdeeb;border-radius:14px;padding:20px;margin:16px 0;box-shadow:0 4px 18px #18324f12}
              h2{font-size:19px;margin:0 0 14px}label{display:block;font-weight:600;margin:12px 0}input{display:block;width:100%;padding:12px;border:1px solid #b5c9dc;border-radius:8px;margin-top:6px;font:inherit}
              button{background:#52c9ee;border:0;border-radius:8px;padding:12px 16px;color:#123452;font-weight:700;font-size:16px;width:100%;margin-top:10px}
              button.secondary{background:#e9eff5}.notice{background:#fff4ca;padding:12px;border-radius:8px}small{color:#58718b}
            </style><main><h1>Cuenta Schale</h1><p class=sub>Una cuenta por jugador; úsala para recuperar tu partida en otro teléfono.</p>
            {{escapedNotice}}{{accountPanel}}
            <small>Cuenta del servidor privado. No es un inicio de sesión de Nexon. Usa una contraseña única; las contraseñas se guardan con hash y sal.</small></main></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register()
    {
        if (!RequireHttps()) return StatusCode(StatusCodes.Status426UpgradeRequired);
        var (formValid, form) = await ValidateFormAsync();
        if (!formValid) return BadRequest("Formulario inválido o vencido. Recarga /account.");
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        if (!ValidUsername.IsMatch(username) || password.Length is < 10 or > 128)
            return RedirectNotice("Correo/usuario inválido (3–254 letras, números, @, punto, guion o guion bajo) o contraseña menor de 10 caracteres.");

        if (!_accounts.Register(username, password))
            return RedirectNotice("Ese usuario ya existe. Inicia sesión con su contraseña.");
        SetSessionCookie(_accounts.CreateSession(username));
        return RedirectNotice("Cuenta creada. Ahora abre el juego en este teléfono y pulsa Vincular este teléfono.");
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login()
    {
        if (!RequireHttps()) return StatusCode(StatusCodes.Status426UpgradeRequired);
        var (formValid, form) = await ValidateFormAsync();
        if (!formValid) return BadRequest("Formulario inválido o vencido. Recarga /account.");
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var rateKey = ip + ":" + form["username"].ToString().Trim().ToLowerInvariant();
        if (FailedLogins.TryGetValue(rateKey, out var recent)
            && DateTimeOffset.UtcNow - recent.Since < TimeSpan.FromMinutes(15)
            && recent.Failures >= 8)
            return RedirectNotice("Demasiados intentos. Espera 15 minutos.");

        var account = _accounts.Authenticate(form["username"].ToString().Trim(), form["password"].ToString());
        if (account == null)
        {
            FailedLogins.AddOrUpdate(rateKey, (1, DateTimeOffset.UtcNow), (_, old) =>
                DateTimeOffset.UtcNow - old.Since >= TimeSpan.FromMinutes(15)
                    ? (1, DateTimeOffset.UtcNow)
                    : (old.Failures + 1, old.Since));
            return RedirectNotice("Usuario o contraseña incorrectos.");
        }

        FailedLogins.TryRemove(rateKey, out _);
        SetSessionCookie(_accounts.CreateSession(account.Username));
        return RedirectNotice("Sesión iniciada. Abre el juego en este teléfono y vincúlalo para recuperar tu partida aquí.");
    }

    [HttpPost("link")]
    public async Task<IActionResult> LinkDevice()
    {
        if (!RequireHttps()) return StatusCode(StatusCodes.Status426UpgradeRequired);
        var (formValid, _) = await ValidateFormAsync();
        if (!formValid) return BadRequest("Formulario inválido o vencido. Recarga /account.");
        var account = _accounts.GetSessionAccount(Request.Cookies[SessionCookie]);
        if (account == null)
            return RedirectNotice("Inicia sesión antes de vincular el teléfono.");

        var deviceKey = GuestDeviceIdentity.RecentDeviceKey(Request);
        if (deviceKey == null)
            return RedirectNotice("No veo una sesión reciente del juego desde este teléfono. Abre el juego, vuelve a esta página y prueba otra vez.");

        _accounts.LinkDevice(account.Username, deviceKey, account.PublisherId);
        return RedirectNotice("Teléfono vinculado. Reinicia el juego en este teléfono para entrar a esta cuenta.");
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (!RequireHttps()) return StatusCode(StatusCodes.Status426UpgradeRequired);
        var (formValid, _) = await ValidateFormAsync();
        if (!formValid) return BadRequest("Formulario inválido o vencido. Recarga /account.");
        _accounts.RevokeSession(Request.Cookies[SessionCookie]);
        Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/account" });
        return RedirectNotice("Sesión web cerrada.");
    }

    private bool RequireHttps() => true;

    private async Task<(bool Valid, Microsoft.AspNetCore.Http.IFormCollection Form)> ValidateFormAsync()
    {
        var form = await Request.ReadFormAsync();
        var cookie = Request.Cookies[CsrfCookie];
        var token = form["csrf"].ToString();
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(token)) return (false, form);
        var left = System.Text.Encoding.UTF8.GetBytes(cookie);
        var right = System.Text.Encoding.UTF8.GetBytes(token);
        return (left.Length == right.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right), form);
    }

    private void SetSessionCookie(string token)
        => Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            Secure = Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/account",
            MaxAge = TimeSpan.FromDays(30)
        });

    private IActionResult RedirectNotice(string notice)
        => Redirect($"/account?notice={WebUtility.UrlEncode(notice)}");
}
