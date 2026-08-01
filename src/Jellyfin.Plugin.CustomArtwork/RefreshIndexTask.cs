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

    public RefreshIndexTask(
        ArtworkIndex index,
        ArtworkMediaWriter mediaWriter,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ArtworkLibraryConfigurator libraryConfigurator,
        ILogger<RefreshIndexTask> logger)
    {
        _index = index;
        _mediaWriter = mediaWriter;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _libraryConfigurator = libraryConfigurator;
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

        RequeueChangedItems(_index.ChangedItemIds.Concat(mediaChanges));
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

    private void RequeueChangedItems(IEnumerable<Guid> itemIds)
    {
        var changed = itemIds.Distinct().ToList();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var itemId in changed)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceImages = [ImageType.Primary, ImageType.Logo],
            };
            _providerManager.QueueRefresh(itemId, options, RefreshPriority.High);
        }

        _logger.LogInformation(
            "Custom Artwork: обновление изображений поставлено в очередь для {Count} позиций",
            changed.Count);
    }
}
