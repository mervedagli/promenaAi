using System.Text.Json;
using Anthropic;
using Anthropic.Foundry;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using KodAnalist;

var builder = WebApplication.CreateBuilder(args);

// Makineye özel ayarlar (repo yolları, Foundry kaynak adı, Cosmos adresi): Git'e gönderilmez.
// Örnek: appsettings.Local.example.json. Gizli anahtarlar burada değil, user-secrets'ta tutulur.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var repos = builder.Configuration.GetSection("Repos").Get<List<RepoConfig>>() ?? [];
var foundry = builder.Configuration.GetSection("Foundry").Get<FoundryOptions>() ?? new FoundryOptions();
var agentOptions = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();

// Cevap modları (Hızlı / Dengeli / Derin). Ayar dosyasında yoksa varsayılanlar kullanılır.
var answerModes = builder.Configuration.GetSection("AnswerModes").Get<List<AnswerModeOptions>>() is { Count: > 0 } configuredModes
    ? configuredModes
    : AnswerModeOptions.Defaults(agentOptions);
var defaultMode = answerModes.FirstOrDefault(m => m.Default) ?? answerModes[0];
AnswerModeOptions ResolveMode(string? id, bool fastMode) =>
    answerModes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
    ?? (fastMode ? answerModes.FirstOrDefault(m => !m.Verifies) : null)
    ?? defaultMode;

// API anahtarı ayar dosyasında değil, ortam değişkeninde / gizli bilgi deposunda tutulur.
foundry.ApiKey ??= Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_API_KEY");

if (string.IsNullOrWhiteSpace(foundry.ResourceName))
    throw new InvalidOperationException("Foundry:ResourceName ayarı boş. appsettings.Local.json dosyasını doldurun (örnek: appsettings.Local.example.json).");

var repoSync = builder.Configuration.GetSection("RepoSync").Get<RepoSyncOptions>() ?? new RepoSyncOptions();
var syncBaseDir = Path.GetFullPath(repoSync.BaseDir, builder.Environment.ContentRootPath);

foreach (var repo in repos)
{
    if (repoSync.Enabled && !string.IsNullOrWhiteSpace(repo.RemoteUrl))
    {
        // Otomatik eşitleme: repo uygulamaya ait klon klasöründen okunur (ilk açılışta arka planda klonlanır).
        repo.Path = Path.Combine(syncBaseDir, repo.Name);
        continue;
    }

    // Göreli yollar proje klasörüne (src/KodAnalist) göre çözülür; KodHaritasi da aynı kuralı kullanır.
    repo.Path = Path.GetFullPath(repo.Path, builder.Environment.ContentRootPath);
    if (!Directory.Exists(repo.Path))
        throw new InvalidOperationException(
            $"'{repo.Name}' deposu bulunamadı: {repo.Path}. Yolu appsettings.Local.json dosyasında düzeltin " +
            "(örnek: appsettings.Local.example.json) ya da RepoSync ile RemoteUrl tanımlayın.");
}

builder.Services.AddSingleton(repoSync);
builder.Services.AddSingleton(repos);
builder.Services.AddSingleton<RepoSyncStatus>();
builder.Services.AddSingleton<RepoSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RepoSyncService>());

builder.Services.AddSingleton(foundry);
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton(new CodeTools(repos));
builder.Services.AddSingleton(builder.Configuration.GetSection("CodeMap").Get<CodeMapOptions>() ?? new CodeMapOptions());
builder.Services.AddSingleton<CodeMapService>();
builder.Services.AddSingleton<EvidenceBuilder>();
builder.Services.AddSingleton<SourceTracker>();
// Katalog anahtarı ayar dosyasında değil; user-secrets ("Catalog:Key") veya ortam değişkeninden gelir.
builder.Services.AddSingleton(builder.Configuration.GetSection("Catalog").Get<CatalogOptions>() ?? new CatalogOptions());
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton(sp => new ScopeService(
    sp.GetRequiredService<CodeMapService>(),
    builder.Configuration.GetSection("Scopes").Get<List<ScopeDefinition>>() ?? [],
    builder.Configuration.GetSection("Topics").Get<List<TopicDefinition>>() ?? [],
    sp.GetRequiredService<ILogger<ScopeService>>()));
