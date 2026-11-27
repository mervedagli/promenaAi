namespace KodAnalist;

/// <summary>"Onaylı kurallar" sayfasının arama, filtre, sıralama ve sayfa isteği.</summary>
public sealed record CatalogBrowseQuery(
    string? Q = null,
    string? Scope = null,
    string? Topic = null,
    string? Status = null,       // "uptodate" | "changed"
    string? Verified = null,     // "yes" | "no"
    string? ApprovedBy = null,
    string? Sort = null,         // "newest" (varsayılan) | "oldest" | "question"
    int Page = 1,
    int PageSize = 20);

public sealed record CatalogBrowseItem(
    string Id, string ScopeId, string? TopicId, string Question, string ApprovedBy, DateTime ApprovedAt,
    bool IsVerified, string? Mode, bool UpToDate, List<string> Changes, string? Snippet);

/// <summary>Durum sayıları: arama ve ihale tipi / konu / onaylayan filtresine göre, durum filtrelerinden önce.</summary>
public sealed record CatalogBrowseCounts(int Total, int UpToDate, int Changed, int Unverified);

public sealed record CatalogBrowseResult(
    int Total, int Page, int PageSize, int TotalPages, List<CatalogBrowseItem> Items,
    CatalogBrowseCounts Counts, List<string> Approvers);

/// <summary>
/// Onaylı kuralları aramaya, filtrelere ve sıralamaya göre süzüp sayfalar. Model kullanmaz; veritabanından gelen
/// kayıtlar ve bugünkü koda göre "güncel / kod değişti" durumları üzerinde çalışır (Cosmos'a bağlı değil, test edilebilir).
/// </summary>
public static class CatalogBrowser
{
    public const int MaxPageSize = 100;

    public static CatalogBrowseResult Browse(IEnumerable<(CatalogEntry Entry, CatalogFreshness Fresh)> source, CatalogBrowseQuery q)
    {
        var all = source.ToList();
        var terms = string.IsNullOrWhiteSpace(q.Q) ? [] : QuestionMatcher.SearchTerms(q.Q);

        // Onaylayan listesi (filtre seçenekleri) arama ve diğer filtrelerden bağımsız: her zaman tümü.
        var approvers = all.Select(x => x.Entry.ApprovedBy).Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.Create(new("tr-TR"), true)).ToList();

        // 1) Arama + ihale tipi / konu / onaylayan.
        var matched = all.Where(x =>
            (string.IsNullOrEmpty(q.Scope) || x.Entry.ScopeId == q.Scope) &&
            (string.IsNullOrEmpty(q.Topic) || x.Entry.TopicId == q.Topic) &&
            (string.IsNullOrEmpty(q.ApprovedBy) || string.Equals(x.Entry.ApprovedBy, q.ApprovedBy, StringComparison.OrdinalIgnoreCase)) &&
            (terms.Count == 0 || QuestionMatcher.ContainsAllTerms($"{x.Entry.Question}\n{x.Entry.Answer}\n{x.Entry.Note}\n{x.Entry.ApprovedBy}", terms)))
            .ToList();

        var counts = new CatalogBrowseCounts(
            matched.Count,
            matched.Count(x => x.Fresh.UpToDate),
            matched.Count(x => !x.Fresh.UpToDate),
            matched.Count(x => !x.Entry.IsVerified));

        // 2) Durum filtreleri.
        var filtered = matched.Where(x =>
            (q.Status != "uptodate" || x.Fresh.UpToDate) &&
            (q.Status != "changed" || !x.Fresh.UpToDate) &&
            (q.Verified != "yes" || x.Entry.IsVerified) &&
            (q.Verified != "no" || !x.Entry.IsVerified));

        // 3) Sıralama. Aramada sorusunda geçenler önce gelir.
        IOrderedEnumerable<(CatalogEntry Entry, CatalogFreshness Fresh)> ordered = q.Sort switch
        {
            "oldest" => filtered.OrderBy(x => terms.Count > 0 && !QuestionMatcher.ContainsAllTerms(x.Entry.Question, terms)).ThenBy(x => x.Entry.ApprovedAt),
            "question" => filtered.OrderBy(x => terms.Count > 0 && !QuestionMatcher.ContainsAllTerms(x.Entry.Question, terms))
                .ThenBy(x => x.Entry.Question, StringComparer.Create(new("tr-TR"), true)),
            _ => filtered.OrderBy(x => terms.Count > 0 && !QuestionMatcher.ContainsAllTerms(x.Entry.Question, terms)).ThenByDescending(x => x.Entry.ApprovedAt),
        };
        var list = ordered.ToList();

        // 4) Sayfalama.
        var pageSize = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)pageSize));
        var page = Math.Clamp(q.Page, 1, totalPages);
        var items = list.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new CatalogBrowseItem(
            x.Entry.Id, x.Entry.ScopeId, x.Entry.TopicId, x.Entry.Question, x.Entry.ApprovedBy, x.Entry.ApprovedAt,
            x.Entry.IsVerified, x.Entry.Mode, x.Fresh.UpToDate, x.Fresh.Changes,
            terms.Count > 0 && !QuestionMatcher.ContainsAllTerms(x.Entry.Question, terms)
                ? QuestionMatcher.Snippet($"{x.Entry.Answer}\n{x.Entry.Note}", terms)
                : null)).ToList();

        return new CatalogBrowseResult(list.Count, page, pageSize, totalPages, items, counts, approvers);
    }
}
