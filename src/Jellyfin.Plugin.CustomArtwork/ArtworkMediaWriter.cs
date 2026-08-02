using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomArtwork.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class ArtworkMediaWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ArtworkIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtworkMediaWriter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArtworkMediaWriter(
        ArtworkIndex index,
        ILibraryManager libraryManager,
        ILogger<ArtworkMediaWriter> logger)
    {
        _index = index;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static string StatePath => Path.Combine(
        Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(),
        "managed-media-files.v1.json");

    public async Task<IReadOnlyCollection<Guid>> ApplyAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = LoadState();
            if (!configuration.StorageMode.Equals(
                    PluginConfiguration.MediaFolderStorage,
                    StringComparison.Ordinal))
            {
                if (state.Files.Count == 0
                    && state.StorageMode.Equals(PluginConfiguration.JellyfinStorage, StringComparison.Ordinal))
                {
                    return Array.Empty<Guid>();
                }

                return CleanupAll(state);
            }

            var processAll = !state.StorageMode.Equals(
                    PluginConfiguration.MediaFolderStorage,
                    StringComparison.Ordinal)
                || state.Posters != configuration.Posters
                || state.Logos != configuration.Logos
                || state.OverwriteExistingMediaFiles != configuration.OverwriteExistingMediaFiles;
            var itemIds = processAll
                ? _index.Matches.Keys.ToHashSet()
                : _index.ChangedItemIds.ToHashSet();

            var allMatchedItemIds = _index.Matches.Keys.ToHashSet();
            var items = GetLibraryItems(allMatchedItemIds);
            if (!processAll)
            {
                AddPendingItems(
                    itemIds,
                    items,
                    configuration,
                    state);
            }

            foreach (var removed in state.Files.Values
                         .Where(file => !_index.Matches.ContainsKey(file.ItemId))
                         .Select(file => file.ItemId))
            {
                itemIds.Add(removed);
            }

            if (itemIds.Count == 0)
            {
                if (processAll)
                {
                    state.StorageMode = PluginConfiguration.MediaFolderStorage;
                    state.Posters = configuration.Posters;
                    state.Logos = configuration.Logos;
                    state.OverwriteExistingMediaFiles = configuration.OverwriteExistingMediaFiles;
                    SaveState(state);
                }

                return Array.Empty<Guid>();
            }

            var refreshed = new HashSet<Guid>();

            foreach (var itemId in itemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_index.Matches.TryGetValue(itemId, out var artwork)
                    || !items.TryGetValue(itemId, out var item)
                    || item is BoxSet)
                {
                    CleanupManagedFiles(
                        itemId,
                        state,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        refreshed);
                    continue;
                }

                var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (configuration.Posters && artwork.Poster is not null)
                {
                    var path = GetDestinationPath(item, "poster", artwork.Poster.Path);
                    if (path is not null)
                    {
                        desiredPaths.Add(path);
                    }
                }

                if (configuration.Logos && artwork.Logo is not null)
                {
                    var path = GetDestinationPath(item, "logo", artwork.Logo.Path);
                    if (path is not null)
                    {
                        desiredPaths.Add(path);
                    }
                }

                CleanupManagedFiles(itemId, state, desiredPaths, refreshed);

                if (configuration.Posters && artwork.Poster is not null)
                {
                    if (await SynchronizeAsync(item, "poster", artwork.Poster, configuration, state, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        refreshed.Add(itemId);
                    }
                }

                if (configuration.Logos && artwork.Logo is not null)
                {
                    if (await SynchronizeAsync(item, "logo", artwork.Logo, configuration, state, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        refreshed.Add(itemId);
                    }
                }
            }

            state.StorageMode = PluginConfiguration.MediaFolderStorage;
            state.Posters = configuration.Posters;
            state.Logos = configuration.Logos;
            state.OverwriteExistingMediaFiles = configuration.OverwriteExistingMediaFiles;
            SaveState(state);
            return refreshed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AddPendingItems(
        ISet<Guid> itemIds,
        IReadOnlyDictionary<Guid, BaseItem> items,
        PluginConfiguration configuration,
        ManagedMediaState state)
    {
        foreach (var (itemId, artwork) in _index.Matches)
        {
            if (!items.TryGetValue(itemId, out var item) || item is BoxSet)
            {
                continue;
            }

            if (configuration.Posters
                && artwork.Poster is not null
                && NeedsSynchronization(
                    GetDestinationPath(item, "poster", artwork.Poster.Path),
                    artwork.Poster.Sha256,
                    configuration.OverwriteExistingMediaFiles,
                    state))
            {
                itemIds.Add(itemId);
                continue;
            }

            if (configuration.Logos
                && artwork.Logo is not null
                && NeedsSynchronization(
                    GetDestinationPath(item, "logo", artwork.Logo.Path),
                    artwork.Logo.Sha256,
                    configuration.OverwriteExistingMediaFiles,
                    state))
            {
                itemIds.Add(itemId);
            }
        }
    }

    internal static bool NeedsSynchronization(
        string? destination,
        string sourceSha256,
        bool overwriteExistingMediaFiles,
        ManagedMediaState state)
    {
        if (destination is null)
        {
            return false;
        }

        if (!File.Exists(destination))
        {
            return true;
        }

        if (!state.Files.TryGetValue(destination, out var managed))
        {
            return overwriteExistingMediaFiles;
        }

        return !managed.Sha256.Equals(
            sourceSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<Guid, BaseItem> GetLibraryItems(IReadOnlySet<Guid> itemIds)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.Season,
            ],
            Recursive = true,
        };

        return _libraryManager
            .GetItemList(query)
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionary(item => item.Id);
    }

    private async Task<bool> SynchronizeAsync(
        BaseItem item,
        string role,
        ArtworkManifestFile source,
        PluginConfiguration configuration,
        ManagedMediaState state,
        CancellationToken cancellationToken)
    {
        var destination = GetDestinationPath(item, role, source.Path);
        if (destination is null)
        {
            _logger.LogWarning(
                "Custom Artwork: не определена папка медиатеки для {Item} ({Id})",
                item.Name,
                item.Id);
            return false;
        }

        state.Files.TryGetValue(destination, out var managed);
        var overwriteManagedFile = false;
        if (File.Exists(destination))
        {
            var currentHash = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (currentHash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            overwriteManagedFile = true;

            if (!CanOverwriteExistingFile(
                    configuration.OverwriteExistingMediaFiles,
                    managed,
                    currentHash))
            {
                state.Files.Remove(destination);
                _logger.LogWarning(
                    "Custom Artwork: локальный файл {Path} не перезаписан, потому что он создан или изменён не плагином",
                    destination);
                return false;
            }

            if (managed is null
                || !managed.Sha256.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                state.Files.Remove(destination);
                _logger.LogInformation(
                    "Custom Artwork: локальный файл {Path} заменяется изображением из облака согласно настройке",
                    destination);
            }
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".cowabunga-artwork-{Guid.NewGuid():N}.tmp");
        try
        {
            var url = _index.GetArtworkUri(source.Path).AbsoluteUri;
            using var response = await _index
                .GetArtworkResponseAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength != source.Size)
            {
                throw new InvalidDataException($"Размер изображения не совпал с манифестом: {source.Path}");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(temporaryPath);
            if (info.Length != source.Size)
            {
                throw new InvalidDataException($"Размер изображения не совпал с манифестом: {source.Path}");
            }

            var downloadedHash = await ComputeSha256Async(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!downloadedHash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 изображения не совпал с манифестом: {source.Path}");
            }

            File.Move(temporaryPath, destination, overwriteManagedFile);
            state.Files[destination] = new ManagedMediaFile
            {
                ItemId = item.Id,
                Role = role,
                Sha256 = source.Sha256,
            };
            return true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Custom Artwork: не записать изображение {Path}", destination);
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string? GetDestinationPath(BaseItem item, string role, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        string itemPath;
        try
        {
            itemPath = Path.GetFullPath(item.Path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
        var isDirectoryItem = item is Series or Season || Directory.Exists(itemPath);
        var directory = isDirectoryItem ? itemPath : Path.GetDirectoryName(itemPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string fileName;
        if (role == "logo")
        {
            fileName = isDirectoryItem
                ? "clearlogo.png"
                : $"{Path.GetFileNameWithoutExtension(itemPath)}-clearlogo.png";
        }
        else
        {
            var extension = Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : ".jpg";
            fileName = isDirectoryItem
                ? $"poster{extension}"
                : $"{Path.GetFileNameWithoutExtension(itemPath)}-poster{extension}";
        }

        return Path.Combine(directory, fileName);
    }

    private static void CleanupManagedFiles(
        Guid itemId,
        ManagedMediaState state,
        IReadOnlySet<string> desiredPaths,
        ISet<Guid> refreshed)
    {
        var obsolete = state.Files
            .Where(pair => pair.Value.ItemId == itemId && !desiredPaths.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var path in obsolete)
        {
            var result = DeleteManagedFile(path, state.Files[path]);
            if (result == ManagedDeleteResult.Deleted)
            {
                refreshed.Add(itemId);
            }

            if (result != ManagedDeleteResult.Retry)
            {
                state.Files.Remove(path);
            }
        }
    }

    private static IReadOnlyCollection<Guid> CleanupAll(ManagedMediaState state)
    {
        var refreshed = new HashSet<Guid>();
        foreach (var (path, file) in state.Files.ToList())
        {
            var result = DeleteManagedFile(path, file);
            if (result == ManagedDeleteResult.Deleted)
            {
                refreshed.Add(file.ItemId);
            }

            if (result != ManagedDeleteResult.Retry)
            {
                state.Files.Remove(path);
            }
        }

        state.StorageMode = PluginConfiguration.JellyfinStorage;
        SaveState(state);
        return refreshed;
    }

    internal static bool DeleteIfUnchanged(string path, ManagedMediaFile managed)
        => DeleteManagedFile(path, managed) == ManagedDeleteResult.Deleted;

    internal static bool CanOverwriteExistingFile(
        bool overwriteExistingMediaFiles,
        ManagedMediaFile? managed,
        string currentHash)
        => overwriteExistingMediaFiles
            || managed is not null
            && managed.Sha256.Equals(currentHash, StringComparison.OrdinalIgnoreCase);

    private static ManagedDeleteResult DeleteManagedFile(string path, ManagedMediaFile managed)
    {
        if (!File.Exists(path))
        {
            return ManagedDeleteResult.Relinquished;
        }

        try
        {
            string currentHash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                currentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            if (!currentHash.Equals(managed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ManagedDeleteResult.Relinquished;
            }

            File.Delete(path);
            return ManagedDeleteResult.Deleted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ManagedDeleteResult.Retry;
        }
    }

    private enum ManagedDeleteResult
    {
        Deleted,
        Relinquished,
        Retry,
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ManagedMediaState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new ManagedMediaState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<ManagedMediaState>(File.ReadAllText(StatePath), JsonOptions)
                ?? new ManagedMediaState();
            state.Files = new Dictionary<string, ManagedMediaFile>(state.Files, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new ManagedMediaState();
        }
    }

    private static void SaveState(ManagedMediaState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, StatePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
