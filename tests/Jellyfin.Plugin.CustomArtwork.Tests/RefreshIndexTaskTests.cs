using Jellyfin.Plugin.CustomArtwork.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CustomArtwork.Tests;

public sealed class RefreshIndexTaskTests
{
    [Fact]
    public void LogoOnlyArtworkDoesNotReplacePoster()
    {
        var artwork = new ArtworkSet
        {
            Logo = new ArtworkManifestFile { Path = "clearlogo.png" },
        };

        var types = RefreshIndexTask.GetRefreshImageTypes(
            artwork,
            new PluginConfiguration { Posters = true, Logos = true });

        Assert.Equal([ImageType.Logo], types);
    }

    [Fact]
    public void PosterOnlyArtworkDoesNotReplaceLogo()
    {
        var artwork = new ArtworkSet
        {
            Poster = new ArtworkManifestFile { Path = "poster.jpg" },
        };

        var types = RefreshIndexTask.GetRefreshImageTypes(
            artwork,
            new PluginConfiguration { Posters = true, Logos = true });

        Assert.Equal([ImageType.Primary], types);
    }

    [Fact]
    public void RemovedArtworkRestoresEnabledFallbackRoles()
    {
        var types = RefreshIndexTask.GetRefreshImageTypes(
            artwork: null,
            new PluginConfiguration { Posters = true, Logos = false });

        Assert.Equal([ImageType.Primary], types);
    }

    [Fact]
    public void CollectionWithoutCustomPosterRequestsFallbackOnlyWhenPosterIsMissing()
    {
        var artwork = new ArtworkSet
        {
            Logo = new ArtworkManifestFile { Path = "clearlogo.png" },
        };

        var missingPoster = CollectionArtworkRetryTracker.GetFallbackImageTypes(
            artwork,
            postersEnabled: true,
            logosEnabled: true,
            hasPoster: false,
            hasLogo: true);
        var existingPoster = CollectionArtworkRetryTracker.GetFallbackImageTypes(
            artwork,
            postersEnabled: true,
            logosEnabled: true,
            hasPoster: true,
            hasLogo: true);

        Assert.Equal([ImageType.Primary], missingPoster);
        Assert.Empty(existingPoster);
    }

    [Theory]
    [InlineData("poster-old|logo", "poster-new|logo", ImageType.Primary)]
    [InlineData("poster|logo-old", "poster|logo-new", ImageType.Logo)]
    [InlineData("poster|logo", "|logo", ImageType.Primary)]
    [InlineData("poster|logo", null, ImageType.Primary, ImageType.Logo)]
    public void IndexTracksChangedImageRoles(
        string previous,
        string? current,
        params ImageType[] expected)
    {
        Assert.Equal(
            expected,
            ArtworkIndex.GetChangedImageTypes(previous, current));
    }

    [Fact]
    public void CollectionRefreshesOnlyRoleRemovedFromCloud()
    {
        var artwork = new ArtworkSet
        {
            Logo = new ArtworkManifestFile { Path = "clearlogo.png" },
        };

        var result = RefreshIndexTask.GetRemovedImageTypes(
            artwork,
            [ImageType.Primary, ImageType.Logo]);

        Assert.Equal([ImageType.Primary], result);
    }
}
