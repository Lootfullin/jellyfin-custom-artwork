namespace Jellyfin.Plugin.CustomArtwork.Tests;

using MediaBrowser.Model.Entities;

public sealed class CollectionArtworkRetryTrackerTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(7, 60)]
    [InlineData(8, 1440)]
    [InlineData(20, 1440)]
    public void RetryDelayUsesBoundedExponentialBackoff(int attempts, int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            CollectionArtworkRetryTracker.RetryDelay(attempts));
    }

    [Theory]
    [InlineData("poster.jpg", "image/jpeg")]
    [InlineData("poster.jpeg", "image/jpeg")]
    [InlineData("clearlogo.png", "image/png")]
    [InlineData("poster.webp", "image/webp")]
    public void MimeTypeUsesArtworkExtension(string path, string expected)
    {
        Assert.Equal(expected, CollectionArtworkRetryTracker.MimeType(path));
    }

    [Fact]
    public void SuccessfulArtworkIsAuditedLaterInsteadOfImmediatelyRequeued()
    {
        var now = new DateTime(2026, 8, 2, 20, 0, 0, DateTimeKind.Utc);
        var entry = new CollectionArtworkRetryEntry { Attempts = 7, NextAttemptUtc = DateTime.MinValue };

        CollectionArtworkRetryTracker.MarkApplied(entry, now);

        Assert.Equal(0, entry.Attempts);
        Assert.Equal(now.AddHours(1), entry.NextAttemptUtc);
    }

    [Fact]
    public void ExistingButWrongCollectionImagesAreQueuedForRefresh()
    {
        var artwork = new ArtworkSet
        {
            Poster = new ArtworkManifestFile { Path = "poster.jpg" },
            Logo = new ArtworkManifestFile { Path = "clearlogo.png" },
        };

        var result = CollectionArtworkRetryTracker.GetUnappliedImageTypes(
            artwork,
            postersEnabled: true,
            logosEnabled: true,
            posterMatches: false,
            logoMatches: false);

        Assert.Equal([ImageType.Primary, ImageType.Logo], result);
    }
}
