using Microsoft.Extensions.Configuration;

namespace DnaX.MCPFab;

/// <summary>
/// The configuration chain every MCPFab server uses, in one place.
/// </summary>
public static class McpFabConfiguration
{
    /// <summary>
    /// Resolves the content root. When the Windows Service Control Manager starts a server the
    /// working directory is <c>C:\Windows\System32</c>, so config and logs must be resolved
    /// relative to the executable rather than the current directory.
    /// </summary>
    public static string GetContentRoot()
    {
        // Under `dotnet run` or `dotnet X.dll`, ProcessPath is the dotnet host rather than the
        // app, so prefer the app base directory and fall back to the process location.
        string? baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Finds a configuration file case-insensitively, returning <paramref name="fileName"/>
    /// unchanged when there is no match.
    /// </summary>
    /// <remarks>
    /// Windows file names are case-insensitive and Linux file names are not, so a container built
    /// from a Windows checkout can ship <c>RedisMCPSharp.json</c> while the host asks for
    /// <c>redismcpsharp.json</c> and silently gets defaults instead. Every server carried a copy
    /// of this fix; none of them had a test for it.
    /// </remarks>
    public static string ResolveConfigFile(string contentRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string direct = Path.Combine(contentRoot, fileName);
        if (File.Exists(direct) || !Directory.Exists(contentRoot))
        {
            return fileName;
        }

        foreach (string candidate in Directory.EnumerateFiles(contentRoot))
        {
            string name = Path.GetFileName(candidate);
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return fileName;
    }

    /// <summary>
    /// Applies the standard source chain, last source winning:
    /// <c>appsettings{,.Environment,.Local}.json</c>, then
    /// <c>{Product}{,.Environment,.Local}.json</c>, then unprefixed environment variables, then
    /// <c>{EnvPrefix}</c>-prefixed ones, then the command line.
    /// </summary>
    /// <remarks>
    /// The product file deliberately overrides <c>appsettings*.json</c>, and unprefixed
    /// environment variables are added before prefixed ones so both <c>Server__Port</c> and
    /// <c>REDISMCP_Server__Port</c> work. The shared Dockerfile relies on the unprefixed form.
    /// </remarks>
    public static IConfigurationBuilder AddMcpFabSources(
        this IConfigurationBuilder configuration,
        McpFabProduct product,
        string contentRoot,
        string environmentName,
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(args);

        configuration.SetBasePath(contentRoot);

        foreach (string stem in new[] { "appsettings", product.Name })
        {
            foreach (string name in new[]
            {
                $"{stem}.json",
                $"{stem}.{environmentName}.json",
                $"{stem}.Local.json",
            })
            {
                configuration.AddJsonFile(
                    ResolveConfigFile(contentRoot, name),
                    optional: true,
                    reloadOnChange: true);
            }
        }

        configuration.AddEnvironmentVariables();
        configuration.AddEnvironmentVariables(prefix: product.EnvPrefix);
        configuration.AddCommandLine(args);
        return configuration;
    }
}
