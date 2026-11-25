using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace KodAnalist;

/// <summary>Kataloğa kaydedilmiş, analist tarafından onaylanmış bir iş kuralı cevabı.</summary>
public sealed class CatalogEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Bölümleme anahtarı: ihale tipi (seçilmediyse "genel").</summary>
    [JsonPropertyName("scopeId")] public string ScopeId { get; set; } = CatalogService.NoScope;

    [JsonPropertyName("topicId")] public string? TopicId { get; set; }
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("answer")] public string Answer { get; set; } = "";
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("approvedBy")] public string ApprovedBy { get; set; } = "";
    [JsonPropertyName("approvedAt")] public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cevabın dayandığı kod parçaları ve onay anındaki parmak izleri.</summary>
    [JsonPropertyName("sources")] public List<SourceRef> Sources { get; set; } = [];
}

/// <summary>Cevabın dayandığı bir kod parçası.</summary>
public sealed class SourceRef
{
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>"method": bir metodun tamamı (satır kayması sorun olmaz). "range": metot dışında kalan satır aralığı.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "method";

    [JsonPropertyName("methodId")] public string? MethodId { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("startLine")] public int StartLine { get; set; }
    [JsonPropertyName("endLine")] public int EndLine { get; set; }

    /// <summary>İçeriğin SHA-256 parmak izi (boşluklar normalize edilerek).</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
}

/// <summary>Bir katalog kaydının bugünkü koda göre durumu.</summary>
public sealed record CatalogFreshness(bool UpToDate, List<string> Changes);

/// <summary>
/// Cevaptaki "dosya:satır" referanslarından dayanakları çıkarır ve kod değişip değişmediğini kontrol eder.
/// Model kullanmaz. Metot içindeki referanslar metodun tamamıyla izlenir: dosyanın başka yerine kod eklenip
/// satırlar kaysa bile kayıt yanlışlıkla "değişti" görünmez; yalnızca kuralın dayandığı metot değişirse işaretlenir.
/// </summary>
public sealed partial class SourceTracker(CodeTools tools, CodeMapService codeMap, ScopeService scopes)
{
    [GeneratedRegex(@"(?<path>[A-Za-z0-9_\-./]+\.(?:cs|jsx?|tsx?))(?::(?<a>\d+)(?:\s*[-–]\s*(?<b>\d+))?)")]
    private static partial Regex FileRefRegex();

    // `MetotAdi`, `Sinif.MetotAdi` veya `MetotAdi()` biçiminde ters tırnak içinde geçen adlar.
    [GeneratedRegex(@"`(?<id>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)(?:\(\))?`")]
    private static partial Regex IdentifierRegex();

    private const int MinUnqualifiedIdentifierLength = 8;