builder.Services.AddSingleton<AnthropicClient>(_ =>
{
    IAnthropicFoundryCredentials credentials = string.IsNullOrWhiteSpace(foundry.ApiKey)
        ? new AnthropicFoundryIdentityTokenCredentials(new DefaultAzureCredential(), foundry.ResourceName)   // Microsoft Entra ID
        : new AnthropicFoundryApiKeyCredentials(foundry.ApiKey, foundry.ResourceName);
    return new AnthropicFoundryClient(credentials);
});
builder.Services.AddSingleton<AnalystAgent>();
builder.Services.AddSingleton<AskJobStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AskJobStore>());

// Giriş: "Ldap" ayarı doluysa e-posta + şifre (Active Directory), "AzureAd" doluysa Microsoft Entra ID; ikisi de boşsa kapalı.
var authSettings = builder.AddPromenaAuth();

var app = builder.Build();

if (!authSettings.Enabled)
    app.Logger.LogWarning("Giriş KAPALI: Ldap:Server veya AzureAd:TenantId / AzureAd:ClientId ayarlanmamış. Uygulamayı adresi bilen herkes kullanabilir.");
else
    app.Logger.LogInformation("Giriş açık: {Mode}", authSettings.Mode == "ldap" ? "e-posta + şifre (Active Directory / LDAP)" : "Microsoft Entra ID");
if (authSettings.Mode == "ldap" && !app.Services.GetRequiredService<LdapOptions>().UseSsl)
    app.Logger.LogWarning("LDAP bağlantısı ŞİFRESİZ (Ldap:UseSsl=false): şifreler ağda açık gider. Yalnızca test içindir.");

// Giriş, ana sayfa ve statik dosyalardan önce: giriş yapmayan kullanıcı yalnızca giriş ekranını görür.
app.UsePromenaAuth(authSettings);

app.UseDefaultFiles();
// Ekran dosyaları tarayıcıda önbelleğe alınmasın: güncelleme sonrası eski sayfa görünmesin.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/api/repos", (CodeTools tools) =>
    tools.Repos.Select(r => new { r.Name, r.Description }));

// Kod haritası: model çağırmadan (token harcamadan) akış görüntüleme.
app.MapGet("/api/map/find", (string q, CodeMapService map) => Results.Text(map.Find(q)));
app.MapGet("/api/map/trace", (string target, int? depth, CodeMapService map) => Results.Text(map.Trace(target, depth ?? 4)));
app.MapGet("/api/map/callers", (string target, int? depth, CodeMapService map) => Results.Text(map.Callers(target, depth ?? 4)));

// Soru-cevap: ilerleme adımları ve cevap Server-Sent Events (SSE) ile akıtılır.
// Repoların otomatik eşitleme durumu ve elle tetikleme.
app.MapGet("/api/sync-status", (RepoSyncStatus status) => Results.Json(status.Snapshot()));
app.MapPost("/api/sync-now", (RepoSyncOptions opts, RepoSyncService sync) =>
{
    if (!opts.Enabled) return Results.Json(new { error = "Otomatik güncelleme kapalı (RepoSync:Enabled)." }, statusCode: 400);
    sync.TriggerNow();
    return Results.Json(new { started = true });
});

// Ekrandaki ihale tipi, konu ve cevap modu listeleri.
app.MapGet("/api/scopes", (ScopeService scopes) => new
{
    scopes = scopes.Scopes.Select(s => new { s.Id, s.Label }),
    topics = scopes.Topics.Select(t => new { t.Id, t.Label }),
    modes = answerModes.Select(m => new { m.Id, m.Label, m.Description, m.Verifies, isDefault = m == defaultMode }),
});

// Model çağırmadan kapsamın ne içerdiğini görmek için (kontrol amaçlı).
app.MapGet("/api/scope-preview", (string? scope, string? topic, ScopeService scopes) =>
{
    var s = scopes.Build(scope, topic);
    return Results.Text($"{s.ContextText}\nKapsamdaki dosya sayısı: {s.Files.Count}\n\n" +
                        string.Join("\n", s.Files.OrderBy(f => f).Take(300)));
});

// Model çağırmadan, verilen bir taslaktan kanıt paketinin nasıl oluşacağını görmek için (kontrol amaçlı).
// Model çağırmadan 1. aşamanın başlangıç paketini görmek için (kontrol amaçlı).
app.MapGet("/api/starter-preview", (string? scope, string? topic, EvidenceBuilder evidence, ScopeService scopes) =>
    Results.Text(evidence.BuildStarterPack(scopes.Build(scope, topic)) ?? "Konu seçilmediği için başlangıç paketi yok."));

