using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Identity.Web;

namespace KodAnalist;

/// <summary>Giriş ayarlarının özeti. Mode: "ldap", "entra" veya "none" (giriş kapalı).</summary>
public sealed record AuthSettings(bool Enabled, string Mode, string[] AllowedGroupIds);

public sealed record LoginRequest(string? Email, string? Password);

/// <summary>
/// Uygulamaya giriş. Ayarlara göre iki yöntemden biri kullanılır:
/// - "Ldap:Server" doluysa: giriş ekranında e-posta + şifre; şifre şirket içi Active Directory'de (LDAP) doğrulanır
///   (<see cref="LdapAuthService"/>). Bilgisayara girilen domain şifresiyle aynıdır.
/// - Değilse ve "AzureAd:TenantId" / "AzureAd:ClientId" doluysa: Microsoft Entra ID; kullanıcı Microsoft'un giriş
///   sayfasında giriş yapar ve uygulamaya döner.
/// İkisi de boşsa giriş kapalıdır; uygulama girişsiz çalışır (yerel geliştirme).
/// </summary>
public static class AuthSetup
{
    public const string LoginPath = "/giris";

    public static AuthSettings AddPromenaAuth(this WebApplicationBuilder builder)
    {
        var ldap = builder.Configuration.GetSection("Ldap").Get<LdapOptions>() ?? new LdapOptions();
        if (ldap.IsConfigured)
        {
            builder.Services.AddSingleton(ldap);
            builder.Services.AddSingleton<LdapAuthService>();
            var ldapSettings = new AuthSettings(true, "ldap", []);
            builder.Services.AddSingleton(ldapSettings);
            // Grup kontrolü girişte (LdapAuthService) yapılır; sonrasında giriş yapmış olmak yeterli.
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
            ConfigureCookie(builder);
            builder.Services.AddAuthorization(o => o.FallbackPolicy =
                new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());
            return ldapSettings;
        }

        var section = builder.Configuration.GetSection("AzureAd");
        var enabled = !string.IsNullOrWhiteSpace(section["TenantId"]) && !string.IsNullOrWhiteSpace(section["ClientId"]);
        var groups = section.GetSection("AllowedGroupIds").Get<string[]>()?.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray() ?? [];
        var settings = new AuthSettings(enabled, enabled ? "entra" : "none", groups);
        builder.Services.AddSingleton(settings);
        if (!enabled) return settings;

        // Oturum çerezde tutulur. Giriş yapmamış kullanıcı önce bizim giriş ekranımıza gelir (doğrudan Microsoft'a değil).
        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddMicrosoftIdentityWebApp(section, OpenIdConnectDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme);

        ConfigureCookie(builder);

        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
        {
            // "Farklı hesapla giriş": Microsoft hesap seçme ekranını göstersin.
            var redirect = o.Events.OnRedirectToIdentityProvider;
            o.Events.OnRedirectToIdentityProvider = async ctx =>
            {
                if (ctx.Properties.Items.TryGetValue("prompt", out var prompt) && !string.IsNullOrEmpty(prompt))
                    ctx.ProtocolMessage.Prompt = prompt;
                if (redirect != null) await redirect(ctx);
            };
            // Microsoft tarafında giriş iptal edilir veya hata olursa kullanıcı giriş ekranında anlaşılır bir mesaj görsün.
            var failure = o.Events.OnRemoteFailure;
            o.Events.OnRemoteFailure = async ctx =>
            {
                if (failure != null) await failure(ctx);
                if (ctx.Result?.Handled == true) return;
                ctx.HttpContext.RequestServices.GetRequiredService<ILogger<AuthSettings>>()
                    .LogWarning(ctx.Failure, "Microsoft girişi tamamlanamadı");
                ctx.Response.Redirect(LoginPath + "?hata=giris");
                ctx.HandleResponse();
            };
        });

