using System.Text.RegularExpressions;

namespace KodHaritasi;

/// <summary>
/// React (JavaScript) dosyalarındaki backend çağrılarını bulur:
/// postMethod("/api/X/Y"), requestMethod(`${BASE}/Y`) gibi. Aynı dosyada tanımlı
/// "const BASE = '/api/X'" sabitleri çözülür.
/// </summary>
public static partial class FrontendScanner
{
    // Bir satırda "/api/Controller/Action" geçen string veya şablon ifadesi.
    [GeneratedRegex(@"(?<helper>\b[A-Za-z]*(?:Method|fetch|axios\.(?:get|post|put|delete)))\s*\(\s*(?<q>[`""'])(?<url>[^`""']*)\k<q>")]
    private static partial Regex CallRegex();

    // const BASE = "/api/ScoreCard";  veya  const BASE = `${apiUrl}/api/ScoreCard`;
    [GeneratedRegex(@"\b(?:const|let|var)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<q>[`""'])(?<url>[^`""']*?/api/[^`""']*)\k<q>")]
    private static partial Regex ConstRegex();

    [GeneratedRegex(@"/api/[A-Za-z0-9_\-]+(?:/[A-Za-z0-9_\-]+)*")]
    private static partial Regex ApiPathRegex();

    // Çağrıyı içeren fonksiyonun adını bulmak için: "const sendBid = async (" / "function sendBid(" / "sendBid: async ("
    [GeneratedRegex(@"(?:\b(?:const|let|var)\s+(?<n>[A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\(|[A-Za-z_$][\w$]*\s*=>|function))|(?:\bfunction\s+(?<n>[A-Za-z_$][\w$]*)\s*\()|(?:^\s*(?<n>[A-Za-z_$][\w$]*)\s*:\s*(?:async\s*)?\()")]
    private static partial Regex FunctionRegex();

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".js", ".jsx", ".ts", ".tsx" };

    public static bool IsFrontendFile(string file) => Extensions.Contains(Path.GetExtension(file));

    public static void ScanFile(string repo, string repoRoot, string file, CodeMap map)
    {
        var lines = File.ReadAllLines(file);
        var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

        // Aynı dosyadaki sabitler
        var constants = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            foreach (Match m in ConstRegex().Matches(line))
            {
                var path = ApiPathRegex().Match(m.Groups["url"].Value);
                if (path.Success) constants[m.Groups["name"].Value] = path.Value;
            }
        }

        var currentFunction = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fn = FunctionRegex().Match(line);
            if (fn.Success) currentFunction = fn.Groups["n"].Value;

            foreach (Match m in CallRegex().Matches(line))
            {
                var url = m.Groups["url"].Value;
                var route = ResolveUrl(url, constants);
                if (route == null) continue;

                map.FrontendCalls.Add(new FrontendCall
                {
                    Repo = repo,
                    File = relative,
                    Line = i + 1,
                    Function = currentFunction,
                    HttpHelper = m.Groups["helper"].Value,
                    Route = route,
                });
            }
        }
    }

    private static string? ResolveUrl(string url, Dictionary<string, string> constants)
    {
        var direct = ApiPathRegex().Match(url);
        if (direct.Success) return direct.Value;

        // `${BASE}/GetX` → sabitin değeri + "/GetX"
        var tpl = Regex.Match(url, @"^\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}(?<rest>/[A-Za-z0-9_\-/]*)");
        if (tpl.Success && constants.TryGetValue(tpl.Groups["name"].Value, out var basePath))
            return basePath + tpl.Groups["rest"].Value.TrimEnd('/');

        return null;
    }
}
