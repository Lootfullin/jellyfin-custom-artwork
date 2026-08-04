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
    private const int StateSchemaVersion = 4;
    private const int MaxRetriesPerRun = 200;
    private const int FrequentRetryLimit = 8;
    private static readonly TimeSpan AuditInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongRetryInterval = TimeSpan.FromHours(24);
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

    internal async Task<IReadOnlyCollection<ArtworkRefreshRequest>> ApplyDueRetriesAsync(
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

        var fallbackRequests = new List<ArtworkRefreshRequest>();
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
            if (matches.TryGetValue(itemId, out var artwork)
                && _libraryManager.GetItemById(itemId) is BoxSet item)
            {
                if (AppliedFilesUnchanged(item, entry, artwork, postersEnabled, logosEnabled))
                {
                    AddFallbackRequests(
                        fallbackRequests,
                        itemId,
                        item,
                        artwork,
                        postersEnabled,
                        logosEnabled);
                    MarkApplied(entry, item, now);
                    continue;
                }

                await SaveAvailableArtworkAsync(
                    item,
                    artwork,
                    postersEnabled,
                    logosEnabled,
                    cancellationToken).ConfigureAwait(false);

                if (await IsAppliedAsync(
                        item,
                        artwork,
                        postersEnabled,
                        logosEnabled,
                        cancellationToken).ConfigureAwait(false))
                {
                    AddFallbackRequests(
                        fallbackRequests,
                        itemId,
                        item,
                        artwork,
                        postersEnabled,
                        logosEnabled);
                    MarkApplied(entry, item, now);
                    continue;
                }

                var unappliedTypes = await GetUnappliedImageTypesAsync(
                    item,
                    artwork,
                    postersEnabled,
                    logosEnabled,
                    cancellationToken).ConfigureAwait(false);
                if (unappliedTypes.Count > 0)
                {
                    fallbackRequests.Add(new ArtworkRefreshRequest(itemId, unappliedTypes));
                }
            }

            entry.NextAttemptUtc = now + RetryDelay(entry.Attempts);
        }

        SaveState(state);
        if (due.Length > 0)
        {
            _logger.LogInformation(
                "Custom Artwork: напрямую обработано {Count} коллекций; ожидают проверки {Pending}",
                due.Length,
                state.Entries.Count);
        }

        return fallbackRequests;
    }

    internal static void MarkApplied(CollectionArtworkRetryEntry entry, DateTime now)
    {
        entry.Attempts = 0;
        entry.NextAttemptUtc = now + AuditInterval;
    }

    private static void MarkApplied(CollectionArtworkRetryEntry entry, BoxSet item, DateTime now)
    {
        MarkApplied(entry, now);
        entry.PosterFileSignature = FileSignature(item, ImageType.Primary);
        entry.LogoFileSignature = FileSignature(item, ImageType.Logo);
    }

    internal static bool AppliedFilesUnchanged(
        BoxSet item,
        CollectionArtworkRetryEntry entry,
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled) =>
        (!postersEnabled
            || artwork.Poster is null
            || entry.PosterFileSignature.Length > 0
            && entry.PosterFileSignature.Equals(FileSignature(item, ImageType.Primary), StringComparison.Ordinal))
        && (!logosEnabled
            || artwork.Logo is null
            || entry.LogoFileSignature.Length > 0
            && entry.LogoFileSignature.Equals(FileSignature(item, ImageType.Logo), StringComparison.Ordinal));

    private static string FileSignature(BoxSet item, ImageType imageType)
    {
        var path = item.GetImageInfo(imageType, 0)?.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var info = new FileInfo(path);
            return $"{path}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    internal bool IsCollection(Guid itemId) => _libraryManager.GetItemById(itemId) is BoxSet;

    internal static TimeSpan RetryDelay(int attempts) => attempts >= FrequentRetryLimit
        ? LongRetryInterval
        : TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 4))));

    private static string EffectiveFingerprint(ArtworkSet artwork, bool postersEnabled, bool logosEnabled) =>
        $"{(postersEnabled ? artwork.Poster?.Sha256 : null)}|{(logosEnabled ? artwork.Logo?.Sha256 : null)}".Trim('|');

    internal static IReadOnlyCollection<ImageType> MissingImageTypes(
        BoxSet item,
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled)
    {
        var result = new List<ImageType>(2);
        if (postersEnabled
            && artwork.Poster is not null
            && !HasImage(item, ImageType.Primary))
        {
            result.Add(ImageType.Primary);
        }

        if (logosEnabled
            && artwork.Logo is not null
            && !HasImage(item, ImageType.Logo))
        {
            result.Add(ImageType.Logo);
        }

        return result;
    }

    private static async Task<IReadOnlyCollection<ImageType>> GetUnappliedImageTypesAsync(
        BoxSet item,
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled,
        CancellationToken cancellationToken)
    {
        var posterMatches = !postersEnabled
            || artwork.Poster is null
            || await ImageMatchesAsync(item, ImageType.Primary, artwork.Poster.Sha256, cancellationToken)
                .ConfigureAwait(false);
        var logoMatches = !logosEnabled
            || artwork.Logo is null
            || await ImageMatchesAsync(item, ImageType.Logo, artwork.Logo.Sha256, cancellationToken)
                .ConfigureAwait(false);
        return GetUnappliedImageTypes(
            artwork,
            postersEnabled,
            logosEnabled,
            posterMatches,
            logoMatches);
    }

    internal static IReadOnlyCollection<ImageType> GetUnappliedImageTypes(
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled,
        bool posterMatches,
        bool logoMatches)
    {
        var result = new List<ImageType>(2);
        if (postersEnabled && artwork.Poster is not null && !posterMatches)
        {
            result.Add(ImageType.Primary);
        }

        if (logosEnabled && artwork.Logo is not null && !logoMatches)
        {
            result.Add(ImageType.Logo);
        }

        return result;
    }

    internal static IReadOnlyCollection<ImageType> GetFallbackImageTypes(
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled,
        bool hasPoster,
        bool hasLogo)
    {
        var result = new List<ImageType>(2);
        if (postersEnabled && artwork.Poster is null && !hasPoster)
        {
            result.Add(ImageType.Primary);
        }

        if (logosEnabled && artwork.Logo is null && !hasLogo)
        {
            result.Add(ImageType.Logo);
        }

        return result;
    }

    private static void AddFallbackRequests(
        ICollection<ArtworkRefreshRequest> requests,
        Guid itemId,
        BoxSet item,
        ArtworkSet artwork,
        bool postersEnabled,
        bool logosEnabled)
    {
        var imageTypes = GetFallbackImageTypes(
            artwork,
            postersEnabled,
            logosEnabled,
            HasImage(item, ImageType.Primary),
            HasImage(item, ImageType.Logo));
        if (imageTypes.Count > 0)
        {
            requests.Add(new ArtworkRefreshRequest(itemId, imageTypes));
        }
    }

    private static bool HasImage(BoxSet item, ImageType imageType)
    {
        var path = item.GetImageInfo(imageType, 0)?.Path;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

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
            or InvalidDataException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Custom Artwork: кастомное изображение коллекции {ItemName} не сохранено; попытка будет повторена",
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