app.MapPost("/api/evidence-preview", (EvidencePreviewRequest req, EvidenceBuilder evidence, ScopeService scopes) =>
    Results.Text(evidence.Build(req.Draft, scopes.Build(req.ScopeId, req.TopicId))));

// ---------------- Kural kataloğu ----------------

app.MapGet("/api/catalog/status", (CatalogService catalog) => new { configured = catalog.IsConfigured });

// Bir ihale tipi / konu için onaylı kurallar ve bugünkü koda göre güncel olup olmadıkları.
app.MapGet("/api/catalog", async (string? scope, string? topic, CatalogService catalog, SourceTracker tracker,
    ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured) return Results.Json(new { error = "Katalog yapılandırılmadı." }, statusCode: 503);
    try
    {
        var entries = await catalog.ListAsync(string.IsNullOrEmpty(scope) ? null : scope, topic, ct);
        return Results.Json(entries.Select(e =>
        {
            var f = tracker.Check(e);
            return new { e.Id, e.ScopeId, e.TopicId, e.Question, e.ApprovedBy, e.ApprovedAt, e.IsVerified, e.Mode, f.UpToDate, f.Changes };
        }));
    }
    catch (Exception ex) when (ex is CosmosException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
    {
        logger.LogError(ex, "Katalog listelenemedi");
        return Results.Json(new { error = "Kataloğa erişilemedi. Ayrıntılar sunucu loglarında." }, statusCode: 502);
    }
});

app.MapGet("/api/catalog/{scopeId}/{id}", async (string scopeId, string id, CatalogService catalog, SourceTracker tracker,
    ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured) return Results.Json(new { error = "Katalog yapılandırılmadı." }, statusCode: 503);
    try
    {
        var entry = await catalog.GetAsync(scopeId, id, ct);
        if (entry == null) return Results.NotFound();
        var f = tracker.Check(entry);
        return Results.Json(new { entry, f.UpToDate, f.Changes });
    }
    catch (Exception ex) when (ex is CosmosException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
    {
        logger.LogError(ex, "Katalog kaydı okunamadı");
        return Results.Json(new { error = "Kataloğa erişilemedi. Ayrıntılar sunucu loglarında." }, statusCode: 502);
    }
});

// Analistin onayladığı cevabı kataloğa kaydeder. Dayanaklar (metotlar ve parmak izleri) sunucuda hesaplanır.
app.MapPost("/api/catalog", async (SaveCatalogRequest req, CatalogService catalog, SourceTracker tracker,
    HttpContext http, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured) return Results.Json(new { error = "Katalog yapılandırılmadı." }, statusCode: 503);
    if (string.IsNullOrWhiteSpace(req.Question) || string.IsNullOrWhiteSpace(req.Answer))
        return Results.BadRequest(new { error = "Soru ve cevap boş olamaz." });

    // Giriş açıksa onaylayan, giriş yapan hesaptır (ekrandan gelen ad dikkate alınmaz; başkası adına onay verilemez).
    var approvedBy = authSettings.Enabled ? AuthSetup.DisplayName(http.User) : req.ApprovedBy;
    if (string.IsNullOrWhiteSpace(approvedBy))
        return Results.BadRequest(new { error = "Onaylayan kişinin adı zorunludur." });

    var entry = new CatalogEntry
    {
        ScopeId = string.IsNullOrEmpty(req.ScopeId) ? CatalogService.NoScope : req.ScopeId,
        TopicId = string.IsNullOrEmpty(req.TopicId) ? null : req.TopicId,
        Question = req.Question.Trim(),
        Answer = req.Answer,
        Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
        ApprovedBy = approvedBy.Trim(),
        ApprovedAt = DateTime.UtcNow,
        Mode = string.IsNullOrWhiteSpace(req.Mode) ? null : ResolveMode(req.Mode, false).Id,
        Verified = req.Verified ?? true,
        Sources = tracker.ExtractSources(req.Answer, req.ScopeId),
    };

    try
    {
        var saved = await catalog.SaveAsync(entry, ct);
        logger.LogInformation("Kataloğa kaydedildi: {Id} ({Scope}/{Topic}), {Sources} dayanak", saved.Id, saved.ScopeId, saved.TopicId, saved.Sources.Count);
        return Results.Json(new { saved.Id, saved.ScopeId, sources = saved.Sources.Count });
    }
    catch (Exception ex) when (ex is CosmosException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
    {
        logger.LogError(ex, "Kataloğa kaydedilemedi");
        return Results.Json(new { error = "Kataloğa kaydedilemedi. Ayrıntılar sunucu loglarında." }, statusCode: 502);
    }
});

