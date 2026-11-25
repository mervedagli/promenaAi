using System.Diagnostics;
using System.Text.Json;
using KodHaritasi;

// Kullanım:
//   dotnet run -- build  [--ayarlar ../KodAnalist/appsettings.json] [--harita ../../data/codemap.json]
//   dotnet run -- build  --repo ProSourceCore=/yol/ProSourceCore --repo Sourcing=/yol/Sourcing   (ayar dosyası yerine)
//   dotnet run -- trace  "/api/AggregatedAuctionSupplier/SendBid" [--derinlik 4]
//   dotnet run -- trace  "AuctionBidBaseService.AdvancedSendBid"
//   dotnet run -- callers "AggregatedAuctionSupplierController.ValidateBidSettings" [--derinlik 3]
//   dotnet run -- find   "AggregatedAuction"
//   dotnet run -- stats

var command = args.FirstOrDefault()?.ToLowerInvariant();
string Option(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var mapPath = Path.GetFullPath(Option("--harita", "../../data/codemap.json"));
var settingsPath = Path.GetFullPath(Option("--ayarlar", "../KodAnalist/appsettings.json"));
var depth = int.TryParse(Option("--derinlik", "4"), out var d) ? d : 4;
var jsonOptions = new JsonSerializerOptions { WriteIndented = false };

switch (command)
{
    case "build":
        Build(settingsPath, mapPath);
        break;
    case "trace" or "callers" or "find" or "stats":
        if (!File.Exists(mapPath))
        {
            Console.Error.WriteLine($"Harita bulunamadı: {mapPath}. Önce 'build' çalıştırın.");
            return 1;
        }
        var map = JsonSerializer.Deserialize<CodeMap>(File.ReadAllText(mapPath))!;
        var queries = new Queries(map) { ShowAll = args.Contains("--tumu") };
        var arg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        Console.WriteLine(command switch
        {
            "trace" => queries.Trace(arg, depth),
            "callers" => queries.Callers(arg, depth),
            "find" => queries.Find(arg),
            _ => queries.StatsText(),
        });
        break;
    default:
        Console.WriteLine("Komutlar: build | trace <adres|Tip.Metot> | callers <Tip.Metot> | find <metin> | stats");
        return 1;
}
return 0;

void Build(string settingsFile, string outputFile)
{
    var sw = Stopwatch.StartNew();

    // --repo Ad=Yol verilmişse (ör. KodAnalist'in otomatik eşitlediği klonlar) ayar dosyası yerine onlar kullanılır.
    var explicitRepos = args
        .Select((a, i) => (a, i))
        .Where(x => x.a == "--repo" && x.i + 1 < args.Length)
        .Select(x => args[x.i + 1].Split('=', 2))
        .Where(p => p.Length == 2)
        .Select(p => (p[0], Path.GetFullPath(p[1])))
        .ToList();
    var repos = explicitRepos.Count > 0 ? explicitRepos : ReadRepos(settingsFile);
    var map = new CodeMap { GeneratedAt = DateTime.Now };
    var rawMethods = new List<CSharpIndexer.RawMethod>();

    foreach (var (name, path) in repos)
    {
        Console.WriteLine($"Taranıyor: {name} ({path})");
        map.Repos.Add(new RepoSnapshot { Name = name, Path = path, Commit = GitCommit(path) });

        foreach (var file in EnumerateSourceFiles(path))
        {
            try
            {
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    map.Stats.CSharpFiles++;
                    CSharpIndexer.IndexFile(name, path, file, map, rawMethods);
                }
                else if (FrontendScanner.IsFrontendFile(file))
                {
                    map.Stats.JsFiles++;
                    FrontendScanner.ScanFile(name, path, file, map);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  Okunamadı: {file}: {ex.Message}");
            }
        }
    }

    Console.WriteLine("Çağrılar bağlanıyor…");
    new Linker(map).Link(rawMethods);

    sw.Stop();
    map.Stats.Seconds = sw.Elapsed.TotalSeconds;

    // Önce geçici dosyaya yaz, sonra tek adımda yerine koy: çalışan uygulama yarım yazılmış haritayı okumaz.
    Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
    var tempFile = outputFile + ".tmp";
    File.WriteAllText(tempFile, JsonSerializer.Serialize(map, jsonOptions));
    File.Move(tempFile, outputFile, overwrite: true);
    Console.WriteLine($"Harita yazıldı: {outputFile} ({new FileInfo(outputFile).Length / 1024 / 1024.0:F1} MB)");
    Console.WriteLine();
    Console.WriteLine(new Queries(map).StatsText());
}

// Repo listesi KodAnalist'in ayarlarından okunur: önce appsettings.json, sonra varsa makineye özel
// appsettings.Local.json (aynı sıradaki kayıtların Name/Path değerlerini ezer; KodAnalist'in davranışıyla aynı).
// Göreli yollar, ayar dosyasının bulunduğu klasöre (src/KodAnalist) göre çözülür.
static List<(string Name, string Path)> ReadRepos(string settingsFile)
{
    var settingsDir = Path.GetDirectoryName(Path.GetFullPath(settingsFile))!;
    var repos = ReadRepoArray(settingsFile);

    var localFile = Path.Combine(settingsDir, "appsettings.Local.json");
    if (File.Exists(localFile))
    {
        var local = ReadRepoArray(localFile);
        for (var i = 0; i < local.Count; i++)
        {
            if (i < repos.Count)
                repos[i] = (local[i].Name ?? repos[i].Name, local[i].Path ?? repos[i].Path);
            else if (local[i].Name != null && local[i].Path != null)
                repos.Add(local[i]);
        }
    }

    return repos
        .Where(r => r.Name != null && r.Path != null)
        .Select(r => (r.Name!, Path.GetFullPath(r.Path!, settingsDir)))
        .ToList();
}

static List<(string? Name, string? Path)> ReadRepoArray(string file)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(file),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    if (!doc.RootElement.TryGetProperty("Repos", out var arr)) return [];
    return arr.EnumerateArray()
        .Select(r => (
            r.TryGetProperty("Name", out var n) ? n.GetString() : null,
            r.TryGetProperty("Path", out var p) ? p.GetString() : null))
        .ToList();
}

static IEnumerable<string> EnumerateSourceFiles(string root)
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", "packages", "TestResults",
        "public", "lib", "Temp", "Migrations",
    };
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var dir = stack.Pop();
        foreach (var f in Directory.GetFiles(dir))
        {
            if (f.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            // Test projeleri haritaya dahil edilmez (iş akışını göstermezler).
            if (skip.Contains(name) || name == "Test" || name.EndsWith(".Test") || name.EndsWith(".Tests")) continue;
            stack.Push(sub);
        }
    }
}

static string? GitCommit(string path)
{
    try
    {
        var psi = new ProcessStartInfo("git", $"-C \"{path}\" rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 && output.Length > 0 ? output : null;
    }
    catch
    {
        return null;
    }
}