        builder.Services.AddAuthorization(o =>
        {
            // Varsayılan kural: her istek giriş ister (ayrıca izin verilenler hariç). Grup listesi varsa kullanıcı
            // bunlardan birinde olmalı (uygulama kaydında "groups" talebi açık olmalı).
            var policy = new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser();
            if (groups.Length > 0)
                policy.RequireAssertion(c => c.User.FindAll("groups").Any(g => groups.Contains(g.Value, StringComparer.OrdinalIgnoreCase)));
            o.FallbackPolicy = policy.Build();
        });
        return settings;
    }

    private static void ConfigureCookie(WebApplicationBuilder builder) =>
        builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            o.Cookie.Name = "PromenaRehberi.Oturum";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(10);   // bir iş günü
            o.SlidingExpiration = true;
            // API çağrıları yönlendirme değil 401 / 403 alır; ekran bunu görünce giriş ekranına geçer.
            o.Events.OnRedirectToLogin = ctx => RedirectOrStatus(ctx, LoginPath, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => RedirectOrStatus(ctx, LoginPath + "?hata=yetki", StatusCodes.Status403Forbidden);
        });

    /// <summary>
    /// Giriş ara katmanlarını ve uç noktalarını ekler. Statik dosyalardan ÖNCE çağrılmalıdır: böylece ana sayfa ve
    /// API'ler de girişe tabi olur. Giriş ekranı ve logolar giriş yapmadan görünür.
    /// </summary>
    public static void UsePromenaAuth(this WebApplication app, AuthSettings settings)
    {
        // Giriş ekranındaki logo ve sekme simgesi giriş yapmadan da görünmeli.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.WebRootPath, "img")),
            RequestPath = "/img",
        });

        if (settings.Enabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapGet(LoginPath, (HttpContext http) =>
            !settings.Enabled
                ? Results.Redirect("/")   // giriş kapalıysa doğrudan uygulama
                : Results.File(Path.Combine(app.Environment.WebRootPath, "giris.html"), "text/html; charset=utf-8"))
            .AllowAnonymous();

        // E-posta + şifre (Active Directory / LDAP). Şifre kaydedilmez ve loglanmaz.
        app.MapPost("/auth/giris", async (LoginRequest req, HttpContext http) =>
        {
            if (settings.Mode != "ldap") return Results.NotFound();
            // Yalnızca giriş ekranımızdan (aynı siteden, fetch ile) gelen istekler: başka sitelerin form göndermesini engeller.
            if (http.Request.Headers["X-Requested-With"] != "fetch") return Results.BadRequest();
            var origin = http.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) &&
                (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var ldap = http.RequestServices.GetRequiredService<LdapAuthService>();
            var result = await ldap.LoginAsync(req.Email, req.Password, http.Connection.RemoteIpAddress?.ToString());
            switch (result.Status)
            {
                case LdapLoginStatus.Success:
                    var u = result.User!;
                    var claims = new List<Claim> { new("name", u.Name), new("preferred_username", u.Email), new(ClaimTypes.Email, u.Email) };
                    claims.AddRange(u.Groups.Select(g => new Claim("groups", g)));
                    // Oturum çerezi tarayıcı kapanınca silinir; açıkken 10 saat (kullanıldıkça uzar).
                    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(new ClaimsIdentity(claims, "ldap", "name", ClaimTypes.Role)));
                    return Results.Json(new { ok = true });
                case LdapLoginStatus.NotAllowed:
                    return Results.Json(new { error = "Bu hesapla Promena Rehberi'ne erişim yetkiniz yok. Yöneticinizden erişim isteyin." }, statusCode: 403);
                case LdapLoginStatus.TooManyAttempts:
                    return Results.Json(new { error = "Çok fazla hatalı deneme yapıldı. Hesabınızın kilitlenmemesi için 15 dakika sonra tekrar deneyin." }, statusCode: 429);
                case LdapLoginStatus.ServerUnavailable:
                    return Results.Json(new { error = "Şirket dizin sunucusuna ulaşılamadı. Şirket ağına (veya VPN'e) bağlı olduğunuzdan emin olup tekrar deneyin." }, statusCode: 503);
                default:
                    return Results.Json(new { error = "E-posta veya şifre hatalı." }, statusCode: 401);
            }
        }).AllowAnonymous();

        // Microsoft giriş sayfasına yönlendirir; dönüşte uygulamanın ana sayfası açılır.
        app.MapGet("/auth/login", (string? hesap) =>
        {
            if (settings.Mode != "entra") return Results.Redirect(settings.Enabled ? LoginPath : "/");
            var props = new AuthenticationProperties { RedirectUri = "/" };
            if (hesap == "sec") props.Items["prompt"] = "select_account";
            return Results.Challenge(props, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous();

        // Yalnızca bu uygulamadan çıkış (diğer Microsoft uygulamalarındaki oturum açık kalır).
        app.MapGet("/auth/logout", async (HttpContext http) =>
        {
            if (settings.Enabled) await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(settings.Enabled ? LoginPath + "?cikis=1" : "/");
        }).AllowAnonymous();

        // Ekranın kimin giriş yaptığını göstermesi için.
        app.MapGet("/api/me", (HttpContext http) =>
        {
            var user = http.User;
            var signedIn = user.Identity?.IsAuthenticated == true;
            return Results.Json(new
            {
                authEnabled = settings.Enabled,
                mode = settings.Mode,
                signedIn,
                name = signedIn ? DisplayName(user) : null,
                email = signedIn ? Email(user) : null,
            });
        }).AllowAnonymous();
    }

    public static string? DisplayName(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value ?? user.Identity?.Name ?? Email(user);

    public static string? Email(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst(ClaimTypes.Upn)?.Value;

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> ctx, string target, int status)
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
            ctx.Response.StatusCode = status;
        else
            ctx.Response.Redirect(target);
        return Task.CompletedTask;
    }
}
