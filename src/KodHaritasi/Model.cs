namespace KodHaritasi;

/// <summary>Kod haritasının tamamı. JSON olarak diske yazılır, sorgular buradan okunur.</summary>
public sealed class CodeMap
{
    public DateTime GeneratedAt { get; set; }
    public List<RepoSnapshot> Repos { get; set; } = [];
    public List<TypeNode> Types { get; set; } = [];
    public List<MethodNode> Methods { get; set; } = [];
    public List<Endpoint> Endpoints { get; set; } = [];
    public List<FrontendCall> FrontendCalls { get; set; } = [];
    public Stats Stats { get; set; } = new();
}

public sealed class RepoSnapshot
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Haritanın üretildiği andaki git commit'i (biliniyorsa).</summary>
    public string? Commit { get; set; }
}

public sealed class TypeNode
{
    public string Repo { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";          // class, interface, record, struct
    public string File { get; set; } = "";
    public int Line { get; set; }
    /// <summary>Taban sınıf ve arayüzler, yazıldığı gibi (ör. "IRequestHandler&lt;GetXRequest, GetXResponse&gt;").</summary>
    public List<string> BaseTypes { get; set; } = [];
    /// <summary>Alan / özellik / kurucu parametresi adı → tip adı.</summary>
    public Dictionary<string, string> Members { get; set; } = [];
}

public sealed class MethodNode
{
    /// <summary>Benzersiz kimlik: "Repo:dosya/yolu.cs:Tip.Metot#n" (n: aynı adlı aşırı yüklemeler için sıra).</summary>
    public string Id { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<CallSite> Calls { get; set; } = [];
    /// <summary>Metot gövdesinde geçen API adresleri (ör. "/v1/AggregatedAuctionBid/SendBid").</summary>
    public List<RouteLiteral> RouteLiterals { get; set; } = [];
}

public sealed class CallSite
{
    public int Line { get; set; }
    public string Text { get; set; } = "";               // ör. "_service.AdvancedSendBid"
    public List<string> Targets { get; set; } = [];       // çözümlenen MethodNode kimlikleri
    public string Via { get; set; } = "direct";           // direct, interface, mediator, http
}

public sealed class RouteLiteral
{
    public int Line { get; set; }
    public string Route { get; set; } = "";
}

public sealed class Endpoint
{
    public string Repo { get; set; } = "";
    public string HttpMethod { get; set; } = "";           // POST, GET, ... (bilinmiyorsa ANY)
    public string Route { get; set; } = "";               // ör. "/api/AggregatedAuctionSupplier/SendBid"
    public string MethodId { get; set; } = "";
    public bool Conventional { get; set; }                  // attribute route yok, {controller}/{action} varsayıldı
}

public sealed class FrontendCall
{
    public string Repo { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Function { get; set; } = "";
    public string HttpHelper { get; set; } = "";           // postMethod, requestMethod, ...
    public string Route { get; set; } = "";
    public List<string> EndpointMethodIds { get; set; } = [];
}

public sealed class Stats
{
    public int CSharpFiles { get; set; }
    public int JsFiles { get; set; }
    public int ParseErrors { get; set; }
    public int TotalCalls { get; set; }
    public int ResolvedCalls { get; set; }
    public int HttpBridges { get; set; }
    public int MediatorBridges { get; set; }
    public int FrontendCallsMatched { get; set; }
    public double Seconds { get; set; }
}
