using DnaX.MCPFab.Tooling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length is 0 or > 2 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        McpFabJsonRewriter - converts JsonSerializer.Serialize(new { ... }) into McpJson chains.

          dotnet run --project eng/McpFabJsonRewriter -- <server-repo-path> [--apply]

        Without --apply nothing is written; the run reports what it would change and what it would
        decline. Build the target repo first: the rewriter needs a semantic model to type each
        member, and takes its references from the repo's most recent build output.

        Run `dotnet format` afterwards - the chains are emitted correct but not laid out.
        """);
    return 0;
}

string root = Path.GetFullPath(args[0]);
bool apply = args.Contains("--apply", StringComparer.Ordinal);

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"No such directory: {root}");
    return 2;
}

static bool IsGenerated(string path) =>
    path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

string[] files = [.. Directory
    .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(path => !IsGenerated(path)
        // ImplicitUsings puts the global usings in obj/. Without them the semantic model cannot
        // resolve ordinary locals, and every site degrades to an unhelpful "could not type x".
        || path.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.Ordinal)];

if (files.Length == 0)
{
    Console.Error.WriteLine($"No C# files under {root}.");
    return 2;
}

SyntaxTree[] trees = [.. files.Select(path =>
    CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path))];

CSharpCompilation compilation = CSharpCompilation.Create(
    "Target",
    trees,
    ReferencesFor(root),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

// A degraded compilation still yields a semantic model, but one that cannot type ordinary locals -
// which surfaces as a pile of "could not type x" skips that look like exotic code and are not.
// Report the cause up front so a missing reference is fixed rather than worked around.
Diagnostic[] errors = [.. compilation
    .GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error)
    .Take(5)];

if (errors.Length > 0)
{
    Console.Error.WriteLine("The target does not compile here, so member types cannot be resolved.");
    Console.Error.WriteLine("Build the repo first; if it is already built, a reference is missing.");
    foreach (Diagnostic error in errors)
    {
        Console.Error.WriteLine($"  {error}");
    }

    Console.Error.WriteLine();
    return 3;
}

int changedFiles = 0;
int rewritten = 0;
List<(string File, McpJsonSkip Skip)> skipped = [];

foreach (SyntaxTree tree in trees)
{
    if (IsGenerated(tree.FilePath))
    {
        continue;
    }

    McpJsonRewriteResult result = McpJsonRewriter.Rewrite(compilation.GetSemanticModel(tree));
    string relative = Path.GetRelativePath(root, tree.FilePath);

    skipped.AddRange(result.Skipped.Select(skip => (relative, skip)));

    if (!result.Changed)
    {
        continue;
    }

    changedFiles++;
    rewritten += result.Rewritten;
    Console.WriteLine($"{relative}: {result.Rewritten} site(s)");

    if (apply)
    {
        File.WriteAllText(tree.FilePath, result.Root.ToFullString());
    }
}

if (skipped.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Declined {skipped.Count} site(s) - these need a hand:");
    foreach ((string file, McpJsonSkip skip) in skipped)
    {
        Console.WriteLine($"  {file}({skip.Line}): {skip.Reason}");
    }
}

Console.WriteLine();
Console.WriteLine(apply
    ? $"Rewrote {rewritten} site(s) across {changedFiles} file(s). Run `dotnet format` next."
    : $"Would rewrite {rewritten} site(s) across {changedFiles} file(s). Re-run with --apply.");

return 0;

// The rewriter only needs enough references to type expressions, so it reuses this process's
// framework set and adds whatever the target's last build produced. Loading the project properly
// would mean taking an MSBuild dependency for no gain in what actually gets typed here.
static List<MetadataReference> ReferencesFor(string root)
{
    List<MetadataReference> references =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];

    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    foreach (string dll in Directory
        .EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
        .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
    {
        if (!seen.Add(Path.GetFileName(dll)))
        {
            continue;
        }

        try
        {
            // A self-contained publish leaves native libraries next to the managed ones.
            // CreateFromFile accepts them and the failure only surfaces as a compile error much
            // later, so probe the metadata here where it can simply be skipped.
            _ = System.Reflection.AssemblyName.GetAssemblyName(dll);
            references.Add(MetadataReference.CreateFromFile(dll));
        }
        catch (Exception error) when (error is IOException or BadImageFormatException)
        {
        }
    }

    return references;
}
