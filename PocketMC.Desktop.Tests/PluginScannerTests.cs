using System.IO.Compression;
using PocketMC.Desktop.Services;

namespace PocketMC.Desktop.Tests;

public sealed class PluginScannerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "PocketMC.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryGetPluginMetadata_ReadsPluginNameAndApiVersion()
    {
        Directory.CreateDirectory(_tempDirectory);
        string jarPath = Path.Combine(_tempDirectory, "example.jar");

        using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("plugin.yml");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("name: ExamplePlugin");
            writer.WriteLine("api-version: '1.20'");
        }

        Assert.Equal("ExamplePlugin", PluginScanner.TryGetPluginName(jarPath));
        Assert.Equal("1.20", PluginScanner.TryGetApiVersion(jarPath));
    }

    [Fact]
    public void IsIncompatible_ReturnsTrue_WhenPluginRequiresNewerApi()
    {
        Assert.True(PluginScanner.IsIncompatible("1.21", "1.20.4"));
        Assert.False(PluginScanner.IsIncompatible("1.19", "1.20.4"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
