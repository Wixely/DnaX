using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DnaX.MCPFab.Tooling;

/// <summary>A call site the rewriter declined to change, and why.</summary>
public sealed record McpJsonSkip(int Line, string Expression, string Reason);

/// <summary>The outcome of rewriting one syntax tree.</summary>
public sealed record McpJsonRewriteResult(SyntaxNode Root, int Rewritten, IReadOnlyList<McpJsonSkip> Skipped)
{
    /// <summary>Whether anything changed.</summary>
    public bool Changed => Rewritten > 0;
}

/// <summary>
/// Converts <c>JsonSerializer.Serialize(new { ... }, options)</c> into an <c>McpJson</c> chain.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous types cannot be named in a <c>[JsonSerializable]</c> attribute, so source generation
/// cannot reach them; and nothing statically references their property getters, so a trimmed build
/// strips them and the call returns <c>{}</c> without throwing. Converting the site to a
/// <c>JsonObject</c> tree removes the reflection entirely.
/// </para>
/// <para>
/// The rewriter declines rather than guesses. A site it cannot model is left untouched and
/// reported in <see cref="McpJsonRewriteResult.Skipped"/>, because a wrong rewrite here is silent:
/// it produces a server that starts, answers, and returns the wrong shape.
/// </para>
/// </remarks>
public sealed class McpJsonRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly McpJsonDialect? _forcedDialect;
    private readonly List<McpJsonSkip> _skipped = [];
    private const int MaxElementDepth = 4;

    private int _rewritten;

    private McpJsonRewriter(SemanticModel model, McpJsonDialect? forcedDialect)
    {
        _model = model;
        _forcedDialect = forcedDialect;
    }

    /// <summary>Rewrites every convertible call in one tree.</summary>
    /// <param name="model">Semantic model for the tree; required to type each member.</param>
    /// <param name="dialect">
    /// Overrides detection of the serialiser options. Leave null to read them from the options
    /// argument, which is what a migration should do.
    /// </param>
    public static McpJsonRewriteResult Rewrite(SemanticModel model, McpJsonDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        McpJsonRewriter rewriter = new(model, dialect);
        SyntaxNode root = rewriter.Visit(model.SyntaxTree.GetRoot());

        if (rewriter._rewritten > 0)
        {
            root = EnsureMcpJsonImport(root);
        }

        return new McpJsonRewriteResult(root, rewriter._rewritten, rewriter._skipped);
    }

    /// <summary>
    /// Adds <c>using DnaX.MCPFab;</c> when the rewrite introduced <c>McpJson</c>.
    /// </summary>
    /// <remarks>
    /// None of the eight tool files in RedisMCPSharp imported it, so without this every rewritten
    /// file failed to compile - a whole-file failure rather than a subtle one, but it would have
    /// turned a clean run into a manual pass over every file the tool touched.
    /// </remarks>
    private static SyntaxNode EnsureMcpJsonImport(SyntaxNode root)
    {
        const string Namespace = "DnaX.MCPFab";

        if (root is not CompilationUnitSyntax unit
            || unit.Usings.Any(directive => directive.Name?.ToString() == Namespace))
        {
            return root;
        }

        // NormalizeWhitespace is required, not cosmetic: a directive built from bare tokens carries
        // no trivia at all and renders as "usingDnaX.MCPFab;".
        UsingDirectiveSyntax import = SyntaxFactory
            .UsingDirective(SyntaxFactory.ParseName(Namespace))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

        // Inserted in sorted position so the file still satisfies whatever using-order rule the
        // repo enforces, rather than being reordered by a later `dotnet format` pass.
        int index = unit.Usings.TakeWhile(directive =>
            string.CompareOrdinal(directive.Name?.ToString(), Namespace) < 0).Count();

        return unit.WithUsings(unit.Usings.Insert(index, import));
    }

    /// <inheritdoc />
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (!IsSerializeCall(node, out AnonymousObjectCreationExpressionSyntax? payload, out ExpressionSyntax? options))
        {
            return base.VisitInvocationExpression(node);
        }

        McpJsonDialect dialect = _forcedDialect ?? McpJsonDialect.Default;
        if (_forcedDialect is null && options is not null
            && !McpJsonDialect.TryDetect(options, _model, out dialect, out string? why))
        {
            return Skip(node, why!);
        }

        string? built = BuildObject(payload!, dialect, out string? failure);
        if (built is null)
        {
            return Skip(node, failure!);
        }

        _rewritten++;

        // Trivia is carried across so a rewritten statement keeps its leading comment and the
        // surrounding layout; `dotnet format` tidies the chain itself afterwards.
        return SyntaxFactory.ParseExpression(built + ".ToJsonString()").WithTriviaFrom(node);
    }

    private SyntaxNode? Skip(InvocationExpressionSyntax node, string reason)
    {
        int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        string text = node.ToString();
        _skipped.Add(new McpJsonSkip(line, text.Length > 120 ? text[..120] + "..." : text, reason));
        return base.VisitInvocationExpression(node);
    }

    /// <summary>Recognises <c>JsonSerializer.Serialize(new { ... }[, options])</c>.</summary>
    private bool IsSerializeCall(
        InvocationExpressionSyntax node,
        out AnonymousObjectCreationExpressionSyntax? payload,
        out ExpressionSyntax? options)
    {
        payload = null;
        options = null;

        if (node.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Serialize" })
        {
            return false;
        }

        // Resolve through the symbol rather than matching on the text, so a local helper called
        // Serialize on some other type is not rewritten.
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method
            || method.ContainingType?.ToDisplayString() != "System.Text.Json.JsonSerializer")
        {
            return false;
        }

        if (node.ArgumentList.Arguments.Count is 0 or > 2)
        {
            return false;
        }

        if (node.ArgumentList.Arguments[0].Expression is not AnonymousObjectCreationExpressionSyntax anonymous)
        {
            return false;
        }

        payload = anonymous;
        options = node.ArgumentList.Arguments.Count == 2
            ? node.ArgumentList.Arguments[1].Expression
            : null;
        return true;
    }

    /// <summary>Builds the <c>McpJson.Object().Set(...)...</c> text for one anonymous object.</summary>
    private string? BuildObject(
        AnonymousObjectCreationExpressionSyntax anonymous,
        McpJsonDialect dialect,
        out string? failure)
    {
        failure = null;
        StringBuilder chain = new("McpJson.Object()");

        foreach (AnonymousObjectMemberDeclaratorSyntax member in anonymous.Initializers)
        {
            string? declared = MemberName(member);
            if (declared is null)
            {
                failure = $"member '{member}' has no inferable name";
                return null;
            }

            // JsonIgnoreCondition.WhenWritingNull omitted these, and Set skips nulls too - but a
            // bare `null` literal cannot pick an overload, so drop the member rather than emit it.
            if (member.Expression.IsKind(SyntaxKind.NullLiteralExpression))
            {
                continue;
            }

            string? value = BuildValue(member.Expression, dialect, out failure);
            if (value is null)
            {
                return null;
            }

            chain.Append($".Set(\"{dialect.PropertyName(declared)}\", {value})");
        }

        return chain.ToString();
    }

    /// <summary>Builds the expression for one member's value.</summary>
    private string? BuildValue(ExpressionSyntax expression, McpJsonDialect dialect, out string? failure)
    {
        failure = null;

        switch (expression)
        {
            case AnonymousObjectCreationExpressionSyntax nested:
                return BuildObject(nested, dialect, out failure);

            // `var trimmed = xs.Select(x => new { ... }); ... items = trimmed` is the commonest
            // shape in the estate, and the anonymous type is unreachable from the member itself.
            case IdentifierNameSyntax identifier when TryInlineLocal(identifier) is { } inlined:
                return BuildValue(inlined, dialect, out failure);

            case InvocationExpressionSyntax projection when TryReadProjection(projection, out ExpressionSyntax? source, out SimpleLambdaExpressionSyntax? lambda):
                {
                    // `xs.Select(x => new { ... })` becomes McpJson.Array(xs, x => McpJson.Object()...),
                    // which keeps the projection lazy and avoids materialising an anonymous sequence.
                    string parameter = lambda!.Parameter.Identifier.ValueText;

                    if (lambda.Body is AnonymousObjectCreationExpressionSyntax body)
                    {
                        string? element = BuildObject(body, dialect, out failure);
                        return element is null ? null : $"McpJson.Array({source}, {parameter} => {element})";
                    }

                    // A block body that ends in `return new { ... }` - four such in RedisMCPSharp alone.
                    // Only a single return is accepted, so there is no branch whose shape differs.
                    if (lambda.Body is BlockSyntax block)
                    {
                        ReturnStatementSyntax[] returns = [.. block.DescendantNodes()
                        .OfType<ReturnStatementSyntax>()
                        .Where(statement => statement.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() == lambda)];

                        if (returns is not [{ Expression: AnonymousObjectCreationExpressionSyntax returned } single])
                        {
                            failure = $"projection '{projection}' does not end in a single anonymous return";
                            return null;
                        }

                        string? returnedChain = BuildObject(returned, dialect, out failure);
                        if (returnedChain is null)
                        {
                            return null;
                        }

                        BlockSyntax rewrittenBlock = block.ReplaceNode(
                            single,
                            single.WithExpression(SyntaxFactory.ParseExpression(returnedChain)));

                        return $"McpJson.Array({source}, {parameter} => {rewrittenBlock})";
                    }

                    // A projection to a scalar - `fields.Select(f => (string?)f)` - is just as common.
                    if (lambda.Body is not ExpressionSyntax scalarBody)
                    {
                        failure = $"projection '{projection}' has a body this tool cannot read";
                        return null;
                    }

                    string? mapped = BuildElement(scalarBody, dialect, out failure);
                    return mapped is null ? null : $"McpJson.Array({source}, {parameter} => {mapped})";
                }

            default:
                return BuildScalar(expression, dialect, out failure);
        }
    }

    /// <summary>
    /// Emits a member value that <c>Set</c> can bind, falling back to <c>McpJson.Scalar</c>.
    /// </summary>
    /// <remarks>
    /// <c>Set</c> has overloads for string, long, double, decimal, bool, DateTimeOffset and
    /// JsonNode. A member typed <c>DateTime</c>, <c>Guid</c>, <c>ulong</c> or <c>byte[]</c> binds to
    /// none of them, so a rewrite that passed the expression straight through would not compile.
    /// Those route through <c>McpJson.Scalar</c>, which returns <c>JsonNode?</c> and so
    /// picks the node overload.
    /// </remarks>
    private string? BuildScalar(ExpressionSyntax expression, McpJsonDialect dialect, out string? failure)
    {
        failure = null;

        TypeInfo info = _model.GetTypeInfo(expression);
        ITypeSymbol? type = info.Type ?? info.ConvertedType;
        if (type is null || type.TypeKind == TypeKind.Error)
        {
            failure = $"could not type '{expression}'";
            return null;
        }

        ITypeSymbol underlying = Unwrap(type);

        if (underlying.TypeKind == TypeKind.Enum)
        {
            // System.Text.Json writes an enum as its numeric value unless a string converter is
            // registered. Scalar would stringify it, so cast to preserve the existing wire format.
            return dialect.Enums == McpJsonEnums.Numeric
                ? $"(long)({expression})"
                : $"McpJson.Scalar({expression}.ToString())";
        }

        if (BindsDirectly(underlying))
        {
            return expression.ToString();
        }

        // A dictionary serialises as a JSON object, so it must be checked before the sequence case:
        // it is also an IEnumerable<KeyValuePair<,>>, and rendering it as an array of pairs would
        // change the shape of the payload.
        if (TryGetMapValueType(underlying) is { } value)
        {
            string? mapped = BuildElement(SyntaxFactory.IdentifierName("entry"), dialect, out failure, value);
            return mapped is null ? null : $"McpJson.Map({expression}, entry => {mapped})";
        }

        // A sequence must become a JSON array. Passing it to Scalar would compile and then write
        // "System.Collections.Generic.List`1[System.String]" into the payload - the precise class of
        // silent wrongness this tool exists to remove.
        if (TryGetElementType(underlying) is { } element)
        {
            string? mapped = BuildElement(SyntaxFactory.IdentifierName("item"), dialect, out failure, element);
            return mapped is null ? null : $"McpJson.Array({expression}, item => {mapped})";
        }

        if (!IsScalarSafe(underlying, dialect))
        {
            failure = $"'{expression}' is of type '{underlying.ToDisplayString()}', which McpJson.Scalar would "
                + "render with ToString rather than as structured JSON";
            return null;
        }

        return $"McpJson.Scalar({expression})";
    }

    /// <summary>
    /// Builds a JSON array element, which must always be a <c>JsonNode</c> rather than a raw value.
    /// </summary>
    private string? BuildElement(
        ExpressionSyntax expression,
        McpJsonDialect dialect,
        out string? failure,
        ITypeSymbol? known = null,
        int depth = 0)
    {
        failure = null;

        ITypeSymbol? type = known;
        if (type is null)
        {
            TypeInfo info = _model.GetTypeInfo(expression);
            type = info.Type ?? info.ConvertedType;
        }

        if (type is null || type.TypeKind == TypeKind.Error)
        {
            failure = $"could not type the element '{expression}'";
            return null;
        }

        ITypeSymbol underlying = Unwrap(type);

        if (underlying.TypeKind == TypeKind.Enum)
        {
            return dialect.Enums == McpJsonEnums.Numeric
                ? $"McpJson.Scalar((long)({expression}))"
                : $"McpJson.Scalar({expression}.ToString())";
        }

        // Elements nest: a list of dictionaries, a dictionary of lists. Each level is driven by the
        // type alone, so the lambda parameters are generated rather than taken from source.
        if (depth < MaxElementDepth)
        {
            string inner = $"item{depth + 1}";
            SyntaxToken parameter = SyntaxFactory.Identifier(inner);

            if (TryGetMapValueType(underlying) is { } mapValue)
            {
                string? mapped = BuildElement(
                    SyntaxFactory.IdentifierName(parameter), dialect, out failure, mapValue, depth + 1);
                return mapped is null ? null : $"McpJson.Map({expression}, {inner} => {mapped})";
            }

            if (TryGetElementType(underlying) is { } elementType)
            {
                string? mapped = BuildElement(
                    SyntaxFactory.IdentifierName(parameter), dialect, out failure, elementType, depth + 1);
                return mapped is null ? null : $"McpJson.Array({expression}, {inner} => {mapped})";
            }
        }

        if (!IsScalarSafe(underlying, dialect))
        {
            failure = $"element type '{underlying.ToDisplayString()}' is not one McpJson.Scalar renders faithfully";
            return null;
        }

        return $"McpJson.Scalar({expression})";
    }

    /// <summary>
    /// The initialiser of a local whose value can be substituted at the use site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only worth doing for a local holding an anonymous type or a sequence of one, because that
    /// type cannot be named and so cannot be rewritten any other way.
    /// </para>
    /// <para>
    /// Inlining is refused unless it is provably equivalent: a single declarator with an
    /// initialiser, never reassigned, and read exactly once. Without the last condition a local
    /// read twice would have its initialiser evaluated twice, which for a LINQ projection means
    /// enumerating the source again.
    /// </para>
    /// </remarks>
    private ExpressionSyntax? TryInlineLocal(IdentifierNameSyntax identifier)
    {
        if (_model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol local
            || local.DeclaringSyntaxReferences.Length != 1)
        {
            return null;
        }

        if (local.Type is not { } type || !MentionsAnonymousType(type))
        {
            return null;
        }

        if (local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax
            {
                Initializer.Value: { } initialiser,
                Parent: VariableDeclarationSyntax { Variables.Count: 1 },
            })
        {
            return null;
        }

        SyntaxNode? body = identifier.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? (SyntaxNode?)identifier.FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
        if (body is null)
        {
            return null;
        }

        int reads = 0;
        foreach (IdentifierNameSyntax reference in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (reference.Identifier.ValueText != local.Name
                || !SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(reference).Symbol, local))
            {
                continue;
            }

            // An assignment target means the value at the use site is not the initialiser.
            if (reference.Parent is AssignmentExpressionSyntax assignment && assignment.Left == reference)
            {
                return null;
            }

            reads++;
        }

        return reads == 1 ? initialiser : null;
    }

    /// <summary>Whether an anonymous type appears anywhere in this type, including as an argument.</summary>
    private static bool MentionsAnonymousType(ITypeSymbol type)
    {
        if (type.IsAnonymousType)
        {
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            return MentionsAnonymousType(array.ElementType);
        }

        return type is INamedTypeSymbol named && named.TypeArguments.Any(MentionsAnonymousType);
    }

    /// <summary>Recognises <c>source.Select(x =&gt; ...)</c>.</summary>
    private bool TryReadProjection(
        InvocationExpressionSyntax node,
        out ExpressionSyntax? source,
        out SimpleLambdaExpressionSyntax? lambda)
    {
        source = null;
        lambda = null;

        if (node.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" } access
            || node.ArgumentList.Arguments.Count != 1
            || node.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax projection)
        {
            return false;
        }

        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol { ContainingType.Name: "Enumerable" })
        {
            return false;
        }

        source = access.Expression;
        lambda = projection;
        return true;
    }

    private static string? MemberName(AnonymousObjectMemberDeclaratorSyntax member) =>
        member.NameEquals?.Name.Identifier.ValueText
        ?? (member.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        });

    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>
    /// The value type when this is a string-keyed map, which serialises as a JSON object.
    /// </summary>
    /// <remarks>
    /// Only string keys are recognised. System.Text.Json can write other key types by converting
    /// them, and reproducing each conversion is not worth the risk of getting one wrong silently -
    /// those sites are declined instead.
    /// </remarks>
    private static ITypeSymbol? TryGetMapValueType(ITypeSymbol type)
    {
        // KeyValuePair has no SpecialType, so it is matched by identity.
        if (TryGetElementType(type) is not INamedTypeSymbol { Name: "KeyValuePair", Arity: 2 } pair
            || pair.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic")
        {
            return null;
        }

        return pair.TypeArguments[0].SpecialType == SpecialType.System_String ? pair.TypeArguments[1] : null;
    }

    /// <summary>
    /// The element type when this is a sequence that should become a JSON array.
    /// </summary>
    /// <remarks>
    /// <c>string</c> is a sequence of chars and must not be treated as one. <c>byte[]</c> is
    /// excluded too: System.Text.Json writes it as a base64 string, and so does
    /// <c>McpJson.Scalar</c>, so turning it into an array of numbers would change the payload.
    /// </remarks>
    private static ITypeSymbol? TryGetElementType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType.SpecialType == SpecialType.System_Byte ? null : array.ElementType;
        }

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                return candidate.TypeArguments[0];
            }
        }

        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } self
            ? self.TypeArguments[0]
            : null;
    }

    /// <summary>
    /// Whether <c>McpJson.Scalar</c> renders this type faithfully rather than falling through to
    /// its <c>Convert.ToString</c> default.
    /// </summary>
    /// <remarks>
    /// Mirrors Scalar's own type switch. Anything absent here would still compile and would still
    /// produce JSON, which is why the rewriter must decline rather than let it through.
    /// </remarks>
    private static bool IsScalarSafe(ITypeSymbol type, McpJsonDialect dialect)
    {
        if (dialect.TrustBoxedScalars && type.SpecialType == SpecialType.System_Object)
        {
            return true;
        }

        if (BindsDirectly(type) || type.SpecialType == SpecialType.System_UInt64)
        {
            return true;
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            return true;
        }

        // `object` is deliberately absent. System.Text.Json serialises it by its RUNTIME type, so a
        // List<object> holding anonymous types writes real JSON objects, whereas McpJson.Scalar can
        // only recognise scalars and falls back to ToString. RedisMCPSharp's cluster key count is
        // exactly that shape, and an earlier version of this tool turned
        //   "perNode":[{"endpoint":"192.168.3.152:6379","keys":70036}]
        // into
        //   "perNode":["{ endpoint = 192.168.3.152:6379, keys = 70036 }"]
        // which compiled, ran, and was caught only by diffing live tool output. Values that really
        // are boxed scalars - a SQL row cell - still convert, but a person decides that per site.
        return Display(type) switch
        {
            "System.DateTime" or "System.DateOnly" or "System.TimeOnly"
                or "System.TimeSpan" or "System.Guid" => true,
            _ => false,
        };
    }

    /// <summary>
    /// The type's name without its nullable annotation, so <c>object?</c> compares as
    /// <c>object</c>. Nullability does not change how a value serialises.
    /// </summary>
    private static string Display(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();

    /// <summary>Whether a value of this type binds to one of the <c>Set</c> overloads as written.</summary>
    private static bool BindsDirectly(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            // UInt64 is deliberately absent: it has no implicit conversion to long, so it would
            // bind to nothing and fail the build.
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return true;
        }

        if (Display(type) == "System.DateTimeOffset")
        {
            return true;
        }

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (Display(current) == "System.Text.Json.Nodes.JsonNode")
            {
                return true;
            }
        }

        return false;
    }
}
