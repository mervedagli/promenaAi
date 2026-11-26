using System.Collections.Concurrent;
using System.Text.Json;

namespace KodAnalist;

/// <summary>
/// Bir sorunun incelemesi. Tarayıcı bağlantısından bağımsız arka planda çalışır; ürettiği olaylar sırayla
/// saklanır. Bağlantı koparsa tarayıcı aynı incelemeye yeniden bağlanıp kaldığı yerden devam eder; sayfa
/// yenilenirse olaylar baştan tekrar oynatılır. Böylece kısa bir ağ kesintisi incelemeyi ve cevabı kaybettirmez.
/// </summary>
public sealed class AskJob
{
    private readonly object _lock = new();
    private readonly List<AgentEvent> _events = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _subscribers;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; private set; }
    public bool Completed => CompletedAt != null;

    /// <summary>Son bağlı tarayıcının ayrıldığı an (hiç kimse bağlı değilken dolu).</summary>
    public DateTime? DetachedAt { get; private set; }

    /// <summary>İncelemeyi durdurur (ör. kimse uzun süre bağlanmazsa). Tarayıcı bağlantısına bağlı değildir.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public void Add(AgentEvent e)
    {
        lock (_lock)
        {
            _events.Add(e);
            Signal();
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            CompletedAt ??= DateTime.UtcNow;
            Signal();
        }
    }

    /// <summary><paramref name="from"/> sırasından itibaren olaylar, bitip bitmediği ve yeni olay gelince tamamlanan görev.</summary>
    public (List<AgentEvent> Events, bool Completed, Task Changed) Read(int from)
    {
        lock (_lock)
            return (_events.Skip(Math.Max(0, from)).ToList(), Completed, _changed.Task);
    }

    public void Attach()
    {
        lock (_lock) { _subscribers++; DetachedAt = null; }
    }

    public void Detach()
    {
        lock (_lock) { if (--_subscribers <= 0) { _subscribers = 0; DetachedAt = DateTime.UtcNow; } }
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}

/// <summary>
/// Süren ve yeni biten incelemeleri bellekte tutar. Arka planda temizlik yapar:
/// - Hiçbir tarayıcının bağlı olmadığı, <see cref="AbandonAfter"/> süredir sahipsiz kalan inceleme durdurulur
///   (boşuna model maliyeti oluşmasın).
/// - Biten incelemeler <see cref="KeepCompletedFor"/> süre saklanır (sayfa yenilenince cevap geri gelsin), sonra silinir.
/// Uygulama tek sunucuda çalıştığı için bellek yeterlidir; sunucu yeniden başlarsa süren incelemeler kaybolur.
/// </summary>
public sealed class AskJobStore(ILogger<AskJobStore> logger) : BackgroundService
{
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan KeepCompletedFor = TimeSpan.FromMinutes(60);

    /// <summary>Olay gelmezken bu aralıkla boş bir "hâlâ çalışıyorum" satırı gönderilir; ağ cihazları bağlantıyı kesmez.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, AskJob> _jobs = new();

    public AskJob Create()
    {
        var job = new AskJob();
        _jobs[job.Id] = job;
        return job;
    }

    public AskJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>
    /// İncelemenin olaylarını Server-Sent Events olarak akıtır: <paramref name="from"/> sırasından başlar, yeni olayları
    /// geldikçe gönderir, olay yokken düzenli aralıkla boş satır yollar ve inceleme bitince "done" olayıyla kapanır.
    /// Her olayın sıra numarası (Seq) vardır; tarayıcı yeniden bağlanırken kaldığı sırayı bildirir.
    /// </summary>
    public static async Task StreamAsync(AskJob job, int from, HttpContext http)
    {
        var ct = http.RequestAborted;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";   // araya giren proxy'ler akışı biriktirmesin

        async Task Write(object payload)
        {
            await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        }

        job.Attach();
        try
        {
            await Write(new { Seq = -1, Type = "job", Text = job.Id });
            await http.Response.Body.FlushAsync(ct);

            var next = Math.Max(0, from);
            while (true)
            {
                var (events, completed, changed) = job.Read(next);
                foreach (var e in events)
                    await Write(new { Seq = next++, e.Type, e.Text });
                if (events.Count > 0)
                    await http.Response.Body.FlushAsync(ct);

                if (completed && events.Count == 0)
                {
                    await Write(new { Seq = -1, Type = "done", Text = "" });
                    await http.Response.Body.FlushAsync(ct);
                    return;
                }
                if (events.Count > 0) continue;   // okurken yenileri gelmiş olabilir

                var winner = await Task.WhenAny(changed, Task.Delay(HeartbeatInterval, ct));
                ct.ThrowIfCancellationRequested();
                if (winner != changed)
                {
                    await http.Response.WriteAsync(": hala-calisiyor\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Tarayıcı ayrıldı; inceleme arka planda sürer, tarayıcı yeniden bağlanabilir.
        }
        catch (IOException)
        {
            // Bağlantı koptu; aynı şekilde inceleme sürer.
        }
        finally
        {
            job.Detach();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                foreach (var job in _jobs.Values)
                {
                    if (!job.Completed && job.DetachedAt is { } detached && now - detached > AbandonAfter
                        && !job.Cancellation.IsCancellationRequested)
                    {
                        logger.LogInformation("İnceleme durduruldu: {Minutes} dakikadır bağlı tarayıcı yok ({Id})",
                            AbandonAfter.TotalMinutes, job.Id);
                        job.Cancellation.Cancel();
                    }
                    if (job.CompletedAt is { } done && now - done > KeepCompletedFor && _jobs.TryRemove(job.Id, out var removed))
                        removed.Cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