// Katalogda metin araması (soru, cevap, not, onaylayan). Model kullanılmaz. Seçili ihale tipi / konu varsa
// onların içinde, yoksa tüm katalogda aranır. Aranan kelimelerin hepsi geçmelidir.
app.MapGet("/api/catalog/search", async (string q, string? scope, string? topic, CatalogService catalog,
    SourceTracker tracker, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured) return Results.Json(new { error = "Katalog yapılandırılmadı." }, statusCode: 503);
    var terms = QuestionMatcher.SearchTerms(q ?? "");
    if (terms.Count == 0) return Results.Json(Array.Empty<object>());
    try
    {
        var entries = await catalog.ListAsync(string.IsNullOrEmpty(scope) ? null : scope, topic, ct);
        var results = entries
            .Select(e => (Entry: e, Text: $"{e.Question}\n{e.Answer}\n{e.Note}\n{e.ApprovedBy}"))
            .Where(x => QuestionMatcher.ContainsAllTerms(x.Text, terms))
            // Sorusunda geçenler önce gelir.
            .OrderByDescending(x => QuestionMatcher.ContainsAllTerms(x.Entry.Question, terms))
            .ThenByDescending(x => x.Entry.ApprovedAt)
            .Take(50)
            .Select(x =>
            {
                var f = tracker.Check(x.Entry);
                return new
                {
                    x.Entry.Id, x.Entry.ScopeId, x.Entry.TopicId, x.Entry.Question, x.Entry.ApprovedBy, x.Entry.ApprovedAt,
                    x.Entry.IsVerified, x.Entry.Mode,
                    f.UpToDate, f.Changes,
                    snippet = QuestionMatcher.ContainsAllTerms(x.Entry.Question, terms)
                        ? null
                        : QuestionMatcher.Snippet($"{x.Entry.Answer}\n{x.Entry.Note}", terms),
                };
            });
        return Results.Json(results);
    }
    catch (Exception ex) when (ex is CosmosException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
    {
        logger.LogError(ex, "Katalogda arama yapılamadı");
        return Results.Json(new { error = "Kataloğa erişilemedi. Ayrıntılar sunucu loglarında." }, statusCode: 502);
    }
});

// "Sor" düğmesine basıldığında, modeli çağırmadan ÖNCE katalogda aynı veya benzer onaylı soru aranır.
// Aynı ihale tipi içinde aranır; konu seçildiyse o konudakiler ve konusu belirtilmemiş kayıtlar dahil edilir.
// Benzerlik soru metniyle ve onaylı cevabın metniyle birlikte hesaplanır (model kullanılmaz).
// - exact: birebir aynı soru → doğrudan açılır.
// - best: benzerliği Catalog:AutoOpenThreshold üstünde ve kodu değişmemiş en iyi kayıt → doğrudan açılır.
// - similar: diğer benzer kayıtlar → öneri listesi.
app.MapPost("/api/catalog/match", async (CatalogMatchRequest req, CatalogService catalog, CatalogOptions catalogOptions,
    SourceTracker tracker, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured || string.IsNullOrWhiteSpace(req.Question))
        return Results.Json(new { exact = (object?)null, best = (object?)null, similar = Array.Empty<object>() });
    try
    {
        var scopeId = string.IsNullOrEmpty(req.ScopeId) ? CatalogService.NoScope : req.ScopeId;
        var entries = (await catalog.ListAsync(scopeId, null, ct))
            .Where(e => string.IsNullOrEmpty(req.TopicId) || e.TopicId == null || e.TopicId == req.TopicId)
            .ToList();

        object Summary(CatalogEntry e, double score, CatalogFreshness f) => new
        {
            e.Id, e.ScopeId, e.TopicId, e.Question, e.ApprovedBy, e.ApprovedAt, e.IsVerified, e.Mode,
            f.UpToDate, score = Math.Round(score, 2),
        };

        var exact = entries.FirstOrDefault(e => QuestionMatcher.IsSame(e.Question, req.Question));
        var ranked = entries
            .Where(e => e != exact)
            .Select(e => (Entry: e, Score: QuestionMatcher.Score(req.Question, e.Question, e.Answer)))
            .Where(x => x.Score >= QuestionMatcher.SimilarThreshold)
            .OrderByDescending(x => x.Score)
            .Take(4)
            .Select(x => (x.Entry, x.Score, Fresh: tracker.Check(x.Entry)))
            .ToList();

        // En iyi eşleşme yeterince yakın ve kodu değişmemişse model çağrılmadan doğrudan açılır.
        var top = ranked.FirstOrDefault();
        var best = exact == null && top.Entry != null && top.Score >= catalogOptions.AutoOpenThreshold && top.Fresh.UpToDate
            ? Summary(top.Entry, top.Score, top.Fresh)
            : null;
        var similar = ranked
            .Where(x => best == null || x.Entry != top.Entry)
            .Take(3)
            .Select(x => Summary(x.Entry, x.Score, x.Fresh))
            .ToList();

        return Results.Json(new { exact = exact == null ? null : Summary(exact, 1, tracker.Check(exact)), best, similar });
    }
    catch (Exception ex) when (ex is CosmosException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
    {
        // Katalog erişilemiyorsa soru yine de sorulabilsin: eşleşme yok gibi davran.
        logger.LogError(ex, "Katalog eşleştirmesi yapılamadı");
        return Results.Json(new { exact = (object?)null, best = (object?)null, similar = Array.Empty<object>(), error = "Kataloğa erişilemedi." });
    }
});

