using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DnaX.MCPFab.Tooling;

/// <summary>How the replaced <c>JsonSerializerOptions</c> named JSON properties.</summary>
public enum McpJsonNaming
{
    /// <summary>Property names are written exactly as declared. Fifteen of the sixteen servers.</summary>
    Verbatim,

    /// <summary>Property names are lower-camel-cased. WordpressMCPSharp only.</summary>
    CamelCase,
}

/// <summary>How the replaced <c>JsonSerializerOptions</c> wrote enum values.</summary>
public enum McpJsonEnums
{
    /// <summary>Enums are written as their numeric value, which is System.Text.Json's default.</summary>
    Numeric,

    /// <summary>Enums are written as their member name. MailCalMCPSharp only.</summary>
    AsString,
}

/// <summary>
/// The serialisation behaviour a rewrite must reproduce.
/// </summary>
/// <remarks>
/// <para>
/// A codemod that ignores this looks correct and changes the wire format. Two real cases in the
/// estate: WordpressMCPSharp sets <c>PropertyNamingPolicy = JsonNamingPolicy.CamelCase</c>, so
/// <c>new { Count = 1 }</c> serialises as <c>count</c> and a verbatim rewrite would start emitting
/// <c>Count</c>; MailCalMCPSharp registers <c>JsonStringEnumConverter</c>, so its enums are names
/// while everyone else's are numbers. Neither would fail a build or a startup check.
/// </para>
/// <para>
/// <see cref="TryDetect"/> reads these off the options object rather than trusting a flag, and returns
/// false when it finds a setting it does not model - refusing to run beats guessing, because the
/// damage is silent.
/// </para>
/// </remarks>
public sealed record McpJsonDialect(McpJsonNaming Naming, McpJsonEnums Enums)
{
    /// <summary>
    /// Treat an <c>object</c>-typed value as a boxed scalar that <c>McpJson.Scalar</c> can render.
    /// </summary>
    /// <remarks>
    /// Off by default, and it must stay that way: System.Text.Json serialises <c>object</c> by its
    /// runtime type, so a <c>List&lt;object&gt;</c> holding anonymous types writes real JSON objects
    /// while Scalar would write their ToString. RedisMCPSharp has exactly that shape. Turn this on
    /// only for a codebase where the boxed values are known to be database cells or similar, and
    /// only after looking at the sites it unlocks.
    /// </remarks>
    public bool TrustBoxedScalars { get; init; }

    /// <summary>What fifteen of the sixteen servers use: names as written, enums as numbers.</summary>
    public static McpJsonDialect Default { get; } = new(McpJsonNaming.Verbatim, McpJsonEnums.Numeric);

    /// <summary>Applies the naming policy to a declared member name.</summary>
    public string PropertyName(string declared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declared);

        if (Naming != McpJsonNaming.CamelCase || !char.IsUpper(declared[0]))
        {
            return declared;
        }

        // Matches JsonNamingPolicy.CamelCase: lower-case the leading run of capitals, except that a
        // capital followed by a lower-case letter starts a word and is left alone. "ID" -> "id",
        // "IOStream" -> "ioStream", "Count" -> "count".
        char[] buffer = declared.ToCharArray();
        for (int i = 0; i < buffer.Length; i++)
        {
            if (!char.IsUpper(buffer[i]))
            {
                break;
            }

            if (i > 0 && i + 1 < buffer.Length && !char.IsUpper(buffer[i + 1]))
            {
                break;
            }

            buffer[i] = char.ToLowerInvariant(buffer[i]);
        }

        return new string(buffer);
    }

    /// <summary>
    /// Reads the dialect off the <c>JsonSerializerOptions</c> an expression resolves to.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when every setting found is one this tool models. <see langword="false"/>
    /// when the options carry a converter or policy whose effect is not reproduced here, in which
    /// case the caller must leave the call site alone.
    /// </returns>
    public static bool TryDetect(
        ExpressionSyntax options,
        SemanticModel model,
        out McpJsonDialect dialect,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);

        dialect = Default;
        reason = null;

        ISymbol? symbol = model.GetSymbolInfo(options).Symbol;
        if (symbol is null)
        {
            reason = $"could not resolve the options expression '{options}'";
            return false;
        }

        // The estate declares these as `static readonly JsonSerializerOptions Default = new() { ... }`,
        // so the initialiser is on the field's own declaration.
        if (symbol.DeclaringSyntaxReferences.Length == 0)
        {
            reason = $"'{symbol.Name}' is declared outside this compilation, so its settings cannot be read";
            return false;
        }

        SyntaxNode declaration = symbol.DeclaringSyntaxReferences[0].GetSyntax();
        if (declaration is not VariableDeclaratorSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax creation })
        {
            reason = $"'{symbol.Name}' is not initialised with an object initialiser this tool can read";
            return false;
        }

        McpJsonNaming naming = McpJsonNaming.Verbatim;
        McpJsonEnums enums = McpJsonEnums.Numeric;

        foreach (ExpressionSyntax entry in creation.Initializer?.Expressions ?? default)
        {
            if (entry is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax setting } assignment)
            {
                reason = $"'{symbol.Name}' has an initialiser entry '{entry}' this tool cannot read";
                return false;
            }

            string value = assignment.Right.ToString();
            switch (setting.Identifier.ValueText)
            {
                case "PropertyNamingPolicy":
                    if (value.EndsWith("JsonNamingPolicy.CamelCase", StringComparison.Ordinal))
                    {
                        naming = McpJsonNaming.CamelCase;
                        break;
                    }

                    reason = $"unmodelled PropertyNamingPolicy '{value}'";
                    return false;

                case "Converters":
                    if (value.Contains("JsonStringEnumConverter", StringComparison.Ordinal))
                    {
                        enums = McpJsonEnums.AsString;

                        // A converter list may hold more than the enum converter, and anything else
                        // changes the wire format in a way this tool does not reproduce. Count the
                        // registrations rather than the fragments between them: splitting
                        // "{ new JsonStringEnumConverter() }" yields two pieces for one converter.
                        if (value.Split("new ", StringSplitOptions.None).Length - 1 > 1)
                        {
                            reason = $"Converters holds more than JsonStringEnumConverter: '{value}'";
                            return false;
                        }

                        break;
                    }

                    reason = $"unmodelled converter registration '{value}'";
                    return false;

                // Reproduced by McpJson itself: Set skips nulls, and a node built as a tree cannot
                // cycle. WriteIndented affects whitespace only, which no caller depends on.
                case "DefaultIgnoreCondition" when value.EndsWith("JsonIgnoreCondition.WhenWritingNull", StringComparison.Ordinal):
                case "ReferenceHandler" when value.EndsWith("ReferenceHandler.IgnoreCycles", StringComparison.Ordinal):
                case "WriteIndented":
                    break;

                default:
                    reason = $"unmodelled option '{setting.Identifier.ValueText} = {value}'";
                    return false;
            }
        }

        dialect = new McpJsonDialect(naming, enums);
        return true;
    }
}
