using System.Text.RegularExpressions;

namespace KodHaritasi;

/// <summary>
/// Toplanan ham çağrıları hedef metotlara bağlar:
/// - doğrudan çağrılar (aynı sınıf, alan/özellik zinciri, statik çağrı),
/// - arayüz → uygulayan sınıf (taban sınıflardaki metotlar dahil),
/// - MediatR: _mediator.Send(new XRequest()) → IRequestHandler&lt;XRequest&gt;.Handle,
/// - HTTP: kodda geçen "/v1/X/Y" adresi → o adresi karşılayan controller metodu,
/// - Frontend: ekrandaki "/api/X/Y" çağrısı → BFF controller metodu.
/// </summary>
public sealed class Linker
{
    private const int MaxBaseDepth = 8;

    private readonly CodeMap _map;
    private readonly Dictionary<string, List<TypeNode>> _typesByName;
    private readonly Dictionary<TypeNode, Dictionary<string, List<MethodNode>>> _methodsByType = [];
    private readonly Dictionary<string, List<TypeNode>> _implementations = [];
    private readonly List<(string[] Segments, Endpoint Endpoint)> _endpointSegments;

    public Linker(CodeMap map)
    {
        _map = map;
        _typesByName = map.Types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToList());

        // Metotları sahip tipe göre grupla (aynı ad + aynı dosya + aynı repo).
        var typesByKey = map.Types.GroupBy(t => $"{t.Repo}|{t.Name}|{t.File}").ToDictionary(g => g.Key, g => g.ToList());
        foreach (var m in map.Methods)
        {
            if (!typesByKey.TryGetValue($"{m.Repo}|{m.Type}|{m.File}", out var owners)) continue;
            foreach (var owner in owners)
            {
                if (!_methodsByType.TryGetValue(owner, out var byName))
                    _methodsByType[owner] = byName = [];
                if (!byName.TryGetValue(m.Name, out var list))
                    byName[m.Name] = list = [];
                list.Add(m);
            }
        }

        // Arayüz / taban sınıf → onu uygulayan / ondan türeyen tipler
        foreach (var t in map.Types)
        {
            foreach (var bt in t.BaseTypes)
            {
                var simple = CSharpIndexer.SimpleTypeName(bt);
                if (!_implementations.TryGetValue(simple, out var list))
                    _implementations[simple] = list = [];
                list.Add(t);
            }
        }

