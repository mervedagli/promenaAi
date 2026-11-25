using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KodHaritasi;

/// <summary>
/// C# kaynak dosyalarını Roslyn sözdizimi ağacıyla okur: tipleri, metotları, metot içindeki
/// çağrıları, controller adreslerini ve kod içinde geçen API adreslerini toplar.
/// Derleme yapmaz (paket geri yükleme gerektirmez); çağrı hedefleri sonra <see cref="Linker"/> ile çözülür.
/// </summary>
public static class CSharpIndexer
{
    // Çağrı çözümlemesi için metot başına ham bilgi (JSON'a yazılmaz).
    public sealed class RawCall
    {
        public int Line;
        public string Text = "";
        public List<string> ReceiverChain = [];   // ör. ["_deps", "AggregatedAuctionBidClient"]
        public string MethodName = "";
        public string? MediatorRequestType;        // _mediator.Send(new XRequest())
        /// <summary>Metin argümanları: düz metin ("X") veya sabit adı ("Sinif.Sabit"); sabitler Linker'da çözülür.</summary>
        public List<string> StringArgs = [];
    }

    /// <summary>"Sinif.SABIT" → değer. Adres yardımcılarına verilen işlem adlarını çözmek için.</summary>
    public static Dictionary<string, string> Constants { get; } = [];

    public sealed class RawMethod
    {
        public MethodNode Node = null!;
        public TypeNode Owner = null!;
        public Dictionary<string, string> Locals = [];   // parametre + yerel değişken → tip
        public List<RawCall> Calls = [];
    }

    private static readonly Regex RouteLiteralRegex =
        new(@"^~?/?(api|v\d+)/[A-Za-z0-9_\-\[\]]+(/[A-Za-z0-9_\-{}\[\]]*)*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> HttpAttributes = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch",
    };

