namespace Jellyfin.Plugin.CustomArtwork.Tests;

public sealed class ArtworkMediaWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"custom-artwork-writer-{Guid.NewGuid():N}");

    public ArtworkMediaWriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void MissingDestinationIsRetried()
    {
        var path = Path.Combine(_directory, "poster.jpg");

        Assert.True(ArtworkMediaWriter.NeedsSynchronization(
            path,
            "new-hash",
            overwriteExistingMediaFiles: false,
            new ManagedMediaState()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExistingUnmanagedFileHonorsOverwriteSetting(
        bool overwriteExistingMediaFiles,
        bool expected)
    {
        var path = CreateFile("clearlogo.png");

        Assert.Equal(expected, ArtworkMediaWriter.NeedsSynchronization(
            path,
            "new-hash",
            overwriteExistingMediaFiles,
            new ManagedMediaState()));
    }

    [Fact]
    public void ManagedFileIsRetriedWhenCloudHashChanges()
    {
        var path = CreateFile("poster.jpg");
        var state = new ManagedMediaState();
        state.Files[path] = new ManagedMediaFile { Sha256 = "old-hash" };

        Assert.True(ArtworkMediaWriter.NeedsSynchronization(
            path,
            "new-hash",
            overwriteExistingMediaFiles: false,
            state));
    }

    [Fact]
    public void CurrentManagedFileIsNotDownloadedAgain()
    {
        var path = CreateFile("poster.jpg");
        var state = new ManagedMediaState();
        state.Files[path] = new ManagedMediaFile { Sha256 = "same-hash" };

        Assert.False(ArtworkMediaWriter.NeedsSynchronization(
            path,
            "same-hash",
            overwriteExistingMediaFiles: true,
            state));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "existing");
        return path;
    }
}
