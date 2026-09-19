namespace DnaX.MCPFab.Tests;

/// <summary>
/// Regression cover for the content-root resolution that shipped broken in RedisMCPSharp v1.2.0.
/// </summary>
public sealed class McpFabContentRootTests
{
    [Fact]
    public void ContentRootIsTheExecutableFolderNotTheSingleFileExtractionFolder()
    {
        string contentRoot = McpFabConfiguration.GetContentRoot();

        Assert.False(string.IsNullOrWhiteSpace(contentRoot));
        Assert.True(Directory.Exists(contentRoot), $"Content root '{contentRoot}' does not exist.");

        string temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.False(
            Path.GetFullPath(contentRoot)
                .StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"Content root '{contentRoot}' is under TEMP. With PublishSingleFile and "
                + "IncludeAllContentForSelfExtract that is the extraction folder, where the "
                + "product config file can never be, so the server starts unconfigured.");
    }

    [Fact]
    public void ResolveConfigFileFindsTheFileWhateverCaseIsAskedFor()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mcpfab-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "RedisMCPSharp.json"), "{}");

            // The invariant that holds on both platforms: whatever name comes back must resolve
            // to the file on disk. Windows already matches case-insensitively, so the requested
            // name is returned as-is; Linux needs the on-disk casing found for it.
            foreach (string requested in new[] { "redismcpsharp.json", "RedisMCPSharp.json", "REDISMCPSHARP.JSON" })
            {
                string resolved = McpFabConfiguration.ResolveConfigFile(directory, requested);
                Assert.True(
                    File.Exists(Path.Combine(directory, resolved)),
                    $"Resolved '{resolved}' for '{requested}' but no such file exists.");
            }

            // No match leaves the requested name alone, so the configuration source stays optional.
            Assert.Equal("Absent.json", McpFabConfiguration.ResolveConfigFile(directory, "Absent.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
