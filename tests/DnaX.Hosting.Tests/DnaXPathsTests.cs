using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DnaX.Hosting.Tests;

public sealed class DnaXPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dnax-path-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UsesHostContentRootAndReadsResources()
    {
        Directory.CreateDirectory(Path.Combine(_root, "SqlScripts"));
        await File.WriteAllTextAsync(Path.Combine(_root, "SqlScripts", "report.sql"), "select 1");

        await using ServiceProvider provider = CreateProvider(_root);
        IDnaXPaths paths = provider.GetRequiredService<IDnaXPaths>();

        Assert.Equal(Path.GetFullPath(_root), paths.ContentRoot);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), "SqlScripts", "report.sql"),
            paths.Resolve("SqlScripts/report.sql"));
        Assert.Equal("select 1", await paths.ReadAllTextAsync("SqlScripts/report.sql"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    public async Task RejectsTraversalOutsideContentRoot(string relativePath)
    {
        Directory.CreateDirectory(_root);
        await using ServiceProvider provider = CreateProvider(_root);
        IDnaXPaths paths = provider.GetRequiredService<IDnaXPaths>();

        Assert.Throws<ArgumentException>(() => paths.Resolve(relativePath));
        Assert.Throws<ArgumentException>(() => paths.GetFileInfo(relativePath));
    }

    [Fact]
    public async Task WritableRootCanBeRelativeToContentRoot()
    {
        Directory.CreateDirectory(_root);
        await using ServiceProvider provider = CreateProvider(_root, "state");
        IDnaXPaths paths = provider.GetRequiredService<IDnaXPaths>();

        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "state"), paths.WritableDataRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "state", "cache.db"), paths.ResolveWritable("cache.db"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ServiceProvider CreateProvider(string root, string writableRoot = "data")
    {
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(root));
        services.AddDnaXHosting(options => options.WritableDataRoot = writableRoot);
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string root)
        {
            ContentRootPath = Path.GetFullPath(root);
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DnaX.Hosting.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