    public static void IndexFile(string repo, string repoRoot, string file, CodeMap map, List<RawMethod> rawMethods)
    {
        var text = File.ReadAllText(file);
        var tree = CSharpSyntaxTree.ParseText(text, path: file);
        var root = tree.GetCompilationUnitRoot();
        if (root.ContainsDiagnostics && root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            map.Stats.ParseErrors++;

        var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var type = new TypeNode
            {
                Repo = repo,
                Namespace = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "",
                Name = typeDecl.Identifier.Text,
                Kind = typeDecl switch
                {
                    InterfaceDeclarationSyntax => "interface",
                    RecordDeclarationSyntax => "record",
                    StructDeclarationSyntax => "struct",
                    _ => "class",
                },
                File = relative,
                Line = Line(tree, typeDecl.Identifier.Span),
                BaseTypes = typeDecl.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? [],
            };

            // Alanlar, özellikler, kurucu parametreleri (birincil kurucu dahil) → tip
            if (typeDecl.ParameterList != null)
                foreach (var p in typeDecl.ParameterList.Parameters.Where(p => p.Type != null))
                    type.Members[p.Identifier.Text] = TypeName(p.Type!);

            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var v in field.Declaration.Variables)
                        {
                            type.Members[v.Identifier.Text] = TypeName(field.Declaration.Type);
                            if (field.Modifiers.Any(SyntaxKind.ConstKeyword)
                                && v.Initializer?.Value is LiteralExpressionSyntax lit
                                && lit.IsKind(SyntaxKind.StringLiteralExpression))
                                Constants[$"{type.Name}.{v.Identifier.Text}"] = lit.Token.ValueText;
                        }
                        break;
                    case PropertyDeclarationSyntax prop:
                        type.Members[prop.Identifier.Text] = TypeName(prop.Type);
                        break;
                    case ConstructorDeclarationSyntax ctor:
                        foreach (var p in ctor.ParameterList.Parameters.Where(p => p.Type != null))
                            type.Members.TryAdd(p.Identifier.Text, TypeName(p.Type!));
                        break;
                }
            }

            map.Types.Add(type);

            // Controller adres öneki: [Route("api/[controller]")]
            var classRoute = GetAttributeArgument(typeDecl.AttributeLists, "Route");
            var isController = type.Name.EndsWith("Controller", StringComparison.Ordinal) && type.Kind == "class";
            var controllerName = isController ? type.Name[..^"Controller".Length] : "";

            var overloadIndex = new Dictionary<string, int>();
            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var name = method.Identifier.Text;
                overloadIndex[name] = overloadIndex.GetValueOrDefault(name) + 1;
                var node = new MethodNode
                {
                    // Farklı projelerde aynı adlı sınıflar olabildiği için kimlikte dosya yolu da var.
                    Id = $"{repo}:{relative}:{type.Name}.{name}#{overloadIndex[name]}",
                    Repo = repo,
                    Type = type.Name,
                    Name = name,
                    File = relative,
                    StartLine = Line(tree, method.Identifier.Span),
                    EndLine = tree.GetLineSpan(method.Span).EndLinePosition.Line + 1,
                };
                map.Methods.Add(node);

                var raw = new RawMethod { Node = node, Owner = type };
                foreach (var p in method.ParameterList.Parameters.Where(p => p.Type != null))
                    raw.Locals[p.Identifier.Text] = TypeName(p.Type!);

                CollectBody(tree, method, raw);
                rawMethods.Add(raw);

                if (isController && method.Modifiers.Any(SyntaxKind.PublicKeyword))
                    AddEndpoint(repo, map, method, node, classRoute, controllerName);
            }
        }
    }

    private static void CollectBody(SyntaxTree tree, MethodDeclarationSyntax method, RawMethod raw)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return;

        // Yerel değişkenler: "T x = ..." veya "var x = new T(...)"
        foreach (var decl in body.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            foreach (var v in decl.Variables)
            {
                var typeName = decl.Type.IsVar
                    ? (v.Initializer?.Value as ObjectCreationExpressionSyntax)?.Type is { } created ? TypeName(created) : null
                    : TypeName(decl.Type);
                if (typeName != null)
                    raw.Locals[v.Identifier.Text] = typeName;
            }
        }

        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var call = new RawCall { Line = Line(tree, inv.Span) };
            switch (inv.Expression)
            {
                case IdentifierNameSyntax id:
                    call.MethodName = id.Identifier.Text;
                    break;
                case GenericNameSyntax g:
                    call.MethodName = g.Identifier.Text;
                    break;
                case MemberAccessExpressionSyntax ma:
                    call.MethodName = ma.Name.Identifier.Text;
                    call.ReceiverChain = SplitChain(ma.Expression);
                    break;
                case MemberBindingExpressionSyntax mb when inv.Parent?.Parent is ConditionalAccessExpressionSyntax ca:
                    call.MethodName = mb.Name.Identifier.Text;
                    call.ReceiverChain = SplitChain(ca.Expression);
                    break;
                default:
                    continue;
            }

            // Çok genel / çerçeveye ait çağrıları atla (gürültü).
            if (call.MethodName is "nameof" or "ToString" or "Equals" or "GetHashCode" or "Add" or "Any"
                or "Where" or "Select" or "FirstOrDefault" or "First" or "ToList" or "Count" or "Contains"
                or "TryGetValue" or "AddRange" or "OrderBy" or "OrderByDescending" or "ToDictionary"
                or "SerializeObject" or "DeserializeObject" or "Format" or "Round" or "Max" or "Min" or "Sum")
                continue;

            call.Text = (call.ReceiverChain.Count > 0 ? string.Join(".", call.ReceiverChain) + "." : "") + call.MethodName;

            if (call.MethodName == "Send" && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is ObjectCreationExpressionSyntax req)
                call.MediatorRequestType = TypeName(req.Type);

            foreach (var arg in inv.ArgumentList.Arguments)
            {
                switch (arg.Expression)
                {
                    case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                        call.StringArgs.Add("\"" + lit.Token.ValueText);
                        break;
                    case MemberAccessExpressionSyntax ma:
                        // A.B.Sabit → "B.Sabit" (sabitin tanımlandığı sınıf + adı)
                        var chain = SplitChain(ma.Expression);
                        call.StringArgs.Add($"{chain[^1]}.{ma.Name.Identifier.Text}");
                        break;
                }
            }

            raw.Calls.Add(call);
        }

        // Kod içinde geçen API adresleri ("/v1/X/Y", "/api/X/Y")
        foreach (var literal in body.DescendantNodes().OfType<LiteralExpressionSyntax>()
                     .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            AddRouteLiteral(tree, raw, literal.Token.ValueText, literal.Span);
        }
        foreach (var interp in body.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
        {
            var textPart = string.Concat(interp.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "{}"));
            AddRouteLiteral(tree, raw, textPart, interp.Span);
        }
    }

    // Adres metnin ortasında da olabilir: $"{GetCurrentSystemApiURL()}/api/AuctionReport/AddAuctionReport"
    // (interpolasyon kısmı "{}" olarak gelir). Böyle durumlarda "/api/..." veya "/v1/..." kısmı alınır.
    private static readonly Regex EmbeddedRouteRegex =
        new(@"/(?:api|v\d+)/[A-Za-z0-9_\-\[\]]+(?:/[A-Za-z0-9_\-{}\[\]]*)*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void AddRouteLiteral(SyntaxTree tree, RawMethod raw, string value, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        var path = value.Split('?')[0].Trim();
        var m = RouteLiteralRegex.Match(path);
        var route = m.Success ? m.Value : null;
        if (route == null)
        {
            var embedded = EmbeddedRouteRegex.Match(path);
            if (embedded.Success && embedded.Index > 0) route = embedded.Value;
        }
        if (route == null || route.Count(c => c == '/') < 2) return;
        raw.Node.RouteLiterals.Add(new RouteLiteral { Line = Line(tree, span), Route = "/" + route.TrimStart('~', '/') });
    }

    private static void AddEndpoint(string repo, CodeMap map, MethodDeclarationSyntax method, MethodNode node,
        string? classRoute, string controllerName)
    {
        string? httpMethod = null;
        string? methodRoute = null;
        foreach (var attr in method.AttributeLists.SelectMany(a => a.Attributes))
        {
            var attrName = attr.Name.ToString().Replace("Attribute", "");
            if (HttpAttributes.Contains(attrName))
            {
                httpMethod = attrName[4..].ToUpperInvariant();
                methodRoute ??= FirstStringArgument(attr);
            }
            else if (attrName == "Route")
            {
                methodRoute = FirstStringArgument(attr) ?? methodRoute;
            }
            else if (attrName == "NonAction")
            {
                return;
            }
        }

        string route;
        var conventional = false;
        if (methodRoute != null && (methodRoute.StartsWith('/') || methodRoute.StartsWith("~/")))
            route = methodRoute.TrimStart('~');
        else if (classRoute != null)
            route = "/" + classRoute.Trim('/') + (string.IsNullOrEmpty(methodRoute) ? "" : "/" + methodRoute.Trim('/'));
        else if (methodRoute != null)
            route = "/" + methodRoute.Trim('/');
        else
        {
            // Attribute route yok: klasik MVC yönlendirmesi varsayılır.
            route = $"/{controllerName}/{node.Name}";
            conventional = true;
        }

        route = route.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                     .Replace("[action]", node.Name, StringComparison.OrdinalIgnoreCase);

        map.Endpoints.Add(new Endpoint
        {
            Repo = repo,
            HttpMethod = httpMethod ?? "ANY",
            Route = route,
            MethodId = node.Id,
            Conventional = conventional,
        });
    }

    private static string? GetAttributeArgument(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(a => a.Attributes)
             .Where(a => a.Name.ToString().Replace("Attribute", "") == name)
             .Select(FirstStringArgument)
             .FirstOrDefault(s => s != null);

    private static string? FirstStringArgument(AttributeSyntax attr) =>
        attr.ArgumentList?.Arguments
            .Select(a => a.Expression)
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .FirstOrDefault();

    /// <summary>"_a.B.C" → ["_a","B","C"]. Çağrı içeren zincirlerde (a.Get().B) yalnızca son düz kısım alınır.</summary>
    private static List<string> SplitChain(ExpressionSyntax expr)
    {
        var parts = new List<string>();
        var current = expr;
        while (true)
        {
            switch (current)
            {
                case MemberAccessExpressionSyntax ma:
                    parts.Insert(0, ma.Name.Identifier.Text);
                    current = ma.Expression;
                    continue;
                case IdentifierNameSyntax id:
                    parts.Insert(0, id.Identifier.Text);
                    return parts;
                case ThisExpressionSyntax:
                    parts.Insert(0, "this");
                    return parts;
                case BaseExpressionSyntax:
                    parts.Insert(0, "base");
                    return parts;
                case GenericNameSyntax g:
                    parts.Insert(0, g.Identifier.Text);
                    return parts;
                case ParenthesizedExpressionSyntax p:
                    current = p.Expression;
                    continue;
                case AwaitExpressionSyntax a:
                    current = a.Expression;
                    continue;
                default:
                    parts.Insert(0, "?");   // çözülemeyen alıcı (ör. metot dönüş değeri)
                    return parts;
            }
        }
    }

    /// <summary>Tip adını sadeleştirir: "List&lt;Foo&gt;?" → "List", "Ns.IFoo" → "IFoo".</summary>
    public static string TypeName(TypeSyntax type) => SimpleTypeName(type.ToString());

    public static string SimpleTypeName(string type)
    {
        var t = type.Trim().TrimEnd('?');
        var genericStart = t.IndexOf('<');
        if (genericStart >= 0) t = t[..genericStart];
        var lastDot = t.LastIndexOf('.');
        return lastDot >= 0 ? t[(lastDot + 1)..] : t;
    }

    private static int Line(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span) =>
        tree.GetLineSpan(span).StartLinePosition.Line + 1;
}
