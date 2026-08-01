namespace Jellyfin.Plugin.CustomArtwork.Tests;

using System.Security.Cryptography;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

public sealed class ArtworkIndexTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void LibraryConfiguration_PutsCustomArtworkBeforeOtherCollectionProviders()
    {
        var options = new LibraryOptions
        {
            TypeOptions =
            [
                new TypeOptions
                {
                    Type = "BoxSet",
                    ImageFetchers = ["Choose your Meta! — изображения", "TheMovieDb"],
                    ImageFetcherOrder = ["Choose your Meta! — изображения", "TheMovieDb"],
                },
            ],
        };

        Assert.True(ArtworkLibraryConfigurator.Apply(options, CollectionTypeOptions.boxsets));
        var boxSet = Assert.Single(options.TypeOptions);
        Assert.Equal("Cowabunga Custom Artwork", boxSet.ImageFetcherOrder[0]);
        Assert.False(ArtworkLibraryConfigurator.Apply(options, CollectionTypeOptions.boxsets));
    }

    [Theory]
    [InlineData("Sherlock (2010) [S01 UHD DR]", "sherlock (2010)")]
    [InlineData("  Dr. No (1962)  ", "dr. no (1962)")]
    public void ReleaseKey_RemovesQualitySuffix(string value, string expected)
    {
        Assert.Equal(expected, ArtworkIndex.ReleaseKey(value));
    }

    [Fact]
    public void CollectionKey_IgnoresPunctuationAndCase()
    {
        Assert.Equal("starwarscollection", ArtworkIndex.CollectionKey("Star Wars: Collection"));
    }

    [Theory]
    [InlineData("Movies/Test/poster.jpg", true)]
    [InlineData("Shows/Test/Season 01/poster.jpg", true)]
    [InlineData("../secret.jpg", false)]
    [InlineData("Movies/../secret.jpg", false)]
    [InlineData("/absolute/poster.jpg", false)]
    [InlineData("Movies\\Test\\poster.jpg", false)]
    public void RelativePathValidation_RejectsUnsafePaths(string path, bool expected)
    {
        Assert.Equal(expected, ArtworkIndex.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("https://cloud.example/webdav/Media/Movies/Test/poster.jpg", true, "Movies/Test/poster.jpg")]
    [InlineData("https://cloud.example/webdav/Media/%2e%2e/secret.jpg", false, "")]
    [InlineData("https://other.example/webdav/Media/Movies/Test/poster.jpg", false, "")]
    [InlineData("https://cloud.example/webdav/Media/Movies/Test/poster.jpg?download=other", false, "")]
    public void ArtworkUrlValidation_StaysInsideConfiguredMediaRoot(
        string url,
        bool expected,
        string expectedPath)
    {
        var result = ArtworkIndex.TryGetRelativeArtworkPath(
            new Uri(url),
            new Uri("https://cloud.example/webdav/Media"),
            out var path);

        Assert.Equal(expected, result);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void ArtworkUri_UsesFixedCowabungaEndpoint()
    {
        var index = new ArtworkIndex(null!, null!, null!);

        var result = index.GetArtworkUri("Shows/Sherlock/Season 01/poster.jpg");

        Assert.Equal(
            "https://artwork.lootfullin.netcraze.pro/Media/Shows/Sherlock/Season%2001/poster.jpg",
            result.AbsoluteUri);
    }

    [Fact]
    public void ValidateManifest_AcceptsPublisherSchema()
    {
        var manifest = CreateManifest("Movies/Test/poster.jpg");

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }

    [Fact]
    public void CollectionKey_RepairsCyrillicLookalikeInsideLatinName()
    {
        Assert.Equal("beetlejuicecollection", ArtworkIndex.CollectionKey("Beetlejuice Сollection"));
    }

    [Fact]
    public void ValidateManifest_AcceptsSchemaTwoCollectionWithTmdbId()
    {
        var file = CreateFile("Collections/Star Wars/poster.jpg", "collection");
        file.TmdbId = 10;
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [file],
        };

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }

    [Fact]
    public void ValidateManifest_AllowsSchemaTwoCustomCollectionWithoutTmdbId()
    {
        var file = CreateFile("Collections/Unknown/poster.jpg", "collection");
        file.CollectionKey = "phase16";
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [file],
        };

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }


    [Fact]
    public void ValidateManifest_RejectsSchemaTwoCollectionWithoutIdentity()
    {
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [CreateFile("Collections/Unknown/poster.jpg", "collection")],
        };

        Assert.Throws<InvalidDataException>(() => ArtworkIndex.ValidateManifest(manifest, Revision));
    }

    [Fact]
    public void ValidateManifest_RejectsTraversal()
    {
        var manifest = CreateManifest("../poster.jpg");

        Assert.Throws<InvalidDataException>(() => ArtworkIndex.ValidateManifest(manifest, Revision));
    }

    [Fact]
    public void MatchCandidates_JoinsPosterAndLogoFromOneDirectory()
    {
        var poster = CreateFile("Shows/Sherlock/poster.jpg", "series");
        var logo = CreateFile("Shows/Sherlock/clearlogo.png", "series");
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["sherlock"] = [poster, logo],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["sherlock"],
            lookup,
            file => file.Scope == "series");

        Assert.NotNull(result);
        Assert.Same(poster, result.Poster);
        Assert.Same(logo, result.Logo);
    }

    [Fact]
    public void MatchCandidates_SkipsAmbiguousDirectories()
    {
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["example"] =
            [
                CreateFile("Movies/One/poster.jpg", "item"),
                CreateFile("Movies/Two/poster.jpg", "item"),
            ],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["example"],
            lookup,
            file => file.Scope == "item");

        Assert.Null(result);
    }

    [Fact]
    public void MediaFolderDestination_UsesVideoBaseNameForMovie()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var video = Path.Combine(directory, "Example (2026) [UHD].mkv");
            File.WriteAllBytes(video, [0]);
            var movie = new Movie { Path = video };

            Assert.Equal(
                Path.Combine(directory, "Example (2026) [UHD]-poster.jpg"),
                ArtworkMediaWriter.GetDestinationPath(movie, "poster", "Movies/Example/poster.jpg"));
            Assert.Equal(
                Path.Combine(directory, "Example (2026) [UHD]-clearlogo.png"),
                ArtworkMediaWriter.GetDestinationPath(movie, "logo", "Movies/Example/clearlogo.png"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MediaFolderDestination_UsesBareNamesForSeries()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var series = new Series { Path = directory };

            Assert.Equal(
                Path.Combine(directory, "poster.png"),
                ArtworkMediaWriter.GetDestinationPath(series, "poster", "Shows/Example/poster.png"));
            Assert.Equal(
                Path.Combine(directory, "clearlogo.png"),
                ArtworkMediaWriter.GetDestinationPath(series, "logo", "Shows/Example/clearlogo.png"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ManagedFileDeletion_DeletesOnlyUnchangedPluginFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "poster.jpg");
            var contents = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(path, contents);
            var managed = new ManagedMediaFile
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant(),
            };

            Assert.True(ArtworkMediaWriter.DeleteIfUnchanged(path, managed));
            Assert.False(File.Exists(path));

            File.WriteAllBytes(path, [9, 9, 9]);
            Assert.False(ArtworkMediaWriter.DeleteIfUnchanged(path, managed));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ArtworkManifest CreateManifest(string path) => new()
    {
        SchemaVersion = 1,
        Revision = Revision,
        GeneratedAt = "2026-08-01T00:00:00Z",
        Files = [CreateFile(path, "item")],
    };

    private static ArtworkManifestFile CreateFile(string path, string scope) => new()
    {
        Path = path,
        Sha256 = Revision,
        Size = 123,
        ModifiedAt = "2026-08-01T00:00:00Z",
        ReleaseNames = ["Example"],
        Scope = scope,
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cowabunga-artwork-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