    public List<SourceRef> ExtractSources(string answer, string? scopeId = null)
    {
        var queries = codeMap.Current?.Queries;
        var result = new List<SourceRef>();
        var seen = new HashSet<string>();

        // 1) Cevapta adı geçen metotlar. Kapsam seçiliyse yalnızca kapsamdakiler alınır; aynı adlı çok sayıda
        //    metot varsa (ör. her ihale tipinde) belirsiz olduğu için alınmaz.
        if (queries != null)
        {
            var scope = scopes.Build(scopeId == CatalogService.NoScope ? null : scopeId, null);
            foreach (var id in IdentifierRegex().Matches(answer).Select(m => m.Groups["id"].Value).Distinct())
            {
                var qualified = id.Contains('.');
                if (!qualified && id.Length < MinUnqualifiedIdentifierLength) continue;
                var candidates = queries.FindMethods(id);
                if (scope.HasFileScope)
                    candidates = candidates.Where(c => scope.Files.Contains($"{c.Repo}|{c.File}")).ToList();
                if (candidates.Count == 0 || candidates.Count > 2) continue;

                foreach (var method in candidates)
                {
                    var lines = tools.ReadLines(method.Repo, method.File);
                    if (lines == null || method.EndLine > lines.Length || !seen.Add(method.Id)) continue;
                    result.Add(new SourceRef
                    {
                        Repo = method.Repo, Path = method.File, Kind = "method", MethodId = method.Id,
                        Label = $"{method.Type}.{method.Name}",
                        StartLine = method.StartLine, EndLine = method.EndLine,
                        Hash = Hash(lines, method.StartLine, method.EndLine),
                    });
                }
            }
        }

        // 2) "dosya:satır" referansları

        foreach (Match m in FileRefRegex().Matches(answer))
        {
            var resolved = tools.ResolveFile(m.Groups["path"].Value);
            if (resolved == null) continue;
            var (repo, path) = resolved.Value;
            var lines = tools.ReadLines(repo, path);
            if (lines == null) continue;

            var a = int.Parse(m.Groups["a"].Value);
            var b = m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value) : a;
            if (b < a) (a, b) = (b, a);
            if (a < 1 || a > lines.Length) continue;
            b = Math.Min(b, lines.Length);

            var method = queries?.MethodAt(repo, path, a);
            if (method != null && b <= method.EndLine)
            {
                if (!seen.Add(method.Id)) continue;
                result.Add(new SourceRef
                {
                    Repo = repo, Path = path, Kind = "method", MethodId = method.Id,
                    Label = $"{method.Type}.{method.Name}",
                    StartLine = method.StartLine, EndLine = method.EndLine,
                    Hash = Hash(lines, method.StartLine, method.EndLine),
                });
            }
            else
            {
                if (!seen.Add($"{repo}|{path}|{a}-{b}")) continue;
                result.Add(new SourceRef
                {
                    Repo = repo, Path = path, Kind = "range",
                    Label = $"{System.IO.Path.GetFileName(path)}:{a}-{b}",
                    StartLine = a, EndLine = b,
                    Hash = Hash(lines, a, b),
                });
            }
        }
        return result;
    }

    public CatalogFreshness Check(CatalogEntry entry)
    {
        var queries = codeMap.Current?.Queries;
        var changes = new List<string>();

        foreach (var s in entry.Sources)
        {
            var lines = tools.ReadLines(s.Repo, s.Path);
            if (lines == null)
            {
                changes.Add($"{s.Label}: dosya bulunamadı ({s.Repo}/{s.Path})");
                continue;
            }

            if (s.Kind == "method" && s.MethodId != null)
            {
                var method = queries?.MethodById(s.MethodId);
                if (method == null)
                {
                    changes.Add($"{s.Label}: metot bulunamadı (silinmiş, taşınmış veya kod haritası eski)");
                    continue;
                }
                if (method.EndLine > lines.Length || Hash(lines, method.StartLine, method.EndLine) != s.Hash)
                    changes.Add($"{s.Label}: metodun içeriği değişti ({s.Repo}/{s.Path}:{method.StartLine})");
            }
            else if (s.EndLine > lines.Length || Hash(lines, s.StartLine, s.EndLine) != s.Hash)
            {
                changes.Add($"{s.Label}: satırların içeriği değişti");
            }
        }
        return new CatalogFreshness(changes.Count == 0, changes);
    }

    private static string Hash(string[] lines, int start, int end)
    {
        var text = string.Join("\n", lines[(start - 1)..end].Select(l => l.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>
/// Sorulan soruyu katalogdaki onaylı sorularla karşılaştırır. Model kullanmaz.
/// - Aynı: büyük/küçük harf, noktalama ve boşluk farkları dışında birebir aynı soru.
/// - Benzer: anlamlı kelimelerin örtüşmesi yüksek olan soru (Türkçe ekler için kelime kökü yaklaşık alınır).
/// </summary>
public static partial class QuestionMatcher
{
    public const double SimilarThreshold = 0.5;

    private static readonly System.Globalization.CultureInfo Tr = new("tr-TR");

    // Soru kalıpları ve bağlaçlar: anlamı belirlemedikleri için karşılaştırmaya katılmaz.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ve", "veya", "ile", "bir", "bu", "şu", "o", "da", "de", "mi", "mı", "mu", "mü", "ne", "neler", "nedir",
        "nelerdir", "nasıl", "nasil", "hangi", "hangisi", "için", "icin", "olan", "olarak", "gibi", "kadar", "çalışır",
        "calisir", "işler", "isler", "olur", "var", "yok", "ise", "ki", "en", "daha", "her", "tüm", "tum", "sırasıyla",
        "anlat", "açıkla", "acikla", "nedir?", "midir", "mıdır",
    };

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s/]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Türkçe küçültmede "I" → "ı" olur ("MIN" → "mın"); karşılaştırmada ı/i farkı yok sayılır.
    public static string Normalize(string text) =>
        Spaces().Replace(Punctuation().Replace(text.ToLower(Tr).Replace('ı', 'i'), " "), " ").Trim();

    /// <summary>Kelime kökü yaklaşığı: Türkçe ekler (-ları, -nde, -ının...) için ilk 5 harf.</summary>
    private static HashSet<string> Terms(string text) =>
        Normalize(text).Replace("/", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Select(w => w.Length > 5 ? w[..5] : w)
            .ToHashSet(StringComparer.Ordinal);

    public static bool IsSame(string a, string b) => Normalize(a) == Normalize(b);

    /// <summary>
    /// Katalog metin araması: aranan kelimelerin HEPSİ metinde geçmeli (büyük/küçük harf, ı/i ve noktalama farkı yok sayılır).
    /// </summary>
    public static bool ContainsAllTerms(string text, IReadOnlyList<string> normalizedTerms)
    {
        var normalized = Normalize(text);
        return normalizedTerms.All(t => normalized.Contains(t, StringComparison.Ordinal));
    }

    public static List<string> SearchTerms(string query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).Distinct().ToList();

    /// <summary>Metinde ilk eşleşmenin çevresinden kısa bir alıntı (ekranda gösterilir).</summary>
    public static string? Snippet(string text, IReadOnlyList<string> normalizedTerms, int radius = 90)
    {
        // Normalize, ı→i ve küçültme dışında karakter sayısını korumadığı için eşleşmeyi orijinal metinde
        // Türkçe kültürüyle büyük/küçük harf duyarsız arıyoruz.
        var compare = Tr.CompareInfo;
        foreach (var term in normalizedTerms)
        {
            var index = compare.IndexOf(text, term, System.Globalization.CompareOptions.IgnoreCase);
            if (index < 0) index = compare.IndexOf(text, term.Replace('i', 'ı'), System.Globalization.CompareOptions.IgnoreCase);
            if (index < 0) continue;
            var start = Math.Max(0, index - radius);
            var end = Math.Min(text.Length, index + term.Length + radius);
            // Markdown işaretlerini (#, *, `, >, |) alıntıdan temizle.
            var snippet = Spaces().Replace(Regex.Replace(text[start..end], @"[#*`>|]+", " "), " ").Trim();
            return (start > 0 ? "… " : "") + snippet + (end < text.Length ? " …" : "");
        }
        return null;
    }

    /// <summary>0-1 arası benzerlik (Jaccard): ortak anlamlı kelime / toplam anlamlı kelime.</summary>
    public static double Similarity(string a, string b)
    {
        var ta = Terms(a);
        var tb = Terms(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var common = ta.Count(tb.Contains);
        return (double)common / (ta.Count + tb.Count - common);
    }
}

/// <summary>Kural kataloğunu Azure Cosmos DB'de saklar.</summary>
public sealed class CatalogService(CatalogOptions options, ILogger<CatalogService> logger) : IDisposable
{
    public const string NoScope = "genel";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CosmosClient? _client;
    private Container? _container;

    public bool IsConfigured => options.IsConfigured;

    public async Task<List<CatalogEntry>> ListAsync(string? scopeId, string? topicId, CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE (@topic = null OR c.topicId = @topic) ORDER BY c.approvedAt DESC")
            .WithParameter("@topic", string.IsNullOrEmpty(topicId) ? null : topicId);

        var requestOptions = new QueryRequestOptions();
        if (!string.IsNullOrEmpty(scopeId))
            requestOptions.PartitionKey = new PartitionKey(scopeId);

        var result = new List<CatalogEntry>();
        using var iterator = container.GetItemQueryIterator<CatalogEntry>(query, requestOptions: requestOptions);
        while (iterator.HasMoreResults)
            result.AddRange(await iterator.ReadNextAsync(ct));
        return result;
    }

    public async Task<CatalogEntry?> GetAsync(string scopeId, string id, CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        try
        {
            return (await container.ReadItemAsync<CatalogEntry>(id, new PartitionKey(scopeId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<CatalogEntry> SaveAsync(CatalogEntry entry, CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        return (await container.UpsertItemAsync(entry, new PartitionKey(entry.ScopeId), cancellationToken: ct)).Resource;
    }

    private async Task<Container> GetContainerAsync(CancellationToken ct)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Katalog yapılandırılmadı (Catalog:Endpoint boş).");
        if (_container != null) return _container;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_container != null) return _container;

            var clientOptions = new CosmosClientOptions
            {
                ApplicationName = "KodAnalist",
                UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(),
            };
            _client = string.IsNullOrWhiteSpace(options.Key)
                ? new CosmosClient(options.Endpoint, new DefaultAzureCredential(), clientOptions)
                : new CosmosClient(options.Endpoint, options.Key, clientOptions);

            try
            {
                // Anahtarla bağlanıldığında veritabanı ve kapsayıcı yoksa oluşturulur.
                // (Entra ID ile bu işlem yetki gerektirir; yoksa Azure'da önceden oluşturulmuş olmalıdır.)
                Database db = await _client.CreateDatabaseIfNotExistsAsync(options.Database, cancellationToken: ct);
                _container = await db.CreateContainerIfNotExistsAsync(options.Container, "/scopeId", cancellationToken: ct);
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                logger.LogWarning("Cosmos veritabanı/kapsayıcı oluşturulamadı ({Status}); var olduğu varsayılıyor.", ex.StatusCode);
                _container = _client.GetContainer(options.Database, options.Container);
            }

            logger.LogInformation("Kural kataloğu bağlandı: {Database}/{Container}", options.Database, options.Container);
            return _container;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose() => _client?.Dispose();
}
