using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Identity.Web;

namespace KodAnalist;

/// <summary>Giriş ayarlarının özeti (ayar dosyasındaki "AzureAd" bölümünden).</summary>
public sealed record AuthSettings(bool Enabled, string[] AllowedGroupIds);

/// <summary>
/// Microsoft Entra ID (Azure Active Directory) ile giriş. Yalnızca şirketin kendi dizinindeki (tenant) hesaplar girebilir;
/// istenirse ayrıca belirli güvenlik gruplarıyla sınırlanır. Şifre bu uygulamaya hiç uğramaz: kullanıcı Microsoft'un
/// giriş sayfasında (şirketin MFA ve şifre kurallarıyla) giriş yapar ve uygulamaya geri döner.
///
/// "AzureAd:TenantId" ve "AzureAd:ClientId" boşsa giriş kapalıdır; uygulama girişsiz çalışır (yerel geliştirme).
/// </summary>
public static class AuthSetup
{
    public const string LoginPath = "/giris";

    public static AuthSettings AddPromenaAuth(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("AzureAd");
        var enabled = !string.IsNullOrWhiteSpace(section["TenantId"]) && !string.IsNullOrWhiteSpace(section["ClientId"]);
        var groups = section.GetSection("AllowedGroupIds").Get<string[]>()?.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray() ?? [];
        var settings = new AuthSettings(enabled, groups);
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

        // Microsoft giriş sayfasına yönlendirir; dönüşte uygulamanın ana sayfası açılır.
        app.MapGet("/auth/login", (string? hesap) =>
        {
            if (!settings.Enabled) return Results.Redirect("/");
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
