using System.Reflection;
using System.Runtime.Loader;
using DnaX.MCPFab;
using DnaX.MCPFab.Tooling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace McpFabJsonRewriter.Tests;

/// <summary>
/// Compiles a snippet, rewrites it, and runs both halves.
/// </summary>
/// <remarks>
/// Asserting that the rewriter emits the expected source proves only that it did what was asked.
/// The property that matters is that the server still answers with the same bytes, so
/// <see cref="Equivalent"/> executes the original and the rewrite and compares their output. A
/// wrong rewrite in this estate is silent - the build passes and the tool returns the wrong shape -
/// so behaviour, not source text, is the thing worth pinning.
/// </remarks>
internal static class RewriteHarness
{
    private const string Header = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json;
        using System.Text.Json.Nodes;
        using System.Text.Json.Serialization;
        using DnaX.MCPFab;

        public static class Probe
        {
            public static readonly JsonSerializerOptions Options = new()
            {
        """;

    private static readonly MetadataReference[] References = BuildReferences();

    /// <summary>Wraps a method body in a compilable probe.</summary>
    /// <param name="body">Statements ending in a <c>return</c> of a JSON string.</param>
    /// <param name="optionsInitialiser">Entries for the probe's <c>JsonSerializerOptions</c>.</param>
    /// <param name="members">Extra members, for example the source collections a snippet projects.</param>
    public static string Source(string body, string optionsInitialiser = "", string members = "") =>
        $$"""
        {{Header}}
        {{optionsInitialiser}}
            };

        {{members}}

            public static string Run()
            {
        {{body}}
            }
        }
        """;

    /// <summary>Rewrites a probe and returns the new source plus the rewrite outcome.</summary>
    public static (string Source, McpJsonRewriteResult Result) Rewrite(string source, McpJsonDialect? dialect = null)
    {
        CSharpCompilation compilation = Compile(source, out SyntaxTree tree);
        McpJsonRewriteResult result = McpJsonRewriter.Rewrite(compilation.GetSemanticModel(tree), dialect);
        return (result.Root.ToFullString(), result);
    }

    /// <summary>
    /// Runs the probe before and after the rewrite and asserts the JSON is byte-identical.
    /// </summary>
    /// <returns>The JSON both versions produced, so a test can assert its shape as well.</returns>
    public static string Equivalent(string source, McpJsonDialect? dialect = null)
    {
        string before = Execute(source, "before");

        (string rewritten, McpJsonRewriteResult result) = Rewrite(source, dialect);
        Assert.True(
            result.Rewritten > 0,
            "The rewriter changed nothing, so the comparison would pass without proving anything. "
                + $"Skipped: {string.Join("; ", result.Skipped.Select(s => s.Reason))}");

        string after = Execute(rewritten, "after");

        Assert.Equal(before, after);
        return before;
    }

    /// <summary>Compiles and invokes <c>Probe.Run</c>.</summary>
    public static string Execute(string source, string label)
    {
        CSharpCompilation compilation = Compile(source, out _);

        using MemoryStream buffer = new();
        EmitResult emit = compilation.Emit(buffer);
        if (!emit.Success)
        {
            string errors = string.Join(
                Environment.NewLine,
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Fail($"The '{label}' source did not compile:{Environment.NewLine}{errors}{Environment.NewLine}{source}");
        }

        buffer.Position = 0;
        Assembly assembly = new AssemblyLoadContext($"probe-{label}-{Guid.NewGuid():N}", isCollectible: true)
            .LoadFromStream(buffer);

        MethodInfo run = assembly.GetType("Probe")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        try
        {
            return (string)run.Invoke(null, null)!;
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            throw invocation.InnerException;
        }
    }

    private static CSharpCompilation Compile(string source, out SyntaxTree tree)
    {
        tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));

        return CSharpCompilation.Create(
            $"Probe_{Guid.NewGuid():N}",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static MetadataReference[] BuildReferences()
    {
        // The reference set the test process itself is running against, which is the only way to be
        // sure the snippet compiles against the same System.Text.Json that executes it.
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

        return
        [
            .. platform
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(McpJson).Assembly.Location),
        ];
    }
}
