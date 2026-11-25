using System.Text;

namespace KodHaritasi;

/// <summary>Kod haritası üzerinde sorgular: akış izleme (aşağı), çağıranlar (yukarı), arama.</summary>
public sealed class Queries
{
    private readonly CodeMap _map;
    private readonly Dictionary<string, MethodNode> _methodsById;
    private readonly Dictionary<string, List<(MethodNode Caller, CallSite Site)>> _callers = [];
    private readonly Dictionary<string, List<Endpoint>> _endpointsByMethod;
    private readonly Dictionary<string, List<FrontendCall>> _frontendByMethod = [];

    public Queries(CodeMap map)
    {
        _map = map;
        _methodsById = map.Methods.ToDictionary(m => m.Id);
        _endpointsByMethod = map.Endpoints.GroupBy(e => e.MethodId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var m in map.Methods)
            foreach (var site in m.Calls)
                foreach (var target in site.Targets)
                {
                    if (!_callers.TryGetValue(target, out var list)) _callers[target] = list = [];
                    list.Add((m, site));
                }

        foreach (var fc in map.FrontendCalls)
            foreach (var id in fc.EndpointMethodIds)
            {
                if (!_frontendByMethod.TryGetValue(id, out var list)) _frontendByMethod[id] = list = [];
                list.Add(fc);
            }
    }

