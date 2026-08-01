using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class ArtworkLibraryConfigurator
{
    internal const string ProviderName = "Cowabunga Custom Artwork";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtworkLibraryConfigurator> _logger;

    public ArtworkLibraryConfigurator(
        ILibraryManager libraryManager,
        ILogger<ArtworkLibraryConfigurator> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public void Apply()
    {
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var itemId)
                || _libraryManager.GetItemById<CollectionFolder>(itemId) is not { } collectionFolder)
            {
                continue;
            }

            var options = collectionFolder.GetLibraryOptions();
            if (!Apply(options, folder.CollectionType))
            {
                continue;
            }

            collectionFolder.UpdateLibraryOptions(options);
            _logger.LogInformation(
                "Custom Artwork: источник изображений поставлен первым для библиотеки {LibraryName}",
                folder.Name);
        }
    }

    internal static bool Apply(LibraryOptions options, CollectionTypeOptions? collectionType)
    {
        var supportedTypes = collectionType switch
        {
            CollectionTypeOptions.movies => new[] { "Movie", "BoxSet" },
            CollectionTypeOptions.tvshows => new[] { "Series", "Season" },
            CollectionTypeOptions.mixed => new[] { "Movie", "BoxSet", "Series", "Season" },
            CollectionTypeOptions.boxsets => new[] { "BoxSet" },
            _ => [],
        };
        if (supportedTypes.Length == 0)
        {
            return false;
        }

        var changed = false;
        var typeOptions = (options.TypeOptions ?? []).ToList();
        foreach (var type in supportedTypes)
        {
            var itemOptions = typeOptions.FirstOrDefault(value =>
                string.Equals(value.Type, type, StringComparison.OrdinalIgnoreCase));
            if (itemOptions is null)
            {
                itemOptions = new TypeOptions { Type = type };
                typeOptions.Add(itemOptions);
                changed = true;
            }

            var fetchers = Prepend(itemOptions.ImageFetchers);
            itemOptions.ImageFetchers = fetchers.Values;
            changed |= fetchers.Changed;

            var order = Prepend(itemOptions.ImageFetcherOrder);
            itemOptions.ImageFetcherOrder = order.Values;
            changed |= order.Changed;
        }

        if (changed)
        {
            options.TypeOptions = typeOptions.ToArray();
        }

        return changed;
    }

    private static (string[] Values, bool Changed) Prepend(string[]? values)
    {
        values ??= [];
        var updated = new[] { ProviderName }
            .Concat(values.Where(value => !value.Equals(ProviderName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return (updated, !values.SequenceEqual(updated, StringComparer.Ordinal));
    }
}
