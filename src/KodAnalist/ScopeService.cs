using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using KodHaritasi;

namespace KodAnalist;

/// <summary>
/// Akışın bir giriş noktası. <see cref="Target"/> map_trace'e verilebilen değerdir: bir adres
/// ("/api/X/Y") veya "Sinif.Metot". <see cref="Label"/> modele gösterilen açıklamadır.
/// </summary>
public sealed record EntryTarget(string Target, string Label);

/// <summary>Bir soru için hesaplanmış kapsam: ilgili dosyalar, çekirdek sınıflar ve akış giriş noktaları.</summary>
public sealed class AnalysisScope
{
    public ScopeDefinition? Scope { get; init; }
    public TopicDefinition? Topic { get; init; }

    /// <summary>"repo|dosya/yolu" biçiminde kapsamdaki dosyalar. Aramalar önce bu dosyalarda yapılır.</summary>
    public HashSet<string> Files { get; init; } = new(StringComparer.Ordinal);

    public List<string> CoreTypes { get; init; } = [];
    public List<EntryTarget> EntryTargets { get; init; } = [];

    /// <summary>İhale tipi seçildi: kanıt paketi ve dayanaklar diğer ihale tiplerine ait kodu dışarıda bırakır.</summary>
    public bool HasFileScope => Scope != null && Files.Count > 0;

    /// <summary>Aramalar önce kapsam dosyalarında yapılsın mı (ihale tipi veya odak klasörlü konu seçildiyse).</summary>
    public bool HasSearchScope => Files.Count > 0;

    /// <summary>Modele verilecek kısa bağlam metni.</summary>
    public string ContextText { get; init; } = "";
}

