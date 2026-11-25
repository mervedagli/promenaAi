using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KodAnalist;

/// <summary>Eşitlemenin anlık durumu (ekranda gösterilir).</summary>
public sealed class RepoSyncStatus
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RepoState> _repos = [];

    public sealed record RepoState(string Name, string Branch, string? Commit, DateTime? LastCheckAt, DateTime? LastChangeAt, string? Error);

    public bool Enabled { get; set; }
    public bool Running { get; private set; }
    public DateTime? MapBuiltAt { get; private set; }
    public string? MapError { get; private set; }

    public void SetRunning(bool running) { lock (_lock) Running = running; }

    public void SetRepo(RepoState state) { lock (_lock) _repos[state.Name] = state; }

    public RepoState? GetRepo(string name) { lock (_lock) return _repos.GetValueOrDefault(name); }

    public void SetMap(DateTime? builtAt, string? error) { lock (_lock) { if (builtAt != null) MapBuiltAt = builtAt; MapError = error; } }

    public object Snapshot()
    {
        lock (_lock)
            return new
            {
                enabled = Enabled,
                running = Running,
                mapBuiltAt = MapBuiltAt,
                mapError = MapError,
                repos = _repos.Values.OrderBy(r => r.Name).Select(r => new
                {
                    name = r.Name,
                    branch = r.Branch,
                    commit = r.Commit is { Length: > 8 } c ? c[..8] : r.Commit,
                    lastCheckAt = r.LastCheckAt,
                    lastChangeAt = r.LastChangeAt,
                    error = r.Error,
                }).ToList(),
            };
    }
}