        _endpointSegments = map.Endpoints.Select(e => (Segments(e.Route), e)).ToList();
    }

    public void Link(List<CSharpIndexer.RawMethod> rawMethods)
    {
        foreach (var raw in rawMethods)
        {
            foreach (var call in raw.Calls)
            {
                _map.Stats.TotalCalls++;
                var site = new CallSite { Line = call.Line, Text = call.Text };

                if (call.MediatorRequestType != null && LooksLikeMediator(raw, call))
                {
                    site.Targets = FindMediatorHandlers(call.MediatorRequestType, raw.Owner.Repo);
                    site.Via = "mediator";
                    if (site.Targets.Count > 0) _map.Stats.MediatorBridges++;
                }

                if (site.Targets.Count == 0)
                {
                    var (targets, via) = ResolveCall(raw, call);
                    site.Targets = targets;
                    site.Via = via;
                }

                if (site.Targets.Count > 0)
                {
                    _map.Stats.ResolvedCalls++;
                    raw.Node.Calls.Add(site);
                    AddTemplatedHttpBridge(raw, call, site);
                }
            }

            // Kodda geçen API adresleri → controller metotları
            foreach (var literal in raw.Node.RouteLiterals)
            {
                var targets = MatchEndpoints(literal.Route)
                    .Where(e => e.MethodId != raw.Node.Id)
                    .Select(e => e.MethodId)
                    .Distinct()
                    .ToList();
                if (targets.Count == 0) continue;

                _map.Stats.HttpBridges++;
                raw.Node.Calls.Add(new CallSite { Line = literal.Line, Text = literal.Route, Targets = targets, Via = "http" });
            }

            raw.Node.Calls = raw.Node.Calls.OrderBy(c => c.Line).ToList();
        }

        foreach (var fc in _map.FrontendCalls)
        {
            fc.EndpointMethodIds = MatchEndpoints(fc.Route).Select(e => e.MethodId).Distinct().ToList();
            if (fc.EndpointMethodIds.Count > 0) _map.Stats.FrontendCallsMatched++;
        }
    }

    /// <summary>
    /// Adres yardımcıları: AuctionEndPoints.GetAuctionActionUrl(tip, AuctionAction.CreateAuctionActionUrl)
    /// çağrısında hedef metot "/v1/AuctionAction/{}" gibi şablonlar içerir. Çağrıdaki işlem adı şablona
    /// yerleştirilip gerçek controller metodu bulunur (ihale tipi çalışma anında belli olduğu için
    /// tip başına bir hedef çıkabilir).
    /// </summary>
    private void AddTemplatedHttpBridge(CSharpIndexer.RawMethod raw, CSharpIndexer.RawCall call, CallSite site)
    {
        if (call.StringArgs.Count == 0) return;

        var values = call.StringArgs
            .Select(a => a.StartsWith('"') ? a[1..] : CSharpIndexer.Constants.GetValueOrDefault(a))
            .Where(v => !string.IsNullOrWhiteSpace(v) && !v!.Contains('/'))
            .Cast<string>()
            .ToList();
        if (values.Count == 0) return;

        var templates = site.Targets
            .Select(id => _methodsByIdCache.GetValueOrDefault(id))
            .Where(m => m != null)
            .SelectMany(m => m!.RouteLiterals)
            .Select(r => r.Route)
            .Where(r => r.Contains("{}"))
            .Distinct()
            .ToList();
        if (templates.Count == 0) return;

        var targets = templates
            .SelectMany(t => values.Select(v => t.Replace("{}", v)))
            .SelectMany(MatchEndpoints)
            .Select(e => e.MethodId)
            .Distinct()
            .ToList();
        if (targets.Count == 0) return;

        _map.Stats.HttpBridges++;
        raw.Node.Calls.Add(new CallSite
        {
            Line = call.Line,
            Text = $"{call.MethodName}(… \"{values[0]}\")",
            Targets = targets,
            Via = "http",
        });
    }

    private Dictionary<string, MethodNode>? _methodsByIdField;
    private Dictionary<string, MethodNode> _methodsByIdCache => _methodsByIdField ??= _map.Methods.ToDictionary(m => m.Id);

    private static bool LooksLikeMediator(CSharpIndexer.RawMethod raw, CSharpIndexer.RawCall call)
    {
        if (call.ReceiverChain.Count == 0) return false;
        var receiver = call.ReceiverChain[^1];
        return receiver.Contains("mediator", StringComparison.OrdinalIgnoreCase)
               || (raw.Owner.Members.TryGetValue(receiver, out var t) && t.Contains("Mediator", StringComparison.OrdinalIgnoreCase));
    }

    private readonly Dictionary<(string, string), List<string>> _mediatorCache = [];

    private List<string> FindMediatorHandlers(string requestType, string repo)
    {
        if (_mediatorCache.TryGetValue((requestType, repo), out var cached)) return cached;
        return _mediatorCache[(requestType, repo)] = FindMediatorHandlersUncached(requestType, repo);
    }

    private List<string> FindMediatorHandlersUncached(string requestType, string repo)
    {
        var pattern = new Regex($@"IRequestHandler\s*<\s*([\w.]+\.)?{Regex.Escape(requestType)}\s*[,>]");
        return _map.Types
            .Where(t => t.BaseTypes.Any(b => pattern.IsMatch(b)))
            .OrderByDescending(t => t.Repo == repo)
            .SelectMany(t => MethodsNamed(t, "Handle"))
            .Select(m => m.Id)
            .Distinct()
            .ToList();
    }

    private (List<string> Targets, string Via) ResolveCall(CSharpIndexer.RawMethod raw, CSharpIndexer.RawCall call)
    {
        var owner = raw.Owner;

        if (call.ReceiverChain.Count == 0)
            return (FindInHierarchy(owner, call.MethodName).Select(m => m.Id).ToList(), "direct");

        var first = call.ReceiverChain[0];
        TypeNode? current;
        if (first is "this" or "base")
            current = owner;
        else if (first == "?")
            return ([], "direct");
        else if (raw.Locals.TryGetValue(first, out var localType))
            current = FindType(localType, owner.Repo);
        else if (MemberType(owner, first) is { } memberType)
            current = FindType(memberType, owner.Repo);
        else
            current = FindType(first, owner.Repo);   // statik çağrı: TipAdı.Metot()

        for (var i = 1; current != null && i < call.ReceiverChain.Count; i++)
        {
            var memberType = MemberType(current, call.ReceiverChain[i]);
            current = memberType == null ? null : FindType(memberType, current.Repo);
        }

        if (current == null)
            return ([], "direct");

        if (current.Kind == "interface")
        {
            var impl = Implementations(current)
                .SelectMany(t => FindInHierarchy(t, call.MethodName))
                .Select(m => m.Id)
                .Distinct()
                .ToList();
            if (impl.Count > 0)
                return (impl, "interface");
        }

        return (FindInHierarchy(current, call.MethodName).Select(m => m.Id).ToList(), "direct");
    }

    /// <summary>Bir arayüzü doğrudan veya dolaylı (alt arayüz üzerinden) uygulayan somut tipler.</summary>
    private IEnumerable<TypeNode> Implementations(TypeNode iface)
    {
        var result = new HashSet<TypeNode>();
        var queue = new Queue<(string Name, int Depth)>();
        queue.Enqueue((iface.Name, 0));
        var seen = new HashSet<string>();
        while (queue.Count > 0)
        {
            var (name, depth) = queue.Dequeue();
            if (!seen.Add(name) || depth > MaxBaseDepth) continue;
            if (!_implementations.TryGetValue(name, out var subs)) continue;
            // Aynı repodaki uygulamaları tercih et; yoksa diğer repolardakileri kullan.
            var preferred = subs.Any(s => s.Repo == iface.Repo) ? subs.Where(s => s.Repo == iface.Repo) : subs;
            foreach (var s in preferred)
            {
                if (s.Kind == "interface") queue.Enqueue((s.Name, depth + 1));
                else result.Add(s);
            }
        }
        return result;
    }

    /// <summary>Metodu tipin kendisinde, sonra taban sınıflarında arar; bulduğu ilk seviyeyi döner.</summary>
    private List<MethodNode> FindInHierarchy(TypeNode type, string methodName)
    {
        var visited = new HashSet<TypeNode>();
        var level = new List<TypeNode> { type };
        for (var depth = 0; depth <= MaxBaseDepth && level.Count > 0; depth++)
        {
            var found = level.SelectMany(t => MethodsNamed(t, methodName)).ToList();
            if (found.Count > 0) return found;

            level = level
                .Where(visited.Add)
                .SelectMany(t => t.BaseTypes)
                .Select(bt => FindType(CSharpIndexer.SimpleTypeName(bt), type.Repo))
                .Where(t => t != null && t.Kind != "interface")
                .Cast<TypeNode>()
                .Distinct()
                .ToList();
        }
        return [];
    }

    private IEnumerable<MethodNode> MethodsNamed(TypeNode type, string name)
    {
        // partial sınıflar birden fazla dosyada olabilir: aynı ad ve repodaki tüm parçalara bak.
        if (!_typesByName.TryGetValue(type.Name, out var parts)) yield break;
        foreach (var part in parts.Where(p => p.Repo == type.Repo && p.Namespace == type.Namespace))
            if (_methodsByType.TryGetValue(part, out var byName) && byName.TryGetValue(name, out var list))
                foreach (var m in list) yield return m;
    }

    private string? MemberType(TypeNode type, string member)
    {
        var visited = new HashSet<TypeNode>();
        var current = type;
        for (var depth = 0; current != null && depth <= MaxBaseDepth && visited.Add(current); depth++)
        {
            foreach (var part in _typesByName.GetValueOrDefault(current.Name, []).Where(p => p.Repo == current.Repo))
                if (part.Members.TryGetValue(member, out var t)) return t;

            current = current.BaseTypes
                .Select(bt => FindType(CSharpIndexer.SimpleTypeName(bt), current.Repo))
                .FirstOrDefault(t => t != null && t.Kind != "interface");
        }
        return null;
    }

    private TypeNode? FindType(string name, string preferredRepo)
    {
        if (!_typesByName.TryGetValue(CSharpIndexer.SimpleTypeName(name), out var list)) return null;
        return list.FirstOrDefault(t => t.Repo == preferredRepo) ?? list[0];
    }

    private IEnumerable<Endpoint> MatchEndpoints(string route)
    {
        var segs = Segments(route);
        foreach (var (epSegs, ep) in _endpointSegments)
        {
            if (epSegs.Length != segs.Length) continue;
            var ok = true;
            for (var i = 0; i < segs.Length && ok; i++)
            {
                var a = segs[i];
                var b = epSegs[i];
                ok = a == b || IsParam(a) || IsParam(b);
            }
            if (ok) yield return ep;
        }
    }

    private static bool IsParam(string seg) => seg.StartsWith('{') || seg.Length == 0;

    private static string[] Segments(string route) =>
        route.Split('?')[0].Trim('/').ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
}
