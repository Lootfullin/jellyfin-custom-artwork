using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.CustomArtwork.Tests;

public sealed class StalePluginVersionCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"plugin-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public void Cleanup_DeletesOnlyOlderDirectoriesWithMatchingIdentity()
    {
        var current = CreateVersion("Cowabunga Custom Artwork_2.3.4.0", Plugin.PluginGuid, "2.3.4.0");
        var old = CreateVersion("Cowabunga Custom Artwork_2.3.3.0", Plugin.PluginGuid, "2.3.3.0");
        var newer = CreateVersion("Cowabunga Custom Artwork_2.3.5.0", Plugin.PluginGuid, "2.3.5.0");
        var unrelated = CreateVersion("Other_1.0.0.0", Guid.NewGuid().ToString(), "1.0.0.0");

        var remaining = StalePluginVersionCleaner.Cleanup(
            current,
            Plugin.PluginGuid,
            "Cowabunga Custom Artwork",
            new Version(2, 3, 4, 0),
            NullLogger.Instance);

        Assert.Equal(0, remaining);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(newer));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void Cleanup_MarksLockedDirectoryDeletedAndRemovesItOnRetry()
    {
        var current = CreateVersion("Cowabunga Custom Artwork_2.3.4.0", Plugin.PluginGuid, "2.3.4.0");
        var old = CreateVersion("Cowabunga Custom Artwork_2.3.3.0", Plugin.PluginGuid, "2.3.3.0");
        File.SetAttributes(Path.Combine(old, "meta.json"), FileAttributes.ReadOnly);
        Assert.Equal(
            1,
            StalePluginVersionCleaner.Cleanup(
                current,
                Plugin.PluginGuid,
                "Cowabunga Custom Artwork",
                new Version(2, 3, 4, 0),
                NullLogger.Instance,
                _ => false));
        Assert.Contains("Deleted", File.ReadAllText(Path.Combine(old, "meta.json")));

        Assert.Equal(
            0,
            StalePluginVersionCleaner.Cleanup(
                current,
                Plugin.PluginGuid,
                "Cowabunga Custom Artwork",
                new Version(2, 3, 4, 0),
                NullLogger.Instance));
        Assert.False(Directory.Exists(old));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateVersion(string folderName, string guid, string version)
    {
        var directory = Path.Combine(_root, folderName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "meta.json"),
            $$"""{"guid":"{{guid}}","version":"{{version}}","status":"Active","autoUpdate":true}""");
        return directory;
    }
}
