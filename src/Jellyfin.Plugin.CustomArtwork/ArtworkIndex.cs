using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomArtwork.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed partial class ArtworkIndex
{
    private const int MaxRevisionBytes = 4096;
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private const int MaxManifestFiles = 100_000;
    private const long MaxArtworkBytes = 100 * 1024 * 1024;
    private const string RevisionFileName = "artwork-index.revision.json";
    private const string ManifestFileName = "artwork-index.v1.json";
    private const string MediaPath = "Media";

    internal static readonly Uri ServiceBaseUri = new(
        "https://artwork.lootfullin.netcraze.pro/",
        UriKind.Absolute);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<ArtworkIndex> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<ArtworkIdentity, ArtworkSet> _byIdentity = [];
    private Dictionary<Guid, ArtworkSet> _byItemId = [];
    private HashSet<string> _allowedPaths = new(StringComparer.Ordinal);
    private ArtworkManifest? _manifest;

    public ArtworkIndex(
        ILogger<ArtworkIndex> logger,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
    }

    public DateTime BuiltUtc { get; private set; } = DateTime.MinValue;

    public int Count { get; private set; }

    public string Revision { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyCollection<Guid> ChangedItemIds { get; private set; } = Array.Empty<Guid>();

    internal IReadOnlyDictionary<Guid, ArtworkSet> Matches => _byItemId;

    private static string DataFolder =>
        Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CowabungaCustomArtwork");

    private static string ManifestCachePath => Path.Combine(DataFolder, "artwork-index.v1.json");

    private static string StatePath => Path.Combine(DataFolder, "artwork-state.v1.json");

    public bool IsStale(int intervalMinutes) =>
        BuiltUtc == DateTime.MinValue
        || (DateTime.UtcNow - BuiltUtc).TotalMinutes >= Math.Max(1, intervalMinutes);

    internal ArtworkSet? Find(BaseItem item)
    {
        if (_byItemId.TryGetValue(item.Id, out var byItemId))
        {
            return byItemId;
        }

        if (TryGetIdentity(item, out var identity)
            && _byIdentity.TryGetValue(identity, out var byIdentity))
        {
            return byIdentity;
        }

        return null;
    }

    public async Task BuildAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastError = string.Empty;
            progress?.Report(1);

            await RefreshManifestAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(35);

            if (_manifest is null)
            {
                LastError = "Нет доступного индекса artwork.";
                return;
            }

            BuildLibraryMap(_manifest, progress, cancellationToken);
            BuiltUtc = DateTime.UtcNow;
            Revision = _manifest.Revision;
            progress?.Report(100);

            _logger.LogInformation(
                "Custom Artwork: индекс {Revision}, сопоставлено позиций {Count}, изменилось {Changed}",
                Revision,
                Count,
                ChangedItemIds.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or JsonException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            LastError = exception.Message;
            _logger.LogWarning(exception, "Custom Artwork: не удалось обновить индекс");

            if (_manifest is null)
            {
                _manifest = LoadCachedManifest();
            }

            if (_manifest is not null)
            {
                BuildLibraryMap(_manifest, progress, cancellationToken);
                BuiltUtc = DateTime.UtcNow;
                Revision = _manifest.Revision;
                progress?.Report(100);
                _logger.LogInformation("Custom Artwork: используется последняя локальная копия индекса {Revision}", Revision);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Uri GetArtworkUri(string path)
    {
        var relative = JoinRelative(MediaPath, path);
        return BuildUri(ServiceBaseUri, relative);
    }

    public async Task<HttpResponseMessage> GetArtworkResponseAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requested)
            || !TryGetRelativeArtworkPath(requested, BuildUri(ServiceBaseUri, MediaPath), out var path)
            || !_allowedPaths.Contains(path))
        {
            throw new InvalidOperationException("Custom Artwork rejected an unexpected image URL.");
        }

        using var request = CreateRequest(HttpMethod.Get, requested);
        return await _httpClientFactory
            .CreateClient("Default")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RefreshManifestAsync(CancellationToken cancellationToken)
    {
        var revision = await ReadJsonAsync<ArtworkRevision>(
            BuildUri(ServiceBaseUri, RevisionFileName),
            MaxRevisionBytes,
            cancellationToken).ConfigureAwait(false);

        if (!IsSha256(revision.Revision))
        {
            throw new InvalidDataException("Файл ревизии artwork содержит некорректное значение.");
        }

        if (_manifest is null)
        {
            _manifest = LoadCachedManifest();
        }

        if (_manifest?.Revision.Equals(revision.Revision, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var manifest = await ReadJsonAsync<ArtworkManifest>(
            BuildUri(ServiceBaseUri, ManifestFileName),
            MaxManifestBytes,
            cancellationToken).ConfigureAwait(false);

        ValidateManifest(manifest, revision.Revision);
        SaveJsonAtomically(ManifestCachePath, manifest);
        _manifest = manifest;
    }

    private void BuildLibraryMap(
        ArtworkManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var byRelease = BuildReleaseLookup(manifest.Files);
        var byCollection = BuildCollectionLookup(manifest.Files);
        var byPublishedIdentity = BuildPublishedIdentityLookup(manifest.Files);
        var byCustomCollectionKey = BuildCustomCollectionLookup(manifest.Files);
        var identities = new Dictionary<ArtworkIdentity, ArtworkSet>();
        var ambiguousIdentities = new HashSet<ArtworkIdentity>();
        var itemIds = new Dictionary<Guid, ArtworkSet>();
        var currentState = new ArtworkState { Revision = manifest.Revision };
        var previousState = LoadState();
        var changed = new HashSet<Guid>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.Season,
                BaseItemKind.BoxSet,
            ],
            Recursive = true,
        };
        var items = _libraryManager.GetItemList(query);

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var stateKey = $"item:{item.Id:N}";
            previousState.Entries.TryGetValue(stateKey, out var previousEntry);
            var artwork = MatchItem(
                item,
                manifest.SchemaVersion,
                byRelease,
                byCollection,
                byPublishedIdentity,
                byCustomCollectionKey,
                previousEntry?.CollectionKey);
            if (artwork is null)
            {
                continue;
            }

            itemIds[item.Id] = artwork;
            if (TryGetIdentity(item, out var identity))
            {
                if (!ambiguousIdentities.Contains(identity))
                {
                    if (identities.TryGetValue(identity, out var existing)
                        && !existing.Fingerprint.Equals(artwork.Fingerprint, StringComparison.Ordinal))
                    {
                        identities.Remove(identity);
                        ambiguousIdentities.Add(identity);
                    }
                    else
                    {
                        identities[identity] = artwork;
                    }
                }
            }

            currentState.Entries[stateKey] = new ArtworkStateEntry
            {
                ItemId = item.Id,
                Fingerprint = artwork.Fingerprint,
                CollectionKey = artwork.CollectionKey,
            };

            if (!previousState.Entries.TryGetValue(stateKey, out var previous)
                || !previous.Fingerprint.Equals(artwork.Fingerprint, StringComparison.Ordinal))
            {
                changed.Add(item.Id);
            }

            if (index % 100 == 0)
            {
                progress?.Report(35 + (60.0 * index / Math.Max(1, items.Count)));
            }
        }

        foreach (var removed in previousState.Entries.Keys.Except(currentState.Entries.Keys, StringComparer.Ordinal))
        {
            changed.Add(previousState.Entries[removed].ItemId);
        }

        _byIdentity = identities;
        _byItemId = itemIds;
        _allowedPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        Count = itemIds.Count;
        ChangedItemIds = changed;
        SaveJsonAtomically(StatePath, currentState);
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildReleaseLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var releaseName in file.ReleaseNames)
            {
                AddLookup(result, ReleaseKey(releaseName), file);
            }
        }

        return result;
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildCollectionLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Scope.Equals("collection", StringComparison.Ordinal)))
        {
            foreach (var releaseName in file.ReleaseNames)
            {
                AddLookup(result, CollectionKey(releaseName), file);
            }
        }

        return result;
    }

    private static void AddLookup(
        Dictionary<string, List<ArtworkManifestFile>> lookup,
        string key,
        ArtworkManifestFile file)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!lookup.TryGetValue(key, out var values))
        {
            values = [];
            lookup[key] = values;
        }

        values.Add(file);
    }

    private static ArtworkSet? MatchItem(
        BaseItem item,
        int schemaVersion,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byRelease,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCollection,
        IReadOnlyDictionary<ArtworkIdentity, List<ArtworkManifestFile>> byPublishedIdentity,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCustomCollectionKey,
        string? previousCollectionKey)
    {
        if (TryGetIdentity(item, out var identity)
            && byPublishedIdentity.TryGetValue(identity, out var identityMatches))
        {
            return BuildArtworkSet(identityMatches);
        }

        if (item is BoxSet)
        {
            if (!string.IsNullOrWhiteSpace(previousCollectionKey)
                && byCustomCollectionKey.TryGetValue(previousCollectionKey, out var stableMatches))
            {
                var stableArtwork = BuildArtworkSet(stableMatches);
                if (stableArtwork is not null)
                {
                    return stableArtwork;
                }
            }

            return MatchCandidates(
                CandidateNames(item).Select(CollectionKey),
                byCollection,
                file => file.Scope.Equals("collection", StringComparison.Ordinal)
                    && (schemaVersion < 2 || file.TmdbId is null));
        }

        var scope = item switch
        {
            Movie => "item",
            Series => "series",
            Season => "season",
            _ => string.Empty,
        };
        var seasonNumber = item is Season ? item.IndexNumber : null;

        return MatchCandidates(
            CandidateNames(item).Select(ReleaseKey),
            byRelease,
            file => file.Scope.Equals(scope, StringComparison.Ordinal)
                && (scope != "season" || file.SeasonNumber == seasonNumber));
    }

    internal static ArtworkSet? MatchCandidates(
        IEnumerable<string> candidates,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> lookup,
        Func<ArtworkManifestFile, bool> predicate)
    {
        foreach (var candidate in candidates.Where(candidate => candidate.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (!lookup.TryGetValue(candidate, out var matches))
            {
                continue;
            }

            var filtered = matches.Where(predicate).ToList();
            var directories = filtered
                .Where(file => !Path.GetFileName(file.Path).Contains("(alt)", StringComparison.OrdinalIgnoreCase))
                .Select(file => ParentPath(file.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count();
            if (directories > 1)
            {
                return null;
            }

            var result = BuildArtworkSet(filtered);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static Dictionary<ArtworkIdentity, List<ArtworkManifestFile>> BuildPublishedIdentityLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<ArtworkIdentity, List<ArtworkManifestFile>>();
        foreach (var file in files.Where(file => file.Scope == "collection" && file.TmdbId is > 0))
        {
            var identity = new ArtworkIdentity(
                "collection",
                file.TmdbId!.Value.ToString(CultureInfo.InvariantCulture),
                null);
            if (!result.TryGetValue(identity, out var values))
            {
                values = [];
                result[identity] = values;
            }

            values.Add(file);
        }

        return result;
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildCustomCollectionLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Scope == "collection" && file.CollectionKey is not null))
        {
            if (!result.TryGetValue(file.CollectionKey!, out var values))
            {
                values = [];
                result[file.CollectionKey!] = values;
            }

            values.Add(file);
        }

        return result;
    }

    private static ArtworkSet? BuildArtworkSet(IEnumerable<ArtworkManifestFile> matches)
    {
        var groups = matches
            .Where(file => !Path.GetFileName(file.Path).Contains("(alt)", StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => ParentPath(file.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count != 1)
        {
            return null;
        }

        var result = new ArtworkSet();
        foreach (var file in groups[0])
        {
            if (IsPoster(file.Path))
            {
                result.Poster ??= file;
            }
            else if (IsLogo(file.Path))
            {
                result.Logo ??= file;
            }
        }

        return result.Poster is not null || result.Logo is not null ? result : null;
    }

    private static IEnumerable<string> CandidateNames(BaseItem item)
    {
        if (item is Season season)
        {
            var series = season.Series;
            if (series is not null)
            {
                foreach (var candidate in CandidateNames(series))
                {
                    yield return candidate;
                }
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            if (item is Movie)
            {
                yield return Path.GetFileNameWithoutExtension(item.Path);
            }

            var folder = item is Movie
                ? Path.GetFileName(Path.GetDirectoryName(item.Path))
                : new DirectoryInfo(item.Path).Name;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                yield return folder;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            yield return item.Name;
            if (item.ProductionYear.HasValue)
            {
                yield return $"{item.Name} ({item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)})";
            }
        }
    }

    private static bool TryGetIdentity(BaseItem item, out ArtworkIdentity identity)
    {
        BaseItem identityItem = item;
        var mediaType = item switch
        {
            Movie => "movie",
            Series => "tv",
            Season => "tv",
            BoxSet => "collection",
            _ => string.Empty,
        };

        if (item is Season season && season.Series is not null)
        {
            identityItem = season.Series;
        }

        var tmdbId = identityItem.GetProviderId(MetadataProvider.Tmdb);
        if (mediaType.Length == 0 || string.IsNullOrWhiteSpace(tmdbId))
        {
            identity = default;
            return false;
        }

        identity = new ArtworkIdentity(mediaType, tmdbId, item is Season ? item.IndexNumber : null);
        return true;
    }

    private async Task<T> ReadJsonAsync<T>(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _httpClientFactory
            .CreateClient("Default")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"Ответ {uri} превышает допустимый размер.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"Ответ {uri} превышает допустимый размер.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Ответ {uri} пуст.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Cowabunga-Custom-Artwork/2.3");
        return request;
    }

    internal static void ValidateManifest(ArtworkManifest manifest, string expectedRevision)
    {
        if (manifest.SchemaVersion is not (1 or 2)
            || !manifest.Revision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase)
            || !IsSha256(manifest.Revision)
            || manifest.Files.Count > MaxManifestFiles)
        {
            throw new InvalidDataException("Манифест artwork имеет неподдерживаемый формат или ревизию.");
        }

        foreach (var file in manifest.Files)
        {
            if (!IsSafeRelativePath(file.Path)
                || !IsSha256(file.Sha256)
                || file.Size is <= 0 or > MaxArtworkBytes
                || file.ReleaseNames.Count == 0
                || file.ReleaseNames.Count > 50
                || file.ReleaseNames.Any(string.IsNullOrWhiteSpace)
                || file.Scope is not ("item" or "series" or "season" or "collection")
                || (file.Scope == "season" && file.SeasonNumber is null or < 0)
                || (file.TmdbId is <= 0)
                || (file.CollectionKey is not null && !CustomCollectionKeyRegex().IsMatch(file.CollectionKey))
                || (manifest.SchemaVersion == 2
                    && file.Scope == "collection"
                    && ((file.TmdbId is not null) == (file.CollectionKey is not null)))
                || (file.Scope != "collection" && (file.TmdbId is not null || file.CollectionKey is not null)))
            {
                throw new InvalidDataException($"Некорректная запись artwork: {file.Path}");
            }
        }
    }

    private static ArtworkManifest? LoadCachedManifest()
    {
        if (!File.Exists(ManifestCachePath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ArtworkManifest>(File.ReadAllText(ManifestCachePath), JsonOptions);
            if (manifest is null)
            {
                return null;
            }

            ValidateManifest(manifest, manifest.Revision);
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static ArtworkState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new ArtworkState();
        }

        try
        {
            return JsonSerializer.Deserialize<ArtworkState>(File.ReadAllText(StatePath), JsonOptions)
                ?? new ArtworkState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new ArtworkState();
        }
    }

    private static void SaveJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string ReleaseKey(string name) =>
        QualityTagRegex().Replace(name, string.Empty).Trim().ToLowerInvariant();

    internal static string CollectionKey(string name)
    {
        var normalized = name.ToLowerInvariant();
        var latin = normalized.Count(character => character is >= 'a' and <= 'z');
        var cyrillic = normalized.Count(character => character is >= 'а' and <= 'я' or 'ё');
        if (latin > cyrillic)
        {
            normalized = normalized
                .Replace('а', 'a').Replace('в', 'b').Replace('с', 'c').Replace('е', 'e')
                .Replace('к', 'k').Replace('м', 'm').Replace('н', 'h').Replace('о', 'o')
                .Replace('р', 'p').Replace('т', 't').Replace('х', 'x');
        }

        return NonCollectionCharacterRegex().Replace(normalized, string.Empty);
    }

    private static bool IsPoster(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("poster.jpg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("poster.png", StringComparison.OrdinalIgnoreCase)
            || name.Equals("folder.jpg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("folder.png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-poster.jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-poster.png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLogo(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("clearlogo.png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-clearlogo.png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-сlearlogo.png", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator > 0 ? path[..separator] : string.Empty;
    }

    internal static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static bool IsSha256(string value) => Sha256Regex().IsMatch(value);

    private static Uri BuildUri(Uri baseUri, string relativePath)
    {
        var escaped = string.Join(
            '/',
            NormalizeRelative(relativePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return new Uri(baseUri, escaped);
    }

    private static string JoinRelative(string left, string right) =>
        $"{NormalizeRelative(left)}/{NormalizeRelative(right)}".Trim('/');

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    internal static bool TryGetRelativeArtworkPath(Uri candidate, Uri root, out string path)
    {
        if (!candidate.Scheme.Equals(root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !candidate.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != root.Port
            || candidate.Query.Length > 0
            || candidate.Fragment.Length > 0)
        {
            path = string.Empty;
            return false;
        }

        var rootPath = root.AbsolutePath.TrimEnd('/') + "/";
        if (!candidate.AbsolutePath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            path = string.Empty;
            return false;
        }

        try
        {
            path = string.Join(
                '/',
                candidate.AbsolutePath[rootPath.Length..]
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString));
            return IsSafeRelativePath(path);
        }
        catch (UriFormatException)
        {
            path = string.Empty;
            return false;
        }
    }

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex QualityTagRegex();

    [GeneratedRegex(@"[^0-9a-zа-я]", RegexOptions.CultureInvariant)]
    private static partial Regex NonCollectionCharacterRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^[a-z0-9]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex CustomCollectionKeyRegex();
}
