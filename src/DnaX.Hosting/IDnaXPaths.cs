using Microsoft.Extensions.FileProviders;

namespace DnaX.Hosting;

/// <summary>Resolves application resources independently of the process working directory.</summary>
public interface IDnaXPaths
{
    string ContentRoot { get; }

    string WritableDataRoot { get; }

    string Resolve(string relativePath);

    string ResolveWritable(string relativePath);

    IFileInfo GetFileInfo(string relativePath);

    Stream OpenRead(string relativePath);

    ValueTask<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadAllBytesAsync(string relativePath, CancellationToken cancellationToken = default);
}
