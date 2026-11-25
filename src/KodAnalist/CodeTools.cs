using System.Text;
using System.Text.RegularExpressions;

namespace KodAnalist;

/// <summary>
/// Modelin kodu incelemek için kullandığı salt okunur araçlar.
/// Hiçbir araç dosya yazmaz, komut çalıştırmaz ve tanımlı depoların dışına çıkamaz.
/// </summary>
public sealed class CodeTools
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", "packages", "TestResults",
    };

    private static readonly HashSet<string> SearchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".jsx", ".ts", ".tsx", ".sql", ".json", ".cshtml", ".razor", ".xml", ".config",
        ".resx", ".yml", ".yaml", ".md", ".html", ".css", ".scss",
    };

    private const long MaxSearchableFileBytes = 3 * 1024 * 1024;
    // Bu sınırlar her adımda modele giden metni (dolayısıyla token'ı) belirler.
    private const int MaxSearchResults = 50;
    private const int MaxFindResults = 60;
    private const int MaxListEntries = 150;
    private const int MaxReadLines = 250;
    private const int MaxLineLength = 200;

    private readonly Dictionary<string, RepoConfig> _repos;

    public CodeTools(IEnumerable<RepoConfig> repos)
    {
        _repos = repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<RepoConfig> Repos => _repos.Values;

    /// <summary>Bir klasörün içeriğini listeler.</summary>
    public string ListDirectory(string repo, string? path)
    {
        var (root, full) = Resolve(repo, path);
        if (!Directory.Exists(full))
            return $"HATA: Klasör bulunamadı: {repo}/{path}";

        var sb = new StringBuilder();
        var dirs = Directory.GetDirectories(full)
            .Where(d => !SkippedDirectories.Contains(System.IO.Path.GetFileName(d)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = Directory.GetFiles(full).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var count = 0;
        foreach (var d in dirs)
        {
            if (count++ >= MaxListEntries) break;
            sb.AppendLine($"[klasör] {Relative(root, d)}/");
        }
        foreach (var f in files)
        {
            if (count++ >= MaxListEntries) break;
            sb.AppendLine($"[dosya]  {Relative(root, f)}");
        }

        var total = dirs.Count + files.Count;
        if (total > MaxListEntries)
            sb.AppendLine($"... ({total - MaxListEntries} öğe daha var, daha dar bir klasör listeleyin)");

        return sb.Length == 0 ? "(boş klasör)" : sb.ToString();
    }

    /// <summary>
    /// Dosya adına göre dosya bulur (ör. "*AggregatedAuction*Controller.cs").
    /// <paramref name="scopeFiles"/> verilirse önce kapsamdaki dosyalarda arar; bulamazsa tüm repoda.
    /// </summary>
    public string FindFiles(string repo, string namePattern, ISet<string>? scopeFiles = null)
    {
        var (root, _) = Resolve(repo, null);
        var regex = WildcardToRegex(namePattern);

        List<string> Find(Func<string, bool> filter) => EnumerateFiles(root, filterExtensions: false)
            .Where(f => regex.IsMatch(System.IO.Path.GetFileName(f)))
            .Select(f => Relative(root, f))
            .Where(filter)
            .Take(MaxFindResults + 1)
            .ToList();

        var prefix = "";
        var matches = new List<string>();
        if (scopeFiles != null)
        {
            matches = Find(rel => scopeFiles.Contains($"{repo}|{rel}"));
            prefix = matches.Count > 0
                ? "(Seçili kapsam içinde arandı. Kapsam dışını da görmek için kapsam=\"tumu\" kullan.)\n"
                : "(Seçili kapsamda bulunamadı; tüm repoda arandı.)\n";
        }
        if (matches.Count == 0)
            matches = Find(_ => true);

        if (matches.Count == 0)
            return prefix + "Eşleşen dosya bulunamadı.";

        var sb = new StringBuilder(prefix);
        foreach (var m in matches.Take(MaxFindResults))
            sb.AppendLine(m);
        if (matches.Count > MaxFindResults)
            sb.AppendLine($"... (ilk {MaxFindResults} sonuç gösterildi, kalıbı daraltın)");
        return sb.ToString();
    }

    /// <summary>
    /// Dosya içeriklerinde düzenli ifade (regex) ile arama yapar.
    /// <paramref name="scopeFiles"/> verilirse önce kapsamdaki dosyalarda arar; eşleşme yoksa tüm repoda.
    /// </summary>
    public string SearchCode(string repo, string pattern, string? fileGlob, bool ignoreCase, ISet<string>? scopeFiles = null)
    {
        if (scopeFiles != null)
        {
            var scoped = SearchCodeCore(repo, pattern, fileGlob, ignoreCase, rel => scopeFiles.Contains($"{repo}|{rel}"));
            if (scoped.Found > 0 || scoped.Text.StartsWith("HATA"))
                return "(Seçili kapsam içinde arandı. Kapsam dışını da görmek için kapsam=\"tumu\" kullan.)\n" + scoped.Text;
            return "(Seçili kapsamda eşleşme yok; tüm repoda arandı.)\n" + SearchCodeCore(repo, pattern, fileGlob, ignoreCase, null).Text;
        }
        return SearchCodeCore(repo, pattern, fileGlob, ignoreCase, null).Text;
    }

    private (int Found, string Text) SearchCodeCore(string repo, string pattern, string? fileGlob, bool ignoreCase, Func<string, bool>? fileFilter)
    {
        var (root, _) = Resolve(repo, null);

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return (0, $"HATA: Geçersiz düzenli ifade: {ex.Message}");
        }

        var globRegex = string.IsNullOrWhiteSpace(fileGlob) ? null : WildcardToRegex(fileGlob);

        var sb = new StringBuilder();
        var found = 0;
        var filesWithMatches = 0;

        foreach (var file in EnumerateFiles(root, filterExtensions: true))
        {
            if (globRegex != null && !globRegex.IsMatch(System.IO.Path.GetFileName(file)))
                continue;
            if (fileFilter != null && !fileFilter(Relative(root, file)))
                continue;

            var info = new FileInfo(file);
            if (info.Length > MaxSearchableFileBytes)
                continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var fileHadMatch = false;
            for (var i = 0; i < lines.Length; i++)
            {
                bool isMatch;
                try { isMatch = regex.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { return (0, "HATA: Arama çok uzun sürdü, daha basit bir ifade deneyin."); }

                if (!isMatch) continue;

                found++;
                if (!fileHadMatch) { filesWithMatches++; fileHadMatch = true; }
                if (found <= MaxSearchResults)
                    sb.AppendLine($"{Relative(root, file)}:{i + 1}: {Truncate(lines[i].Trim())}");
            }
        }

        if (found == 0)
            return (0, "Eşleşme bulunamadı.");

        if (found > MaxSearchResults)
            sb.AppendLine($"... toplam {found} eşleşme ({filesWithMatches} dosya). İlk {MaxSearchResults} gösterildi; aramayı daraltın.");

        return (found, sb.ToString());
    }

    /// <summary>
    /// Cevapta geçen kısmi bir dosya yolunu ("AggregatedAuctionSupplierController.cs" veya
    /// "ProSource.BFF/Controllers/...cs") gerçek repo + göreli yola çevirir. Birden fazla eşleşme varsa ilkini döner.
    /// </summary>
    public (string Repo, string Path)? ResolveFile(string partialPath)
    {
        var partial = partialPath.Replace('\\', '/').TrimStart('/');

        // "Repo/..." biçiminde yazılmışsa doğrudan çöz.
        foreach (var repo in _repos.Values)
        {
            if (!partial.StartsWith(repo.Name + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = partial[(repo.Name.Length + 1)..];
            if (File.Exists(System.IO.Path.Combine(repo.Path, rel))) return (repo.Name, rel);
        }

        foreach (var (repoName, rel) in AllFiles.Value)
        {
            if (rel.EndsWith("/" + partial, StringComparison.OrdinalIgnoreCase) || rel.Equals(partial, StringComparison.OrdinalIgnoreCase))
                return (repoName, rel);
        }
        return null;
    }

    /// <summary>Kanıt paketi için: dosyanın belirli satırlarını numaralı olarak döner (başlıksız).</summary>
    public string[]? ReadLines(string repo, string path)
    {
        var (_, full) = Resolve(repo, path);
        return File.Exists(full) ? File.ReadAllLines(full) : null;
    }

    private Lazy<List<(string Repo, string Rel)>>? _allFiles;
    private Lazy<List<(string Repo, string Rel)>> AllFiles => _allFiles ??= new(() =>
        _repos.Values.SelectMany(r =>
        {
            var root = System.IO.Path.GetFullPath(r.Path);
            return EnumerateFiles(root, filterExtensions: true).Select(f => (r.Name, Relative(root, f)));
        }).ToList());

    /// <summary>Bir dosyayı satır numaralarıyla okur.</summary>
    public string ReadFile(string repo, string path, int? startLine, int? endLine)
    {
        var (root, full) = Resolve(repo, path);
        if (!File.Exists(full))
            return $"HATA: Dosya bulunamadı: {repo}/{path}";

        var lines = File.ReadAllLines(full);
        var start = Math.Max(1, startLine ?? 1);
        var end = Math.Min(lines.Length, endLine ?? (start + MaxReadLines - 1));
        if (end - start + 1 > MaxReadLines)
            end = start + MaxReadLines - 1;

        if (start > lines.Length)
            return $"HATA: Dosya {lines.Length} satır; {start}. satırdan okunamaz.";

        var sb = new StringBuilder();
        sb.AppendLine($"Dosya: {repo}/{Relative(root, full)} (toplam {lines.Length} satır, gösterilen {start}-{end})");
        for (var i = start; i <= end; i++)
            sb.AppendLine($"{i,6}\t{lines[i - 1]}");

        if (end < lines.Length)
            sb.AppendLine($"... devamı için start_line={end + 1} ile tekrar okuyun.");

        return sb.ToString();
    }

    private (string Root, string Full) Resolve(string repo, string? relativePath)
    {
        if (!_repos.TryGetValue(repo, out var config))
            throw new ToolInputException($"Bilinmeyen depo: '{repo}'. Geçerli depolar: {string.Join(", ", _repos.Keys)}");

        var root = System.IO.Path.GetFullPath(config.Path);

        // Modelin sık yaptığı yol hatalarını düzelt: baştaki "/" ve yola eklenmiş repo adı
        // (ör. "/ProSourceCore/ProSource.BFF/..." veya "ProSourceCore/ProSourceCore/...").
        var cleaned = relativePath?.Replace('\\', '/').TrimStart('/');
        while (cleaned != null && cleaned.StartsWith(config.Name + "/", StringComparison.OrdinalIgnoreCase)
               && !Directory.Exists(System.IO.Path.Combine(root, config.Name)))
            cleaned = cleaned[(config.Name.Length + 1)..];

        var full = string.IsNullOrWhiteSpace(cleaned)
            ? root
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, cleaned));

        // Deponun dışına çıkılmasını engelle (ör. "../../etc/passwd").
        var rootWithSeparator = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;
        if (full != root && !full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ToolInputException("Depo klasörünün dışına erişilemez.");

        return (root, full);
    }

    private static IEnumerable<string> EnumerateFiles(string root, bool filterExtensions)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs, files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files)
            {
                if (!filterExtensions || SearchableExtensions.Contains(System.IO.Path.GetExtension(f)))
                    yield return f;
            }

            foreach (var d in subDirs)
            {
                if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(d)))
                    stack.Push(d);
            }
        }
    }

    private static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase);

    private static string Relative(string root, string full) =>
        System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string Truncate(string s) =>
        s.Length <= MaxLineLength ? s : s[..MaxLineLength] + " …";
}

public sealed class ToolInputException(string message) : Exception(message);
