using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class CollectionArtworkRetryTracker
{
    private const int StateSchemaVersion = 2;
    private const int MaxRetriesPerRun = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ArtworkIndex _index;
    private readonly ILogger<CollectionArtworkRetryTracker> _logger;

    public CollectionArtworkRetryTracker(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ArtworkIndex index,
        ILogger<CollectionArtworkRetryTracker> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _index = index;
        _logger = logger;
    }

    private static string StatePath => Path.Combine(
        Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(),
        "collection-artwork-retries.v1.json");

    internal async Task ApplyDueRetriesAsync(
        IReadOnlyDictionary<Guid, ArtworkSet> matches,
        IEnumerable<Guid> changedItemIds,
        bool postersEnabled,
        bool logosEnabled,
        CancellationToken cancellationToken)
    {
        var state = LoadState();
        var firstRun = state.SchemaVersion != StateSchemaVersion;
        state.SchemaVersion = StateSchemaVersion;
        var changed = changedItemIds.ToHashSet();
        var currentCollectionIds = new HashSet<Guid>();

        foreach (var (itemId, artwork) in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_libraryManager.GetItemById(itemId) is not BoxSet)
            {
                continue;
            }

            currentCollectionIds.Add(itemId);
            var fingerprint = EffectiveFingerprint(artwork, postersEnabled, logosEnabled);
            if (fingerprint.Length == 0)
            {
                state.Entries.Remove(itemId);
                continue;
            }

            if (firstRun
                || changed.Contains(itemId)
                || !state.Entries.TryGetValue(itemId, out var entry)
                || !entry.Fingerprint.Equals(fingerprint, StringComparison.Ordinal))
            {
                state.Entries[itemId] = new CollectionArtworkRetryEntry
                {
                    Fingerprint = fingerprint,
                    NextAttemptUtc = DateTime.MinValue,
                };
            }
        }

        foreach (var removedId in state.Entries.Keys.Except(currentCollectionIds).ToArray())
        {
            state.Entries.Remove(removedId);
        }

        foreach (var (itemId, entry) in state.Entries.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!matches.TryGetValue(itemId, out var artwork)
                || _libraryManager.GetItemById(itemId) is not BoxSet item)
            {
                state.Entries.Remove(itemId);
                continue;
            }

            if (await IsAppliedAsync(item, artwork, postersEnabled, logosEnabled, cancellationToken)
                .ConfigureAwait(false))
            {
                state.Entries.Remove(itemId);
            }
        }

        var now = DateTime.UtcNow;
        var due = state.Entries
            .Where(pair => pair.Value.NextAttemptUtc <= now)
            .OrderBy(pair => pair.Value.NextAttemptUtc)
            .ThenBy(pair => pair.Key)
            .Take(MaxRetriesPerRun)
            .ToArray();

        foreach (var (itemId, entry) in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.Attempts++;
            entry.NextAttemptUtc = now + RetryDelay(entry.Attempts);
            if (matches.TryGetValue(itemId, out var artwork)
                && _libraryManager.GetItemById(itemId) is BoxSet item)
            {
                await SaveAvailableArtworkAsync(
                    item,
                    artwork,
                    postersEnabled,
                    logosEnabled,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        SaveState(state);
        if (due.Length > 0)
        {
            _logger.LogInformation(
                "Custom Artwork: напрямую обработано {Count} коллекций; ожидают проверки {Pending}",
                due.Length,
                state.Entries.Count);
        }

    }

    internal bool IsCollection(Guid itemId) => _libraryManager.GetItemById(itemId) is BoxSet;

    internal static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 4))));

    private static string EffectiveFingerprint(ArtworkSet artwork, bool postersEnabled, bool logosEnabled) =>
        $"{(postersEnabled ? artwork.Poster?.Sha256 : null)}|{(logosEnabled ? artwork.Logo?.Sha256 : null)}".Trim('|');

    private static async Task<bool> IsAppliedAsync(
        BoxSet item,
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled,
        CancellationToken cancellationToken)
    {
        if (postersEnabled
            && artwork.Poster is not null
            && !await ImageMatchesAsync(item, ImageType.Primary, artwork.Poster.Sha256, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        return !logosEnabled
            || artwork.Logo is null
            || await ImageMatchesAsync(item, ImageType.Logo, artwork.Logo.Sha256, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task SaveAvailableArtworkAsync(
        BoxSet item,
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled,
        CancellationToken cancellationToken)
    {
        if (postersEnabled
            && artwork.Poster is not null
            && !await ImageMatchesAsync(
                item,
                ImageType.Primary,
                artwork.Poster.Sha256,
                cancellationToken).ConfigureAwait(false))
        {
            await SaveImageAsync(item, artwork.Poster, ImageType.Primary, cancellationToken)
                .ConfigureAwait(false);
        }

        if (logosEnabled
            && artwork.Logo is not null
            && !await ImageMatchesAsync(
                item,
                ImageType.Logo,
                artwork.Logo.Sha256,
                cancellationToken).ConfigureAwait(false))
        {
            await SaveImageAsync(item, artwork.Logo, ImageType.Logo, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task SaveImageAsync(
        BoxSet item,
        ArtworkManifestFile source,
        ImageType imageType,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(StatePath)!,
            $"collection-artwork-{Guid.NewGuid():N}.tmp");
        try
        {
            var url = _index.GetArtworkUri(source.Path).AbsoluteUri;
            using var response = await _index.GetArtworkResponseAsync(url, cancellationToken)
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

            if (new FileInfo(temporaryPath).Length != source.Size
                || !await FileMatchesAsync(temporaryPath, source.Sha256, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"Содержимое изображения не совпало с манифестом: {source.Path}");
            }

            await _providerManager.SaveImage(
                item,
                temporaryPath,
                MimeType(source.Path),
                imageType,
                0,
                false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            _logger.LogWarning(
                exception,
                "Custom Artwork: кастомное изображение коллекции {ItemName} не сохранено; другие источники не используются",
                item.Name);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    private static async Task<bool> FileMatchesAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ImageMatchesAsync(
        BoxSet item,
        ImageType imageType,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var path = item.GetImageInfo(imageType, 0)?.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static CollectionArtworkRetryState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new CollectionArtworkRetryState();
        }

        try
        {
            return JsonSerializer.Deserialize<CollectionArtworkRetryState>(
                File.ReadAllText(StatePath),
                JsonOptions) ?? new CollectionArtworkRetryState();
        }
        catch (JsonException)
        {
            return new CollectionArtworkRetryState();
        }
        catch (IOException)
        {
            return new CollectionArtworkRetryState();
        }
    }

    private static void SaveState(CollectionArtworkRetryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions),
                new UTF8Encoding(false));
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