/// <summary>
/// Repoları uygulamaya ait ayrı bir klasöre klonlar ve düzenli olarak izlenen dalın son halini çeker
/// (geliştiricinin kendi çalışma klasörüne dokunmaz). Salt okunurdur: hiçbir dala commit atmaz, push yapmaz;
/// klonlarda push adresi devre dışı bırakılır. Kod değiştiyse kod haritasını yeniden üretir;
/// harita yenilenince kural kataloğundaki "güncel / kod değişti" durumu da kendiliğinden güncellenir.
/// Git, bilgisayardaki kayıtlı kimlik bilgilerini (ör. Git Credential Manager) kullanır.
/// </summary>
public sealed partial class RepoSyncService(
    RepoSyncOptions options, List<RepoConfig> repos, CodeMapService codeMap, CodeTools tools,
    RepoSyncStatus status, IHostEnvironment env, ILogger<RepoSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MapBuildTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly SemaphoreSlim _runLock = new(1, 1);

    [GeneratedRegex(@"//[^/@\s]+@")]
    private static partial Regex CredentialInUrl();

    /// <summary>"Şimdi güncelle": bekleyen aralığı beklemeden bir eşitleme başlatır.</summary>
    public void TriggerNow()
    {
        if (_trigger.CurrentCount == 0) _trigger.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        status.Enabled = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncOnceAsync(stoppingToken);
            try
            {
                // Aralık dolana kadar veya "Şimdi güncelle" gelene kadar bekle.
                await _trigger.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return;
        status.SetRunning(true);
        try
        {
            var changed = false;
            foreach (var repo in repos.Where(r => !string.IsNullOrWhiteSpace(r.RemoteUrl)))
                changed |= await SyncRepoAsync(repo, ct);

            if (changed || !File.Exists(codeMap.FullPath))
                await BuildCodeMapAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Repo eşitlemesi başarısız");
        }
        finally
        {
            status.SetRunning(false);
            _runLock.Release();
        }
    }

    /// <returns>Kod değiştiyse (ilk klonlama dahil) true.</returns>
    private async Task<bool> SyncRepoAsync(RepoConfig repo, CancellationToken ct)
    {
        var branch = string.IsNullOrWhiteSpace(repo.Branch) ? options.Branch : repo.Branch;
        var dir = repo.Path;
        var previous = status.GetRepo(repo.Name);
        try
        {
            bool changed;
            if (!Directory.Exists(Path.Combine(dir, ".git")))
            {
                // Uygulamaya ait klasör: yalnızca izlenen dalın son hali (geçmiş gerekmez).
                logger.LogInformation("Klonlanıyor: {Repo} ({Branch}) → {Dir}", repo.Name, branch, dir);
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                await RunGitAsync(null, CloneTimeout, ct, "clone", "--depth", "1", "--branch", branch, "--single-branch", repo.RemoteUrl!, dir);
                await DisablePushAsync(dir, ct);
                changed = true;
            }
            else
            {
                await DisablePushAsync(dir, ct);   // önceden oluşturulmuş klonlar için de garanti
                await RunGitAsync(dir, FetchTimeout, ct, "fetch", "--depth", "1", "origin", branch);
                var head = (await RunGitAsync(dir, FetchTimeout, ct, "rev-parse", "HEAD")).Trim();
                var fetched = (await RunGitAsync(dir, FetchTimeout, ct, "rev-parse", "FETCH_HEAD")).Trim();
                changed = head != fetched;
                if (changed)
                {
                    // Bu klasör uygulamaya aittir; yerel değişiklik beklenmez, uzak dalın haline eşitlenir.
                    await RunGitAsync(dir, FetchTimeout, ct, "reset", "--hard", "FETCH_HEAD");
                    await RunGitAsync(dir, FetchTimeout, ct, "clean", "-fdq");
                    logger.LogInformation("Güncellendi: {Repo} {Old} → {New}", repo.Name, Short(head), Short(fetched));
                }
            }

            var commit = (await RunGitAsync(dir, FetchTimeout, ct, "rev-parse", "HEAD")).Trim();
            var now = DateTime.UtcNow;
            status.SetRepo(new RepoSyncStatus.RepoState(repo.Name, branch, commit, now,
                changed ? now : previous?.LastChangeAt, null));
            return changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = Sanitize(ex.Message);
            logger.LogError("Repo eşitlenemedi: {Repo}: {Error}", repo.Name, message);
            status.SetRepo(new RepoSyncStatus.RepoState(repo.Name, branch, previous?.Commit, DateTime.UtcNow,
                previous?.LastChangeAt, message));
            return false;
        }
    }

    /// <summary>KodHaritasi'ni eşitlenen klonlar üzerinde çalıştırır; harita dosyası tek adımda değiştirilir.</summary>
    private async Task BuildCodeMapAsync(CancellationToken ct)
    {
        var toolDir = Path.GetFullPath(options.CodeMapToolProject, env.ContentRootPath);
        var args = new List<string> { "run", "--project", toolDir, "-c", "Release", "--", "build", "--harita", codeMap.FullPath };
        foreach (var r in repos.Where(r => Directory.Exists(r.Path)))
        {
            args.Add("--repo");
            args.Add($"{r.Name}={r.Path}");
        }

        logger.LogInformation("Kod haritası yeniden üretiliyor…");
        try
        {
            await RunProcessAsync("dotnet", toolDir, MapBuildTimeout, ct, args.ToArray());
            tools.InvalidateFileCache();
            status.SetMap(DateTime.UtcNow, null);
            logger.LogInformation("Kod haritası yenilendi.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Kod haritası üretilemedi: {Error}", ex.Message);
            status.SetMap(null, "Kod haritası üretilemedi: " + ex.Message);
        }
    }

    /// <summary>
    /// Uygulama repolara ASLA commit atmaz ve push yapmaz. Yalnızca okuma amaçlı komutlara izin verilir;
    /// reset/clean yalnızca uygulamanın kendi klon klasöründeki çalışma kopyasını uzak dalın haline getirir.
    /// </summary>
    private static readonly HashSet<string> AllowedGitCommands = new(StringComparer.Ordinal)
    {
        "clone", "fetch", "rev-parse", "reset", "clean", "remote",
    };

    private Task<string> RunGitAsync(string? workDir, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        if (args.Length == 0 || !AllowedGitCommands.Contains(args[0]))
            throw new InvalidOperationException($"İzin verilmeyen git komutu: {args.FirstOrDefault()}. Uygulama repolara yazmaz.");
        if (args[0] == "remote" && !(args.Length >= 3 && args[1] == "set-url" && args[2] == "--push"))
            throw new InvalidOperationException("Yalnızca push adresini devre dışı bırakmak için 'git remote set-url --push' kullanılabilir.");
        return RunProcessAsync("git", workDir, timeout, ct, args);
    }

    /// <summary>Klondan push yapılmasını teknik olarak imkânsız hale getirir (push adresi geçersiz bir değere ayarlanır).</summary>
    private Task DisablePushAsync(string dir, CancellationToken ct) =>
        RunGitAsync(dir, FetchTimeout, ct, "remote", "set-url", "--push", "origin", "PUSH_DEVRE_DISI__KodAnalist_repolara_yazmaz");

    private static async Task<string> RunProcessAsync(string file, string? workDir, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Kimlik bilgisi sorulursa beklemeye takılmasın; kayıtlı kimlik bilgisi (Credential Manager) kullanılır.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{file} başlatılamadı.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* zaten bitmiş olabilir */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{file} {args.FirstOrDefault()} {timeout.TotalMinutes:F0} dakikada bitmedi.");
        }

        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            var output = (await stdout).Trim();
            var detail = string.IsNullOrEmpty(err) ? output : err;
            throw new InvalidOperationException($"{file} {args.FirstOrDefault()} başarısız (çıkış kodu {process.ExitCode}): " +
                                                Sanitize(detail.Length > 800 ? detail[^800..] : detail));
        }
        return await stdout;
    }

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    /// <summary>Hata mesajlarında adrese gömülü kimlik bilgisi (ör. https://token@dev.azure.com) görünmesin.</summary>
    private static string Sanitize(string text) => CredentialInUrl().Replace(text, "//***@");
}
