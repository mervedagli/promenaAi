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

// API anahtarı ayar dosyasında değil, ortam değişkeninde / gizli bilgi deposunda tutulur.
foundry.ApiKey ??= Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_API_KEY");

if (string.IsNullOrWhiteSpace(foundry.ResourceName))
    throw new InvalidOperationException("Foundry:ResourceName ayarı boş. appsettings.Local.json dosyasını doldurun (örnek: appsettings.Local.example.json).");

foreach (var repo in repos)
{
    // Göreli yollar proje klasörüne (src/KodAnalist) göre çözülür; KodHaritasi da aynı kuralı kullanır.
    repo.Path = Path.GetFullPath(repo.Path, builder.Environment.ContentRootPath);
    if (!Directory.Exists(repo.Path))
        throw new InvalidOperationException(
            $"'{repo.Name}' deposu bulunamadı: {repo.Path}. Yolu appsettings.Local.json dosyasında düzeltin " +
            "(örnek: appsettings.Local.example.json).");
}

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

var app = builder.Build();

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
// Ekrandaki ihale tipi ve konu listeleri.
app.MapGet("/api/scopes", (ScopeService scopes) => new
{
    scopes = scopes.Scopes.Select(s => new { s.Id, s.Label }),
    topics = scopes.Topics.Select(t => new { t.Id, t.Label }),
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
            return new { e.Id, e.ScopeId, e.TopicId, e.Question, e.ApprovedBy, e.ApprovedAt, f.UpToDate, f.Changes };
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
    ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured) return Results.Json(new { error = "Katalog yapılandırılmadı." }, statusCode: 503);
    if (string.IsNullOrWhiteSpace(req.Question) || string.IsNullOrWhiteSpace(req.Answer))
        return Results.BadRequest(new { error = "Soru ve cevap boş olamaz." });
    if (string.IsNullOrWhiteSpace(req.ApprovedBy))
        return Results.BadRequest(new { error = "Onaylayan kişinin adı zorunludur." });

    var entry = new CatalogEntry
    {
        ScopeId = string.IsNullOrEmpty(req.ScopeId) ? CatalogService.NoScope : req.ScopeId,
        TopicId = string.IsNullOrEmpty(req.TopicId) ? null : req.TopicId,
        Question = req.Question.Trim(),
        Answer = req.Answer,
        Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
        ApprovedBy = req.ApprovedBy.Trim(),
        ApprovedAt = DateTime.UtcNow,
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

// "Sor" düğmesine basıldığında, modeli çağırmadan ÖNCE katalogda aynı veya benzer onaylı soru aranır.
// Aynı ihale tipi içinde aranır; konu seçildiyse o konudakiler ve konusu belirtilmemiş kayıtlar dahil edilir.
app.MapPost("/api/catalog/match", async (CatalogMatchRequest req, CatalogService catalog, SourceTracker tracker,
    ILogger<Program> logger, CancellationToken ct) =>
{
    if (!catalog.IsConfigured || string.IsNullOrWhiteSpace(req.Question))
        return Results.Json(new { exact = (object?)null, similar = Array.Empty<object>() });
    try
    {
        var scopeId = string.IsNullOrEmpty(req.ScopeId) ? CatalogService.NoScope : req.ScopeId;
        var entries = (await catalog.ListAsync(scopeId, null, ct))
            .Where(e => string.IsNullOrEmpty(req.TopicId) || e.TopicId == null || e.TopicId == req.TopicId)
            .ToList();

        object Summary(CatalogEntry e, double score)
        {
            var f = tracker.Check(e);
            return new { e.Id, e.ScopeId, e.TopicId, e.Question, e.ApprovedBy, e.ApprovedAt, f.UpToDate, score = Math.Round(score, 2) };
        }

        var exact = entries.FirstOrDefault(e => QuestionMatcher.IsSame(e.Question, req.Question));
        var similar = entries
            .Where(e => e != exact)
            .Select(e => (Entry: e, Score: QuestionMatcher.Similarity(e.Question, req.Question)))
            .Where(x => x.Score >= QuestionMatcher.SimilarThreshold)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => Summary(x.Entry, x.Score))
            .ToList();

        return Results.Json(new { exact = exact == null ? null : Summary(exact, 1), similar });
    }
    catch (Exception ex) when (ex is CosmosException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
    {
        // Katalog erişilemiyorsa soru yine de sorulabilsin: eşleşme yok gibi davran.
        logger.LogError(ex, "Katalog eşleştirmesi yapılamadı");
        return Results.Json(new { exact = (object?)null, similar = Array.Empty<object>(), error = "Kataloğa erişilemedi." });
    }
});

// Model ve Cosmos kullanmadan: bir cevaptan hangi dayanakların çıkarılacağını gösterir (kontrol amaçlı).
app.MapPost("/api/catalog/sources-preview", (SourcesPreviewRequest req, SourceTracker tracker) =>
    tracker.ExtractSources(req.Answer, req.ScopeId).Select(s => new { s.Kind, s.Label, s.Repo, s.Path, s.StartLine, s.EndLine }));

app.MapPost("/api/ask", async (AskRequest request, AnalystAgent agent, ScopeService scopes, HttpContext http, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Emit(AgentEvent e)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(e)}\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    try
    {
        var scope = scopes.Build(request.ScopeId, request.TopicId);
        await agent.RunAsync(request.Question.Trim(), scope, request.FastMode, Emit, http.RequestAborted);
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        // Kullanıcı sayfayı kapattı veya isteği iptal etti.
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Soru yanıtlanırken hata oluştu");
        await Emit(new AgentEvent("error", "Soru yanıtlanırken bir hata oluştu. Ayrıntılar sunucu loglarında."));
    }
});

app.Run();

public sealed record AskRequest(string Question, string? ScopeId = null, string? TopicId = null, bool FastMode = false);
public sealed record EvidencePreviewRequest(string Draft, string? ScopeId = null, string? TopicId = null);
public sealed record SaveCatalogRequest(string Question, string Answer, string ApprovedBy, string? ScopeId = null, string? TopicId = null, string? Note = null);
public sealed record SourcesPreviewRequest(string Answer, string? ScopeId = null);
public sealed record CatalogMatchRequest(string Question, string? ScopeId = null, string? TopicId = null);
