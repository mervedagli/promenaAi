using System.Text;
using System.Text.RegularExpressions;
using KodHaritasi;

namespace KodAnalist;

/// <summary>
/// Doğrulama aşaması için "kanıt paketi" hazırlar. Model kullanılmaz; program taslak cevaptaki
/// referansları ve kod haritasını kullanarak ilgili kod parçalarını doğrudan dosyalardan keser:
///  - taslakta geçen "dosya:satır" referansları (satır bir metodun içindeyse metodun tamamı),
///  - taslakta `ters tırnak` içinde geçen metot adlarının gövdeleri,
///  - taslakta / kapsamda geçen adreslerin (/api/...) akış ağacı ve bu akıştaki doğrulama metotları
///    (BFF ve HTTP ile çağrılan Sourcing katmanı dahil),
///  - akışı başlatan ekranlardaki doğrulama fonksiyonları.
/// Böylece doğrulayıcı kodu kendisi aramak / tekrar okumak zorunda kalmaz.
/// </summary>
public sealed partial class EvidenceBuilder(CodeTools tools, CodeMapService codeMap)
{
    private const int MaxTotalChars = 200_000;
    // Kontrol metotları uzun olabilir (ör. ValidateByCompetitionType ~360 satır); yarıda kesilirse
    // doğrulayıcı devamını okumak için adım harcar.
    private const int MaxRangeLines = 420;
    private const int MaxIdentifiers = 40;
    private const int ContextLines = 4;
    /// <summary>Pakete yazılan akış ağacının derinliği (okunurluk için sınırlı).</summary>
    private const int FlowTextDepth = 5;
    /// <summary>Kontrol metotları için inilen derinlik: ekran → BFF → client → [HTTP] → Sourcing → servis → kontrol.</summary>
    private const int FlowMethodDepth = 10;
    private const int MinUnqualifiedIdentifierLength = 8;

    [GeneratedRegex(@"(?<path>[A-Za-z0-9_\-./]+\.(?:cs|jsx?|tsx?))(?::(?<a>\d+)(?:\s*[-–]\s*(?<b>\d+))?)?")]
    private static partial Regex FileRefRegex();

