using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DnaX.Hosting;

internal sealed class DnaXPaths : IDnaXPaths
{
    private readonly IFileProvider _contentFiles;

    public DnaXPaths(IHostEnvironment environment, IOptions<DnaXPathOptions> options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        ContentRoot = Path.GetFullPath(environment.ContentRootPath);
        _contentFiles = environment.ContentRootFileProvider;

        string configuredDataRoot = options.Value.WritableDataRoot;
        if (string.IsNullOrWhiteSpace(configuredDataRoot))
        {
            throw new OptionsValidationException(
                nameof(DnaXPathOptions),
                typeof(DnaXPathOptions),
                ["WritableDataRoot must not be empty."]);
        }

        WritableDataRoot = Path.IsPathRooted(configuredDataRoot)
            ? Path.GetFullPath(configuredDataRoot)
            : Path.GetFullPath(configuredDataRoot, ContentRoot);
    }

    public string ContentRoot { get; }

    public string WritableDataRoot { get; }

    public string Resolve(string relativePath) => ResolveWithin(ContentRoot, relativePath);

    public string ResolveWritable(string relativePath) => ResolveWithin(WritableDataRoot, relativePath);

    public IFileInfo GetFileInfo(string relativePath)
    {
        string normalized = NormalizeProviderPath(relativePath);
        return _contentFiles.GetFileInfo(normalized);
    }

    public Stream OpenRead(string relativePath)
    {
        IFileInfo file = GetFileInfo(relativePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Content resource '{relativePath}' was not found.", relativePath);
        }

        return file.CreateReadStream();
    }

    public async ValueTask<string> ReadAllTextAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = OpenRead(relativePath);
        using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> ReadAllBytesAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = OpenRead(relativePath);
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string NormalizeProviderPath(string relativePath)
    {
        ValidateRelative(relativePath);
        string normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != '/')
        {
            normalized = normalized.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Content paths cannot contain '.' or '..' segments.", nameof(relativePath));
        }

        return string.Join('/', segments);
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        ValidateRelative(relativePath);
        string fullPath = Path.GetFullPath(relativePath, root);
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison) &&
            !string.Equals(fullPath, Path.TrimEndingDirectorySeparator(root), comparison))
        {
            throw new ArgumentException("The path resolves outside the configured root.", nameof(relativePath));
        }

        return fullPath;
    }

    private static void ValidateRelative(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("A relative path is required.", nameof(relativePath));
        }
    }
}