/// <summary>
/// Ekranda seçilen ihale tipi ve konuya göre kod haritasından kapsam hesaplar.
/// İhale tipi: çekirdek sınıflar (ör. AggregatedAuction*) + taban sınıfları + çağrı zincirindeki dosyalar + ekranlar.
/// Konu: giriş noktaları (adres kalıbı, başlangıç sınıfı kalıbı) ve odak klasörü (ör. Promena.Schedule, ProSource.API).
/// </summary>
public sealed class ScopeService(
    CodeMapService codeMap, List<ScopeDefinition> scopes, List<TopicDefinition> topics, ILogger<ScopeService> logger)
{
    private const int CallDepth = 3;
    private const int MaxMethods = 4000;
    private const int MaxEntryTargets = 15;
    private const string DefaultEntryMethodPattern = "^(Run|Execute|Process|Handle)$";

    private readonly ConcurrentDictionary<string, (DateTime Version, AnalysisScope Scope)> _cache = new();

    public IReadOnlyList<ScopeDefinition> Scopes => scopes;
    public IReadOnlyList<TopicDefinition> Topics => topics;

    public AnalysisScope Build(string? scopeId, string? topicId)
    {
        var scope = scopes.FirstOrDefault(s => s.Id == scopeId);
        var topic = topics.FirstOrDefault(t => t.Id == topicId);
        if (scope == null && topic == null) return new AnalysisScope();

        var current = codeMap.Current;
        if (current == null)
        {
            // Harita yoksa yalnızca metin bağlamı verilir.
            return new AnalysisScope { Scope = scope, Topic = topic, ContextText = BuildContext(scope, topic, [], [], 0) };
        }

        var (map, _, version) = current.Value;
        var key = $"{scope?.Id}|{topic?.Id}";
        if (_cache.TryGetValue(key, out var cached) && cached.Version == version) return cached.Scope;

        var result = Compute(map, scope, topic);
        _cache[key] = (version, result);
        logger.LogInformation("Kapsam hesaplandı ({Key}): {Files} dosya, {Types} çekirdek sınıf, {Entries} giriş noktası",
            key, result.Files.Count, result.CoreTypes.Count, result.EntryTargets.Count);
        return result;
    }

    private AnalysisScope Compute(CodeMap map, ScopeDefinition? scope, TopicDefinition? topic)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var methodsById = map.Methods.ToDictionary(m => m.Id);
        var typeRegex = scope != null ? new Regex(scope.TypePattern) : null;
        var coreTypes = new List<TypeNode>();
        Func<MethodNode, bool> excluded = _ => false;

        // ---------------- İhale tipi ----------------
        if (typeRegex != null)
        {
            coreTypes = map.Types.Where(t => typeRegex.IsMatch(t.Name)).ToList();

            // Diğer ihale tiplerine ait sınıflar (ör. Grup Toplamlı seçiliyken DutchAuction*). Ortak adres
            // yardımcıları tüm tiplere bağlandığı için çağrı zinciri bunlara da uzanır; kapsamdan çıkarılırlar.
            var otherTypeRegexes = scopes.Where(s => s.Id != scope!.Id && !string.IsNullOrWhiteSpace(s.TypePattern))
                .Select(s => new Regex(s.TypePattern)).ToList();

            // Taban sınıflar ve arayüzler (ör. AggregatedAuctionBidService → AuctionBidBaseService)
            var typesByName = map.Types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToList());
            var queue = new Queue<TypeNode>(coreTypes);
            var seenTypes = new HashSet<TypeNode>(coreTypes);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                files.Add($"{t.Repo}|{t.File}");
                foreach (var bt in t.BaseTypes)
                {
                    if (!typesByName.TryGetValue(SimpleTypeName(bt), out var bases)) continue;
                    foreach (var b in bases.Where(b => b.Repo == t.Repo && seenTypes.Add(b)))
                        queue.Enqueue(b);
                }
            }

            // Taban sınıflar (ortak kod) diğer tiplerin kalıbına uysa bile kapsamda kalır.
            var baseTypeNames = seenTypes.Select(t => (t.Repo, t.Name)).ToHashSet();
            excluded = m => !baseTypeNames.Contains((m.Repo, m.Type))
                            && !typeRegex.IsMatch(m.Type) && otherTypeRegexes.Any(r => r.IsMatch(m.Type));

            var coreNames = coreTypes.Select(t => (t.Repo, t.Name)).ToHashSet();
            AddCallClosure(map.Methods.Where(m => coreNames.Contains((m.Repo, m.Type))), files, methodsById, excluded);

            // Ekran dosyaları: yol kalıbıyla eşleşenler + çekirdek controller adreslerini çağıranlar
            var frontendRegex = string.IsNullOrWhiteSpace(scope!.FrontendPattern) ? null : new Regex(scope.FrontendPattern);
            var coreMethodIds = map.Methods.Where(m => coreNames.Contains((m.Repo, m.Type))).Select(m => m.Id).ToHashSet();
            foreach (var fc in map.FrontendCalls)
            {
                if ((frontendRegex != null && frontendRegex.IsMatch(fc.File)) || fc.EndpointMethodIds.Any(coreMethodIds.Contains))
                    files.Add($"{fc.Repo}|{fc.File}");
            }
        }

        // ---------------- Konu ----------------
        var entryTargets = new List<EntryTarget>();
        if (topic != null)
        {
            var keyword = scope?.Keyword;

            // a) Odak klasörü: bu klasördeki dosyalar aramada önceliklidir (ör. Promena.Schedule, ProSource.API).
            Regex? focusRegex = string.IsNullOrWhiteSpace(topic.FocusPathPattern) ? null : new Regex(topic.FocusPathPattern);
            if (focusRegex != null)
            {
                foreach (var f in map.Types.Select(t => (t.Repo, t.File))
                             .Concat(map.Methods.Select(m => (m.Repo, m.File)))
                             .Distinct()
                             .Where(x => focusRegex.IsMatch($"{x.Repo}/{x.File}")))
                    files.Add($"{f.Repo}|{f.File}");
            }

            // b) Adres kalıbı: seçili ihale tipinin controller'larındaki giriş adresleri (ör. SendBid).
            if (!string.IsNullOrWhiteSpace(topic.ActionPattern))
            {
                var actionRegex = new Regex(topic.ActionPattern);
                entryTargets.AddRange(map.Endpoints
                    .Where(e => methodsById.TryGetValue(e.MethodId, out var m)
                                && actionRegex.IsMatch(m.Name)
                                && (typeRegex == null || typeRegex.IsMatch(m.Type))
                                && e.Route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                                && (focusRegex == null || topic.EntryTypePattern != null || focusRegex.IsMatch($"{m.Repo}/{m.File}")))
                    .Select(e => new EntryTarget(e.Route, $"{e.HttpMethod} {e.Route}")));
            }

            // c) Başlangıç sınıfı kalıbı: API adresi olmayan giriş noktaları (ör. zamanlanmış görevler).
            //    "{Keyword}" seçili ihale tipinin anahtar kelimesiyle değiştirilir (ör. Generate{Keyword}ReportProcess).
            if (!string.IsNullOrWhiteSpace(topic.EntryTypePattern))
            {
                var typePattern = topic.EntryTypePattern.Replace("{Keyword}", keyword != null ? Regex.Escape(keyword) : "\\w*");
                var entryTypeRegex = new Regex(typePattern);
                var methodRegex = new Regex(topic.EntryMethodPattern ?? DefaultEntryMethodPattern);
                var entryMethods = map.Methods
                    .Where(m => entryTypeRegex.IsMatch(m.Type) && methodRegex.IsMatch(m.Name))
                    .Take(MaxEntryTargets)
                    .ToList();
                entryTargets.AddRange(entryMethods.Select(m => new EntryTarget($"{m.Type}.{m.Name}", $"{m.Type}.{m.Name} ({m.Repo}/{m.File})")));
                AddCallClosure(entryMethods, files, methodsById, excluded);
            }

            // d) Yalnızca odak klasörü verilmişse: o klasördeki, seçili ihale tipiyle ilgili adresler giriş noktasıdır.
            //    İhale tipi seçilmediyse giriş noktası verilmez (tüm adresler ilgisiz olur); yalnızca odak klasörü kullanılır.
            if (focusRegex != null && keyword != null
                && string.IsNullOrWhiteSpace(topic.ActionPattern) && string.IsNullOrWhiteSpace(topic.EntryTypePattern))
            {
                entryTargets.AddRange(map.Endpoints
                    .Where(e => methodsById.TryGetValue(e.MethodId, out var m)
                                && focusRegex.IsMatch($"{m.Repo}/{m.File}")
                                && e.Route.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .Select(e => new EntryTarget(e.Route, $"{e.HttpMethod} {e.Route}")));
            }

            entryTargets = entryTargets.DistinctBy(e => e.Target).Take(MaxEntryTargets).ToList();
        }

        var coreTypeNames = coreTypes.Select(t => t.Name).Distinct().OrderBy(n => n).ToList();
        return new AnalysisScope
        {
            Scope = scope,
            Topic = topic,
            Files = files,
            CoreTypes = coreTypeNames,
            EntryTargets = entryTargets,
            ContextText = BuildContext(scope, topic, coreTypeNames, entryTargets, files.Count),
        };
    }

    /// <summary>Verilen metotlardan başlayıp çağrı zincirinde (en fazla CallDepth seviye) ulaşılan dosyaları ekler.</summary>
    private static void AddCallClosure(IEnumerable<MethodNode> start, HashSet<string> files,
        Dictionary<string, MethodNode> methodsById, Func<MethodNode, bool> excluded)
    {
        var frontier = start.ToList();
        var visited = new HashSet<string>(frontier.Select(m => m.Id));
        for (var depth = 0; depth < CallDepth && frontier.Count > 0 && visited.Count < MaxMethods; depth++)
        {
            var next = new List<MethodNode>();
            foreach (var m in frontier)
            {
                files.Add($"{m.Repo}|{m.File}");
                foreach (var id in m.Calls.SelectMany(c => c.Targets))
                    if (visited.Add(id) && methodsById.TryGetValue(id, out var target) && !excluded(target))
                        next.Add(target);
            }
            frontier = next;
        }
        foreach (var m in frontier) files.Add($"{m.Repo}|{m.File}");
    }

    /// <summary>"Ns.IFoo&lt;T&gt;" → "IFoo".</summary>
    private static string SimpleTypeName(string type)
    {
        var t = type.Trim().TrimEnd('?');
        var genericStart = t.IndexOf('<');
        if (genericStart >= 0) t = t[..genericStart];
        var lastDot = t.LastIndexOf('.');
        return lastDot >= 0 ? t[(lastDot + 1)..] : t;
    }

    private static string BuildContext(ScopeDefinition? scope, TopicDefinition? topic,
        List<string> coreTypes, List<EntryTarget> entryTargets, int fileCount)
    {
        var sb = new StringBuilder("## Analistin seçtiği kapsam\n");
        if (scope != null)
        {
            sb.AppendLine($"- İhale tipi: **{scope.Label}** — koddaki karşılığı: {scope.CodeName}.");
            if (coreTypes.Count > 0)
                sb.AppendLine($"- Bu tipe ait çekirdek sınıflar ({coreTypes.Count}): {string.Join(", ", coreTypes.Take(40))}{(coreTypes.Count > 40 ? ", …" : "")}");
            sb.AppendLine("- Soru başka bir ihale tipini açıkça sormuyorsa yalnızca bu tipi anlat; diğer tiplere ait sınıfları inceleme.");
        }
        if (topic != null)
        {
            sb.AppendLine($"- Konu: **{topic.Label}**.");
            if (!string.IsNullOrWhiteSpace(topic.Description))
                sb.AppendLine($"- Bu konuda nereye bakılmalı: {topic.Description}");
            if (entryTargets.Count > 0)
            {
                sb.AppendLine("- Akışın giriş noktaları (map_trace'i buradan başlat):");
                foreach (var e in entryTargets)
                    sb.AppendLine($"  - {e.Label}");
            }
        }
        if (fileCount > 0)
            sb.AppendLine($"- search_code ve find_files önce bu kapsamdaki {fileCount} dosyada arar. Kapsamda bulunamazsa " +
                          "otomatik olarak tüm repolarda aranır. Ortak enum/ayar/sabitler kapsam dışında olabilir.");
        return sb.ToString();
    }
}