    [GeneratedRegex(@"`(?<id>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)(?:\(\))?`")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"/api/[A-Za-z0-9_\-]+/[A-Za-z0-9_\-]+")]
    private static partial Regex RouteRegex();

    // Kontrol metotları: Validate*/…Validation…/Check*/Kontrol… ve Has…Invalid… (ör. HasInvalidBidIncrement).
    // "MoveToNotValid", "SetValidItemInResult" gibi yardımcılar bilerek dışarıda bırakılır.
    [GeneratedRegex(@"(Validat|Check|Kontrol|^Has\w*Invalid)")]
    private static partial Regex ValidationNameRegex();

    private const int MaxFlowValidationMethods = 40;

    [GeneratedRegex(@"\b(?:const|let|var|function)\s+(?<n>[\w$]*[Vv]alid[\w$]*)\s*=?\s*(?:async\s*)?(?:\(|function|[\w$]+\s*=>)")]
    private static partial Regex JsValidationFunctionRegex();

    /// <summary>Priority 0: kontrol metotları ve taslak referansları (her zaman önce). 1: bunların yardımcı metotları.</summary>
    private sealed record Range(string Repo, string Path, int Start, int End, string Reason, int Priority = 0);

    private const int MaxHelperMethods = 40;
    private const int EntryChainDepth = 3;
    private const int MaxEntryChainMethods = 8;

    /// <summary>
    /// 1. aşama için başlangıç paketi: konu seçilmişse giriş noktasının akışı, akıştaki kontrol metotları,
    /// bunların yardımcı metotları ve ekran doğrulamaları. Taslak olmadığı için yalnızca kapsamdan hesaplanır.
    /// Giriş noktası yoksa null döner.
    /// </summary>
    public string? BuildStarterPack(AnalysisScope scope) =>
        scope.EntryTargets.Count == 0 ? null : Build("", scope, "Başlangıç kod paketi",
            "Aşağıdaki kod parçaları, seçilen konunun giriş noktasından başlayarak kod haritasıyla bulunan akış ve kontrol " +
            "metotlarıdır; program tarafından doğrudan dosyalardan kesildi. Satır numaraları günceldir.");

    public string Build(string draft, AnalysisScope scope) => Build(draft, scope, "Kanıt paketi",
        "Aşağıdaki kod parçaları, taslaktaki referanslar ve kod haritası kullanılarak program tarafından doğrudan " +
        "dosyalardan kesildi. Satır numaraları günceldir.");

    /// <summary>
    /// Doğrulama aynı konuşmada yapılırken kullanılan ek kanıt paketi: konuşmada zaten bulunan kod parçaları
    /// (başlangıç paketi, read_method / read_file sonuçları) ve akış ağaçları tekrar eklenmez; yalnızca eksik
    /// kalan parçalar gelir. Böylece aynı kod iki kez gönderilip ödenmez.
    /// </summary>
    public string BuildSupplement(string draft, AnalysisScope scope, string alreadySent) =>
        Build(draft, scope, "Ek kanıt paketi",
            "Aşağıda yalnızca bu konuşmada henüz bulunmayan kod parçaları var (başlangıç paketindeki ve okuduğun " +
            "parçalar tekrar eklenmedi; onlar yukarıda). Satır numaraları günceldir.",
            ParseCovered(alreadySent), alreadySent);

    [GeneratedRegex(@"^### (?<repo>[^/\s]+)/(?<path>\S+) \(satır (?<a>\d+)-(?<b>\d+)\)", RegexOptions.Multiline)]
    private static partial Regex PackBlockRegex();

    [GeneratedRegex(@"^Dosya: (?<repo>[^/\s]+)/(?<path>\S+) \(toplam \d+ satır, gösterilen (?<a>\d+)-(?<b>\d+)\)", RegexOptions.Multiline)]
    private static partial Regex ReadResultRegex();

    /// <summary>Konuşmadaki paket ve okuma başlıklarından, hangi dosyanın hangi satırlarının zaten gönderildiğini çıkarır.</summary>
    private static Dictionary<(string Repo, string Path), List<(int A, int B)>> ParseCovered(string text)
    {
        var covered = new Dictionary<(string, string), List<(int, int)>>();
        foreach (var m in PackBlockRegex().Matches(text).Concat(ReadResultRegex().Matches(text)))
        {
            var key = (m.Groups["repo"].Value, m.Groups["path"].Value);
            if (!covered.TryGetValue(key, out var list)) covered[key] = list = [];
            list.Add((int.Parse(m.Groups["a"].Value), int.Parse(m.Groups["b"].Value)));
        }
        return covered;
    }

    private static bool IsCovered(Dictionary<(string Repo, string Path), List<(int A, int B)>>? covered,
        string repo, string path, int start, int end)
    {
        if (covered == null || !covered.TryGetValue((repo, path), out var list)) return false;
        // Aralıkları birleştirip [start, end] tamamen kapsanıyor mu bak.
        var pos = start;
        foreach (var (a, b) in list.OrderBy(x => x.A))
        {
            if (a > pos) break;
            if (b >= pos) pos = b + 1;
            if (pos > end) return true;
        }
        return pos > end;
    }

    private string Build(string draft, AnalysisScope scope, string title, string intro,
        Dictionary<(string Repo, string Path), List<(int A, int B)>>? covered = null, string? alreadySent = null)
    {
        var ranges = new List<Range>();
        var flowText = new StringBuilder();
        var current = codeMap.Current;
        var queries = current?.Queries;

        // 1) Akış ağaçları: taslaktaki ve kapsamdaki adresler
        var routes = RouteRegex().Matches(draft).Select(m => m.Value)
            .Concat(scope.EntryTargets.Select(e => e.Target))   // adres veya Sinif.Metot (ör. zamanlanmış görev)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (queries != null)
        {
            foreach (var route in routes)
            {
                var trace = queries.Trace(route, FlowTextDepth);
                if (trace.Contains("bulunamadı")) continue;
                var header = $"### Akış: {route}";
                if (alreadySent == null || !alreadySent.Contains(header, StringComparison.Ordinal))
                    flowText.AppendLine(header).AppendLine("```").AppendLine(Truncate(trace, 20_000)).AppendLine("```");

                // Akıştaki doğrulama metotları (BFF ve HTTP ile çağrılan Sourcing katmanı).
                // Kapsam seçiliyse diğer ihale tiplerine ait metotlar alınmaz.
                var flowMethods = queries.TraceMethods(route, FlowMethodDepth)
                    .Where(m => ValidationNameRegex().IsMatch(m.Name))
                    .Where(m => !scope.HasFileScope || scope.Files.Contains($"{m.Repo}|{m.File}"))
                    .Take(MaxFlowValidationMethods)
                    .ToList();
                foreach (var m in flowMethods)
                    ranges.Add(new Range(m.Repo, m.File, m.StartLine, m.EndLine, $"akıştaki kontrol metodu {m.Type}.{m.Name}"));

                // Giriş zinciri (ör. SendBid → SendBidInternal → PrepareAndSendBid → CompleteSendBid): kontrol
                // sonuçlarına göre işlemin devam edip etmediğine (teklif kaydediliyor mu?) burada karar verilir.
                var entryChain = queries.TraceMethods(route, EntryChainDepth)
                    .Where(m => !scope.HasFileScope || scope.Files.Contains($"{m.Repo}|{m.File}"))
                    .Where(m => m.Repo == queries.TraceMethods(route, 0).FirstOrDefault()?.Repo)
                    .Take(MaxEntryChainMethods);
                foreach (var m in entryChain)
                    ranges.Add(new Range(m.Repo, m.File, m.StartLine, m.EndLine, $"giriş zinciri {m.Type}.{m.Name}"));

                // Kontrol metotlarının doğrudan çağırdığı yardımcılar (ör. TryGetMinMaxRange, CalculateMinMaxBounds).
                // Düşük öncelikli: yalnızca pakette yer kalırsa eklenir.
                var methodsById = current!.Value.Map.Methods.ToDictionary(x => x.Id);
                var flowIds = flowMethods.Select(m => m.Id).ToHashSet();
                var helpers = flowMethods
                    .SelectMany(m => m.Calls.SelectMany(c => c.Targets))
                    .Distinct()
                    .Where(id => !flowIds.Contains(id))          // kontrol metodu olarak zaten pakette
                    .Where(methodsById.ContainsKey)
                    .Select(id => methodsById[id])
                    .Where(t => !scope.HasFileScope || scope.Files.Contains($"{t.Repo}|{t.File}"))
                    .Where(t => !IsHelperNoise(t.Name) && t.Type != "ResourceHelper")
                    .Take(MaxHelperMethods);
                foreach (var h in helpers)
                    ranges.Add(new Range(h.Repo, h.File, h.StartLine, h.EndLine, $"yardımcı metot {h.Type}.{h.Name}", Priority: 1));

                // Akışı başlatan ekranlardaki doğrulama fonksiyonları
                foreach (var screen in queries.ScreensFor(route).DistinctBy(s => (s.Repo, s.File)).Take(3))
                    AddJsValidationFunctions(screen.Repo, screen.File, ranges);
            }
        }

        // 2) Taslaktaki dosya:satır referansları
        foreach (Match m in FileRefRegex().Matches(draft))
        {
            if (!m.Groups["a"].Success) continue;   // satırsız dosya adlarını atla
            var resolved = tools.ResolveFile(m.Groups["path"].Value);
            if (resolved == null) continue;
            var (repo, path) = resolved.Value;
            var a = int.Parse(m.Groups["a"].Value);
            var b = m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value) : a;
            if (b < a) (a, b) = (b, a);

            var method = a == b ? queries?.MethodAt(repo, path, a) : null;
            if (method != null)
                ranges.Add(new Range(repo, path, method.StartLine, method.EndLine, $"taslakta {Path.GetFileName(path)}:{a} (metot {method.Name})"));
            else if (a == b && IsJs(path))
                ranges.Add(new Range(repo, path, a - 25, a + 25, $"taslakta {Path.GetFileName(path)}:{a}"));
            else
                ranges.Add(new Range(repo, path, a - ContextLines, b + ContextLines, $"taslakta {Path.GetFileName(path)}:{a}-{b}"));
        }

        // 3) Taslakta adı geçen metotlar
        if (queries != null)
        {
            var ids = IdentifierRegex().Matches(draft).Select(m => m.Groups["id"].Value).Distinct().Take(MaxIdentifiers);
            foreach (var id in ids)
            {
                var candidates = queries.FindMethods(id);
                if (candidates.Count == 0) continue;

                // "Group" gibi kısa, sınıf adı yazılmamış isimler ilgisiz metotlarla eşleşir; bunları alma.
                var qualified = id.Contains('.');
                if (!qualified && id.Length < MinUnqualifiedIdentifierLength) continue;

                // Kapsam seçiliyse yalnızca kapsamdakileri al; aynı adlı çok sayıda metodu (ör. her ihale tipinde) alma.
                var preferred = scope.HasFileScope
                    ? candidates.Where(c => scope.Files.Contains($"{c.Repo}|{c.File}")).ToList()
                    : candidates;
                if (preferred.Count == 0 && (qualified || candidates.Count == 1)) preferred = candidates;
                if (preferred.Count == 0 || preferred.Count > 3) continue;
                foreach (var c in preferred)
                    ranges.Add(new Range(c.Repo, c.File, c.StartLine, c.EndLine, $"taslakta adı geçen {c.Type}.{c.Name}"));
            }
        }

        return Render(title, intro, flowText.ToString(), ranges, covered);
    }

    /// <summary>İş kuralı taşımayan yardımcılar (mesaj ekleme, sonuç taşıma vb.).</summary>
    private static bool IsHelperNoise(string name) =>
        name.StartsWith("Add") && name.Contains("Message")
        || name is "MoveToNotValid" or "SetValidItemInResult" or "GetOrCreateGroupErr" or "RemoveValidItemsOfGroup"
            or "FindItemResult" or "TryGetColumn" or "GetRequestHeader" or "GetJsonResult";

    private void AddJsValidationFunctions(string repo, string path, List<Range> ranges)
    {
        var lines = tools.ReadLines(repo, path);
        if (lines == null) return;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = JsValidationFunctionRegex().Match(lines[i]);
            if (!m.Success) continue;
            var end = FindJsBlockEnd(lines, i);
            ranges.Add(new Range(repo, path, i + 1, end + 1, $"ekrandaki doğrulama fonksiyonu {m.Groups["n"].Value}"));
        }
    }

    /// <summary>Basit süslü parantez sayımıyla bir JS fonksiyonunun bittiği satırı bulur (en fazla 120 satır).</summary>
    private static int FindJsBlockEnd(string[] lines, int start)
    {
        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Length && i < start + 120; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; opened = true; }
                else if (ch == '}') depth--;
            }
            if (opened && depth <= 0) return i;
        }
        return Math.Min(lines.Length - 1, start + 60);
    }

    private string Render(string title, string intro, string flowText, List<Range> ranges,
        Dictionary<(string Repo, string Path), List<(int A, int B)>>? covered = null)
    {
        var skipped = 0;
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine(intro);
        sb.AppendLine();
        sb.Append(flowText);

        // Önce öncelik 0 (kontrol metotları, taslak referansları), sonra öncelik 1 (yardımcılar).
        // Öncelik 1 aralıkları, zaten eklenmiş bir aralığın içinde kalıyorsa atlanır.
        var primary = MergeRanges(ranges.Where(r => r.Priority == 0));
        var secondary = MergeRanges(ranges.Where(r => r.Priority == 1
            && !primary.Any(p => p.Repo == r.Repo && p.Path == r.Path && p.Start <= r.Start && r.End <= p.End)));

        var truncated = 0;
        foreach (var r in primary.Concat(secondary))
        {
            var lines = tools.ReadLines(r.Repo, r.Path);
            if (lines == null) continue;
            var start = Math.Max(1, r.Start);
            var end = Math.Min(lines.Length, r.End);
            if (end - start + 1 > MaxRangeLines) end = start + MaxRangeLines - 1;
            if (IsCovered(covered, r.Repo, r.Path, start, end)) { skipped++; continue; }

            var block = new StringBuilder();
            block.AppendLine($"### {r.Repo}/{r.Path} (satır {start}-{end}) — {Truncate(r.Reason, 300)}");
            block.AppendLine("```");
            for (var i = start; i <= end; i++)
                block.AppendLine($"{i,6}\t{lines[i - 1]}");
            if (r.End > end) block.AppendLine($"... (devamı kesildi, gerekirse read_file ile {end + 1}. satırdan oku)");
            block.AppendLine("```");

            if (sb.Length + block.Length > MaxTotalChars) { truncated++; continue; }
            sb.Append(block);
        }

        if (skipped > 0)
            sb.AppendLine($"\n({skipped} parça bu konuşmada zaten bulunduğu için tekrar eklenmedi.)");
        if (truncated > 0)
            sb.AppendLine($"\n(Paket boyut sınırına ulaştı; {truncated} parça eklenmedi. Gerekirse read_file ile oku.)");
        return sb.ToString();
    }

    /// <summary>
    /// Aynı dosyadaki çakışan / bitişik aralıkları birleştirir. Dosyalar küçükten büyüğe sıralanır:
    /// boyut sınırına gelinirse dışarıda kalan, tek büyük dosyanın son parçaları olur; ekran ve Sourcing
    /// gibi diğer katmanların kısa kontrolleri her zaman pakette yer alır.
    /// </summary>
    private static List<Range> MergeRanges(IEnumerable<Range> ranges) => ranges
        .GroupBy(r => (r.Repo, r.Path))
        .OrderBy(g => g.Sum(r => Math.Min(MaxRangeLines, r.End - r.Start + 1)))
        .SelectMany(g =>
        {
            var result = new List<Range>();
            foreach (var r in g.OrderBy(r => r.Start))
            {
                var start = Math.Max(1, r.Start);
                if (result.Count > 0 && start <= result[^1].End + 3)
                {
                    var last = result[^1];
                    result[^1] = last with
                    {
                        End = Math.Max(last.End, r.End),
                        Reason = last.Reason.Contains(r.Reason) ? last.Reason : last.Reason + "; " + r.Reason,
                    };
                }
                else
                {
                    result.Add(r with { Start = start });
                }
            }
            return result;
        })
        .ToList();

    private static bool IsJs(string path) => path.EndsWith(".js") || path.EndsWith(".jsx") || path.EndsWith(".ts") || path.EndsWith(".tsx");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