// Model ve Cosmos kullanmadan: bir cevaptan hangi dayanakların çıkarılacağını gösterir (kontrol amaçlı).
app.MapPost("/api/catalog/sources-preview", (SourcesPreviewRequest req, SourceTracker tracker) =>
    tracker.ExtractSources(req.Answer, req.ScopeId).Select(s => new { s.Kind, s.Label, s.Repo, s.Path, s.StartLine, s.EndLine }));

// Soru-cevap. İnceleme tarayıcı bağlantısından bağımsız arka planda çalışır (AskJobStore); ilerleme adımları ve
// cevap Server-Sent Events (SSE) ile akıtılır. Bağlantı koparsa tarayıcı GET /api/ask/{id}/stream ile kaldığı
// yerden devam eder; sayfa yenilenirse aynı adresle tüm olaylar baştan gelir.
app.MapPost("/api/ask", async (AskRequest request, AnalystAgent agent, ScopeService scopes, AskJobStore jobs,
    HttpContext http, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var job = jobs.Create();
    var question = request.Question.Trim();
    var mode = ResolveMode(request.Mode, request.FastMode);
    var scope = scopes.Build(request.ScopeId, request.TopicId);
    logger.LogInformation("İnceleme başladı {Id} | Mod: {Mode}", job.Id, mode.Id);

    _ = Task.Run(async () =>
    {
        try
        {
            await agent.RunAsync(question, scope, mode, e => { job.Add(e); return Task.CompletedTask; }, job.Cancellation.Token);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            job.Add(new AgentEvent("error", "İnceleme durduruldu: sayfa uzun süre kapalı kaldı. Soruyu yeniden sorabilirsiniz."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Soru yanıtlanırken hata oluştu ({Id})", job.Id);
            job.Add(new AgentEvent("error", "Soru yanıtlanırken bir hata oluştu. Ayrıntılar sunucu loglarında."));
        }
        finally
        {
            job.Complete();
        }
    });

    await AskJobStore.StreamAsync(job, 0, http);
});

// Kopan bağlantıyı sürdürmek veya sayfa yenilenince cevabı geri getirmek için: "after" sırasından itibaren olaylar.
app.MapGet("/api/ask/{id}/stream", async (string id, int? after, AskJobStore jobs, HttpContext http) =>
{
    var job = jobs.Get(id);
    if (job == null)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        await http.Response.WriteAsJsonAsync(new { error = "İnceleme bulunamadı (süresi dolmuş veya sunucu yeniden başlamış olabilir)." });
        return;
    }
    await AskJobStore.StreamAsync(job, after ?? 0, http);
});

app.Run();

// FastMode eski ekranlarla uyumluluk için duruyor; yeni ekran Mode ("fast" / "balanced" / "deep") gönderir.
public sealed record AskRequest(string Question, string? ScopeId = null, string? TopicId = null, bool FastMode = false, string? Mode = null);
public sealed record EvidencePreviewRequest(string Draft, string? ScopeId = null, string? TopicId = null);
public sealed record SaveCatalogRequest(string Question, string Answer, string? ApprovedBy, string? ScopeId = null, string? TopicId = null,
    string? Note = null, string? Mode = null, bool? Verified = null);
public sealed record SourcesPreviewRequest(string Answer, string? ScopeId = null);
public sealed record CatalogMatchRequest(string Question, string? ScopeId = null, string? TopicId = null);
