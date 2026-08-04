using System.Globalization;
using Jellyfin.Plugin.CustomArtwork.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class RefreshIndexTask : IScheduledTask
{
    private readonly ArtworkIndex _index;
    private readonly ArtworkMediaWriter _mediaWriter;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<RefreshIndexTask> _logger;
    private readonly ArtworkLibraryConfigurator _libraryConfigurator;
    private readonly CollectionArtworkRetryTracker _collectionRetryTracker;

    public RefreshIndexTask(
        ArtworkIndex index,
        ArtworkMediaWriter mediaWriter,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ArtworkLibraryConfigurator libraryConfigurator,
        CollectionArtworkRetryTracker collectionRetryTracker,
        ILogger<RefreshIndexTask> logger)
    {
        _index = index;
        _mediaWriter = mediaWriter;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _libraryConfigurator = libraryConfigurator;
        _collectionRetryTracker = collectionRetryTracker;
        _logger = logger;
    }

    public string Name => "Обновить индекс кастомных обложек";

    public string Key => "CustomArtworkRefreshIndex";

    public string Description =>
        "Проверяет ревизию приватного облака и обновляет только изменившиеся постеры и логотипы.";

    public string Category => "Cowabunga Custom Artwork";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var configuration = plugin.Configuration;
        _libraryConfigurator.Apply();
        await _index.BuildAsync(progress, cancellationToken).ConfigureAwait(false);

        configuration.LastIndexedUtc = _index.BuiltUtc == DateTime.MinValue
            ? string.Empty
            : _index.BuiltUtc.ToString("u", CultureInfo.InvariantCulture);
        configuration.LastIndexedCount = _index.Count;
        configuration.LastRevision = _index.Revision;
        configuration.LastError = _index.LastError;
        plugin.SaveConfiguration();

        IReadOnlyCollection<Guid> mediaChanges = Array.Empty<Guid>();
        if (configuration.StorageMode.Equals(PluginConfiguration.JellyfinStorage, StringComparison.Ordinal)
            || _index.Revision.Length > 0)
        {
            mediaChanges = await _mediaWriter.ApplyAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        var collectionFallbacks = await _collectionRetryTracker.ApplyDueRetriesAsync(
            _index.Matches,
            _index.ChangedItemIds,
            configuration.Posters,
            configuration.Logos,
            cancellationToken).ConfigureAwait(false);

        var removedCollectionRoles = _index.ChangedImageTypes
            .Where(pair => _collectionRetryTracker.IsCollection(pair.Key))
            .Select(pair => new ArtworkRefreshRequest(
                pair.Key,
                GetRemovedImageTypes(
                    _index.Matches.GetValueOrDefault(pair.Key),
                    FilterEnabledImageTypes(pair.Value, configuration))))
            .Where(request => request.ImageTypes.Count > 0);

        var regularRequests = _index.ChangedImageTypes
            .Where(pair => !_collectionRetryTracker.IsCollection(pair.Key))
            .Select(pair => new ArtworkRefreshRequest(
                pair.Key,
                FilterEnabledImageTypes(pair.Value, configuration)))
            .Where(request => request.ImageTypes.Count > 0)
            .Concat(CreateRefreshRequests(
                mediaChanges
                    .Where(itemId => !_collectionRetryTracker.IsCollection(itemId)),
                configuration));
        RequeueChangedItems(
            regularRequests
                .Concat(collectionFallbacks)
                .Concat(removedCollectionRoles));
        _index.AcknowledgeChanges();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger,
        };

        var interval = Plugin.Instance?.Configuration.RefreshIntervalMinutes ?? 5;
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(Math.Max(1, interval)).Ticks,
        };
    }

    private IEnumerable<ArtworkRefreshRequest> CreateRefreshRequests(
        IEnumerable<Guid> itemIds,
        Configuration.PluginConfiguration configuration)
    {
        foreach (var itemId in itemIds.Distinct())
        {
            _index.Matches.TryGetValue(itemId, out var artwork);
            var imageTypes = GetRefreshImageTypes(artwork, configuration);

            if (imageTypes.Count > 0)
            {
                yield return new ArtworkRefreshRequest(itemId, imageTypes);
            }
        }
    }

    internal static IReadOnlyCollection<ImageType> GetRefreshImageTypes(
        ArtworkSet? artwork,
        Configuration.PluginConfiguration configuration)
    {
        var imageTypes = new List<ImageType>(2);
        if (configuration.Posters && (artwork is null || artwork.Poster is not null))
        {
            imageTypes.Add(ImageType.Primary);
        }

        if (configuration.Logos && (artwork is null || artwork.Logo is not null))
        {
            imageTypes.Add(ImageType.Logo);
        }

        return imageTypes;
    }

    internal static IReadOnlyCollection<ImageType> FilterEnabledImageTypes(
        IEnumerable<ImageType> imageTypes,
        Configuration.PluginConfiguration configuration)
    {
        return imageTypes
            .Where(imageType => imageType switch
            {
                ImageType.Primary => configuration.Posters,
                ImageType.Logo => configuration.Logos,
                _ => false,
            })
            .Distinct()
            .ToArray();
    }

    internal static IReadOnlyCollection<ImageType> GetRemovedImageTypes(
        ArtworkSet? artwork,
        IEnumerable<ImageType> changedImageTypes)
    {
        return changedImageTypes
            .Where(imageType => imageType switch
            {
                ImageType.Primary => artwork?.Poster is null,
                ImageType.Logo => artwork?.Logo is null,
                _ => false,
            })
            .Distinct()
            .ToArray();
    }

    private void RequeueChangedItems(IEnumerable<ArtworkRefreshRequest> requests)
    {
        var changed = MergeRefreshRequests(requests);
        foreach (var request in changed)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceImages = request.ImageTypes.ToArray(),
            };
            _providerManager.QueueRefresh(request.ItemId, options, RefreshPriority.High);
        }

        _logger.LogInformation(
            "Custom Artwork: обновление изображений поставлено в очередь для {Count} позиций",
            changed.Count);
    }

    internal static IReadOnlyList<ArtworkRefreshRequest> MergeRefreshRequests(
        IEnumerable<ArtworkRefreshRequest> requests)
    {
        return requests
            .Where(request => request.ItemId != Guid.Empty)
            .GroupBy(request => request.ItemId)
            .Select(group => new ArtworkRefreshRequest(
                group.Key,
                group.SelectMany(request => request.ImageTypes).Distinct().ToArray()))
            .ToList();
    }
}