    /// <summary>Bir adresten ("/api/X/Y") veya metottan ("Tip.Metot") başlayarak çağrı ağacını aşağı doğru çıkarır.</summary>
    public string Trace(string target, int maxDepth)
    {
        var sb = new StringBuilder();
        var starts = ResolveTarget(target, sb);
        if (starts.Count == 0) return sb.ToString();

        foreach (var start in starts)
        {
            var screens = _frontendByMethod.GetValueOrDefault(start.Id, []);
            if (screens.Count > 0)
            {
                sb.AppendLine("Bu adresi çağıran ekranlar:");
                foreach (var fc in screens.Take(15))
                    sb.AppendLine($"  {fc.Repo}/{fc.File}:{fc.Line}  {fc.Function}() → {fc.HttpHelper}(\"{fc.Route}\")");
                if (screens.Count > 15) sb.AppendLine($"  … {screens.Count - 15} ekran daha");
                sb.AppendLine();
            }

            sb.AppendLine(Describe(start));
            WriteChildren(sb, start, "", 1, maxDepth, new HashSet<string> { start.Id });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Bir metodu kimlerin çağırdığını yukarı doğru çıkarır (ekranlara kadar).</summary>
    public string Callers(string target, int maxDepth)
    {
        var sb = new StringBuilder();
        var starts = ResolveTarget(target, sb);
        foreach (var start in starts)
        {
            sb.AppendLine(Describe(start));
            WriteCallers(sb, start, "", 1, maxDepth, new HashSet<string> { start.Id });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Bir akıştaki metotları (gürültü hariç) liste olarak döner. Kanıt paketi için kullanılır.</summary>
    public List<MethodNode> TraceMethods(string target, int maxDepth)
    {
        var starts = ResolveTarget(target, new StringBuilder());
        var result = new List<MethodNode>();
        var seen = new HashSet<string>();
        var frontier = starts;
        for (var depth = 0; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<MethodNode>();
            foreach (var m in frontier.Where(m => seen.Add(m.Id)))
            {
                result.Add(m);
                next.AddRange(m.Calls.SelectMany(c => c.Targets)
                    .Where(_methodsById.ContainsKey)
                    .Select(id => _methodsById[id])
                    .Where(t => !IsNoise(t)));
            }
            frontier = next;
        }
        return result;
    }

    /// <summary>Bir adresi / metodu çağıran ekranlar.</summary>
    public List<FrontendCall> ScreensFor(string target) =>
        ResolveTarget(target, new StringBuilder())
            .SelectMany(m => _frontendByMethod.GetValueOrDefault(m.Id, []))
            .ToList();

    /// <summary>Ada göre metot bulur ("Metot" veya "Sinif.Metot").</summary>
    public List<MethodNode> FindMethods(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0
            ? _map.Methods.Where(m => m.Name == name[(dot + 1)..] && m.Type == name[..dot][(name[..dot].LastIndexOf('.') + 1)..]).ToList()
            : _map.Methods.Where(m => m.Name == name).ToList();
    }

    /// <summary>Kimliğe göre metot (harita yeniden üretildiğinde kimlik dosya yolu + ad + sıra ile korunur).</summary>
    public MethodNode? MethodById(string id) => _methodsById.GetValueOrDefault(id);

    /// <summary>Bir dosyada verilen satırı içeren metot.</summary>
    public MethodNode? MethodAt(string repo, string file, int line) =>
        _methodsByFile.Value.TryGetValue((repo, file), out var list)
            ? list.FirstOrDefault(m => m.StartLine <= line && line <= m.EndLine)
            : null;

    private Lazy<Dictionary<(string, string), List<MethodNode>>>? _methodsByFileLazy;
    private Lazy<Dictionary<(string, string), List<MethodNode>>> _methodsByFile =>
        _methodsByFileLazy ??= new(() => _map.Methods.GroupBy(m => (m.Repo, m.File)).ToDictionary(g => g.Key, g => g.ToList()));

    public string Find(string text, int limit = 40)
    {
        var sb = new StringBuilder();
        var endpoints = _map.Endpoints.Where(e => e.Route.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
        if (endpoints.Count > 0)
        {
            sb.AppendLine("Adresler:");
            foreach (var e in endpoints)
                sb.AppendLine($"  {e.HttpMethod,-6} {e.Route}  → {Short(e.MethodId)}");
        }

        var methods = _map.Methods
            .Where(m => $"{m.Type}.{m.Name}".Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList();
        if (methods.Count > 0)
        {
            sb.AppendLine("Metotlar:");
            foreach (var m in methods) sb.AppendLine("  " + Describe(m));
        }

        var types = _map.Types.Where(t => t.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
        if (types.Count > 0)
        {
            sb.AppendLine("Tipler:");
            foreach (var t in types) sb.AppendLine($"  {t.Kind} {t.Name}  ({t.Repo}/{t.File}:{t.Line})");
        }

        return sb.Length == 0 ? "Eşleşme bulunamadı." : sb.ToString();
    }

    public string StatsText()
    {
        var s = _map.Stats;
        var pct = s.TotalCalls == 0 ? 0 : 100.0 * s.ResolvedCalls / s.TotalCalls;
        return $"""
            Üretilme zamanı : {_map.GeneratedAt:yyyy-MM-dd HH:mm}
            Repolar         : {string.Join(", ", _map.Repos.Select(r => r.Name + (r.Commit != null ? $" ({r.Commit[..Math.Min(8, r.Commit.Length)]})" : "")))}
            C# dosyası      : {s.CSharpFiles:N0} (sözdizimi hatalı: {s.ParseErrors})
            JS dosyası      : {s.JsFiles:N0}
            Tip / metot     : {_map.Types.Count:N0} / {_map.Methods.Count:N0}
            Adres (endpoint): {_map.Endpoints.Count:N0}
            Çağrılar        : {s.TotalCalls:N0}, çözümlenen {s.ResolvedCalls:N0} (%{pct:F0}; çözülemeyenlerin çoğu .NET/kütüphane çağrısı)
            HTTP köprüsü    : {s.HttpBridges:N0} (BFF → Sourcing vb.)
            MediatR köprüsü : {s.MediatorBridges:N0}
            Ekran çağrısı   : {_map.FrontendCalls.Count:N0}, adresiyle eşleşen {s.FrontendCallsMatched:N0}
            Süre            : {s.Seconds:F1} sn
            """;
    }

    /// <summary>
    /// İş kuralı taşımayan altyapı çağrıları (hata mesajı ekleme, istek başlığı, IP okuma vb.).
    /// Akışı okunur tutmak için ağaçta gizlenir; --tumu ile gösterilebilir.
    /// </summary>
    private static readonly HashSet<string> NoiseTypes = new(StringComparer.Ordinal)
    {
        "ResponseBase", "HelpTool", "RedisCacheService", "ResourceHelper", "LogHelper",
    };

    private static readonly HashSet<string> NoiseMethods = new(StringComparer.Ordinal)
    {
        "AddMessage", "AddMessages", "AddMessagesFromResponse", "AddMessagesToResponse", "GetJsonResult",
        "GetRequestHeader", "HasError", "HasErrorMessage", "IsSuccesed", "GetResource", "GetResourceWithParams",
        "TranslateAndFormatMessages", "GetBrowser", "AddItemErrorMessage", "AddGroupErrorMessage",
    };

    public bool ShowAll { get; set; }

    private bool IsNoise(MethodNode m) => !ShowAll && (NoiseTypes.Contains(m.Type) || NoiseMethods.Contains(m.Name));

    private void WriteChildren(StringBuilder sb, MethodNode node, string indent, int depth, int maxDepth, HashSet<string> path)
    {
        if (depth > maxDepth) return;

        // Bir satırda adres yardımcısı (ör. GetAuctionActionUrl) çözülüp HTTP köprüsü kurulduysa,
        // yardımcının kendisini ayrıca gösterme: köprü zaten gerçek hedefi veriyor.
        var httpLines = node.Calls.Where(c => c.Via == "http").Select(c => c.Line).ToHashSet();

        // Aynı hedefe giden çağrıları birleştir, aşırı yüklemeleri (overload) tek satıra indir, gürültüyü gizle.
        var groups = node.Calls
            .Where(site => site.Via == "http" || !httpLines.Contains(site.Line) || ShowAll)
            .Select(site => (Site: site, Targets: site.Targets.Where(_methodsById.ContainsKey).Select(id => _methodsById[id])
                .Where(t => !IsNoise(t)).ToList()))
            .Where(x => x.Targets.Count > 0)
            .GroupBy(x => string.Join("|", x.Targets.Select(t => $"{t.Repo}:{t.File}:{t.Type}.{t.Name}").Distinct().OrderBy(s => s)))
            .Select(g => (Lines: g.Select(x => x.Site.Line).Distinct().ToList(), First: g.First()))
            .ToList();

        for (var i = 0; i < groups.Count; i++)
        {
            var (lines, (site, targets)) = groups[i];
            var last = i == groups.Count - 1;
            var branch = last ? "└─ " : "├─ ";
            var childIndent = indent + (last ? "   " : "│  ");
            var via = site.Via switch { "http" => " [HTTP]", "mediator" => " [MediatR]", "interface" => " [arayüz]", _ => "" };
            var lineText = lines.Count == 1 ? $"[{lines[0]}]" : $"[{string.Join(",", lines.Take(5))}{(lines.Count > 5 ? ",…" : "")}]";

            // Aynı sınıftaki aşırı yüklemeleri tek hedef say.
            var distinct = targets.GroupBy(t => (t.Repo, t.File, t.Type, t.Name)).Select(g => g.First()).ToList();
            if (distinct.Count == 1)
            {
                var t = distinct[0];
                var overloads = targets.Count > 1 ? $" ({targets.Count} aşırı yükleme)" : "";
                sb.AppendLine($"{indent}{branch}{lineText} {site.Text}{via} → {Describe(t)}{overloads}");
                if (path.Add(t.Id))
                {
                    foreach (var o in targets) path.Add(o.Id);
                    var merged = new MethodNode { Calls = targets.SelectMany(o => o.Calls).ToList() };
                    WriteChildren(sb, targets.Count > 1 ? merged : t, childIndent, depth + 1, maxDepth, path);
                    foreach (var o in targets) path.Remove(o.Id);
                }
            }
            else
            {
                sb.AppendLine($"{indent}{branch}{lineText} {site.Text}{via} → {distinct.Count} olası hedef:");
                foreach (var t in distinct.Take(8))
                    sb.AppendLine($"{childIndent}• {Describe(t)}");
                if (distinct.Count > 8) sb.AppendLine($"{childIndent}• … {distinct.Count - 8} hedef daha");
            }
        }
    }

    private void WriteCallers(StringBuilder sb, MethodNode node, string indent, int depth, int maxDepth, HashSet<string> path)
    {
        if (depth > maxDepth) return;

        foreach (var fc in _frontendByMethod.GetValueOrDefault(node.Id, []).Take(10))
            sb.AppendLine($"{indent}↑ EKRAN {fc.Repo}/{fc.File}:{fc.Line}  {fc.Function}() → \"{fc.Route}\"");

        var callers = _callers.GetValueOrDefault(node.Id, []);
        foreach (var (caller, site) in callers.Take(25))
        {
            var via = site.Via switch { "http" => " [HTTP]", "mediator" => " [MediatR]", "interface" => " [arayüz]", _ => "" };
            sb.AppendLine($"{indent}↑ {Describe(caller)} satır {site.Line}{via}");
            if (path.Add(caller.Id))
            {
                WriteCallers(sb, caller, indent + "   ", depth + 1, maxDepth, path);
                path.Remove(caller.Id);
            }
        }
        if (callers.Count > 25) sb.AppendLine($"{indent}… {callers.Count - 25} çağıran daha");
    }

    private List<MethodNode> ResolveTarget(string target, StringBuilder sb)
    {
        target = target.Trim();
        List<MethodNode> result;

        if (target.StartsWith('/'))
        {
            var segs = target.Split('?')[0].Trim('/').ToLowerInvariant();
            result = _map.Endpoints
                .Where(e => e.Route.Trim('/').Equals(segs, StringComparison.OrdinalIgnoreCase))
                .Select(e => _methodsById[e.MethodId])
                .Distinct()
                .ToList();
        }
        else
        {
            var exact = _map.Methods.Where(m => $"{m.Type}.{m.Name}".Equals(target, StringComparison.OrdinalIgnoreCase)
                                                || m.Id.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            result = exact.Count > 0
                ? exact
                : _map.Methods.Where(m => m.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (result.Count == 0)
            sb.AppendLine($"'{target}' bulunamadı. Önce 'find' ile arayın.");
        else if (result.Count > 8)
        {
            sb.AppendLine($"'{target}' için {result.Count} eşleşme var, daha belirgin yazın (ör. Tip.Metot):");
            foreach (var m in result.Take(20)) sb.AppendLine("  " + Describe(m));
            return [];
        }
        return result;
    }

    private string Describe(MethodNode m)
    {
        var ep = _endpointsByMethod.GetValueOrDefault(m.Id);
        var epText = ep is { Count: > 0 } ? $" [{ep[0].HttpMethod} {ep[0].Route}]" : "";
        return $"{m.Type}.{m.Name}{epText}  ({m.Repo}/{m.File}:{m.StartLine})";
    }

    private string Short(string methodId) =>
        _methodsById.TryGetValue(methodId, out var m) ? $"{m.Type}.{m.Name}  ({m.Repo}/{m.File}:{m.StartLine})" : methodId;
}
