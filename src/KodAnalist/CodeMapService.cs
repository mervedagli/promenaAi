using System.Text.Json;
using KodHaritasi;

namespace KodAnalist;

public sealed class CodeMapOptions
{
    /// <summary>KodHaritasi'nin ürettiği codemap.json dosyasının yolu.</summary>
    public string Path { get; set; } = "../../data/codemap.json";
}

/// <summary>
/// Kod haritasını yükler ve sorgular. Dosya yeniden üretilirse (KodHaritasi build) bir sonraki
/// sorguda otomatik olarak yeniden okunur.
/// </summary>
public sealed class CodeMapService(CodeMapOptions options, ILogger<CodeMapService> logger)
{
    private readonly object _lock = new();
    private Queries? _queries;
    private CodeMap? _map;
    private DateTime _loadedWriteTime;
    private DateTime _generatedAt;

    public string FullPath => System.IO.Path.GetFullPath(options.Path);

    /// <summary>Ham harita ve sorgular (harita yoksa null). Version, harita yeniden üretildiğinde değişir.</summary>
    public (CodeMap Map, Queries Queries, DateTime Version)? Current
    {
        get
        {
            var q = Load();
            return q == null || _map == null ? null : (_map, q, _loadedWriteTime);
        }
    }

    public string Trace(string target, int depth) => Run(q => q.Trace(target, Math.Clamp(depth, 1, 6)));

    public string Callers(string target, int depth) => Run(q => q.Callers(target, Math.Clamp(depth, 1, 6)));

    public string Find(string text) => Run(q => q.Find(text));

    private string Run(Func<Queries, string> query)
    {
        var q = Load();
        if (q == null)
            return "HATA: Kod haritası bulunamadı. Yönetici 'KodHaritasi build' komutunu çalıştırmalı. " +
                   "Bu soruda search_code / read_file ile devam et.";

        var result = query(q);
        return $"(Harita üretilme zamanı: {_generatedAt:yyyy-MM-dd HH:mm}. Satır numaraları bu andaki koda aittir; " +
               "kesin bilgi için read_file ile doğrula.)\n" + result;
    }

    private Queries? Load()
    {
        var path = FullPath;
        if (!File.Exists(path)) return null;

        var writeTime = File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_queries != null && writeTime == _loadedWriteTime) return _queries;

            var map = JsonSerializer.Deserialize<CodeMap>(File.ReadAllText(path))!;
            _queries = new Queries(map);
            _map = map;
            _loadedWriteTime = writeTime;
            _generatedAt = map.GeneratedAt;
            logger.LogInformation("Kod haritası yüklendi: {Methods} metot, {Endpoints} adres ({Path})",
                map.Methods.Count, map.Endpoints.Count, path);
            return _queries;
        }
    }
}
