using Jellyfin.Plugin.CustomArtwork.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class ArtworkImageProvider : IRemoteImageProvider, IImageProvider, IHasOrder
{
    private readonly ArtworkIndex _index;
    private readonly ILogger<ArtworkImageProvider> _logger;

    public ArtworkImageProvider(ArtworkIndex index, ILogger<ArtworkImageProvider> logger)
    {
        _index = index;
        _logger = logger;
    }

    public string Name => "Cowabunga Custom Artwork";

    public int Order => 0;

    public bool Supports(BaseItem item) => item is Movie or Series or Season or BoxSet;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.Posters ?? true)
        {
            yield return ImageType.Primary;
        }

        if (configuration?.Logos ?? true)
        {
            yield return ImageType.Logo;
        }
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.WebDavUrl))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        if (_index.IsStale(configuration.RefreshIntervalMinutes))
        {
            await _index.BuildAsync(configuration, null, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.StorageMode.Equals(
                PluginConfiguration.MediaFolderStorage,
                StringComparison.Ordinal)
            && item is not BoxSet)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var artwork = _index.Find(item);
        if (artwork is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var images = new List<RemoteImageInfo>(2);
        if (configuration.Posters && artwork.Poster is not null)
        {
            Add(images, configuration, artwork.Poster, ImageType.Primary);
        }

        if (configuration.Logos && artwork.Logo is not null)
        {
            Add(images, configuration, artwork.Logo, ImageType.Logo);
        }

        return images;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Custom Artwork configuration is unavailable.");
        return _index.GetArtworkResponseAsync(configuration, url, cancellationToken);
    }

    private void Add(
        ICollection<RemoteImageInfo> images,
        PluginConfiguration configuration,
        ArtworkManifestFile file,
        ImageType imageType)
    {
        try
        {
            var url = _index.GetArtworkUri(configuration, file.Path).AbsoluteUri;
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Url = url,
                ThumbnailUrl = url,
                Type = imageType,
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "Custom Artwork: отклонён путь изображения {Path}", file.Path);
        }
    }
}
