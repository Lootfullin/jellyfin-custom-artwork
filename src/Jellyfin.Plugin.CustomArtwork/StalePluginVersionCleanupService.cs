using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

internal sealed class StalePluginVersionCleanupService(
    ILogger<StalePluginVersionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeferredRetryDelay = TimeSpan.FromMinutes(5);
    private const int QuickRetryAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

        var assembly = typeof(Plugin).Assembly;
        var currentDirectory = Path.GetDirectoryName(assembly.Location);
        var currentVersion = assembly.GetName().Version;
        if (string.IsNullOrEmpty(currentDirectory) || currentVersion is null)
        {
            return;
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            var remaining = StalePluginVersionCleaner.Cleanup(
                currentDirectory,
                Plugin.PluginGuid,
                "Cowabunga Custom Artwork",
                currentVersion,
                logger);
            if (remaining == 0)
            {
                return;
            }

            var delay = attempt < QuickRetryAttempts ? RetryDelay : DeferredRetryDelay;
            logger.LogWarning(
                "Unable to remove {Count} stale plugin directories; retrying in {Delay}",
                remaining,
                delay);
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static class StalePluginVersionCleaner
{
    internal static int Cleanup(
        string currentDirectory,
        string pluginGuid,
        string pluginName,
        Version currentVersion,
        ILogger logger,
        Func<string, bool>? tryDelete = null)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory));
        var pluginRoot = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrEmpty(pluginRoot) || !Directory.Exists(pluginRoot))
        {
            return 0;
        }

        var remaining = 0;
        tryDelete ??= directory => TryDelete(directory, logger);
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(pluginRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to enumerate plugin directory {PluginRoot}", pluginRoot);
            return 1;
        }

        foreach (var directory in directories)
        {
            try
            {
                var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0
                    || !IsOlderVersion(candidate, pluginGuid, pluginName, currentVersion))
                {
                    continue;
                }

                MarkDeleted(candidate, logger);
                ClearReadOnlyAttributes(candidate, logger);
                if (tryDelete(candidate))
                {
                    logger.LogInformation("Deleted stale plugin directory {PluginDirectory}", candidate);
                }
                else
                {
                    remaining++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Unable to clean stale plugin directory {PluginDirectory}", directory);
                remaining++;
            }
        }

        return remaining;
    }

    private static bool TryDelete(string directory, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Stale plugin directory {PluginDirectory} is still locked; cleanup will be retried",
                directory);
            return false;
        }
    }

    private static bool IsOlderVersion(
        string directory,
        string pluginGuid,
        string pluginName,
        Version currentVersion)
    {
        var manifestPath = Path.Combine(directory, "meta.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                if (!root.TryGetProperty("guid", out var guidElement)
                    || !string.Equals(guidElement.GetString(), pluginGuid, StringComparison.OrdinalIgnoreCase)
                    || !root.TryGetProperty("version", out var versionElement)
                    || !Version.TryParse(versionElement.GetString(), out var version))
                {
                    return false;
                }

                return version < currentVersion;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        var folderName = Path.GetFileName(directory);
        var prefix = pluginName + "_";
        return folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(folderName.AsSpan(prefix.Length), out var inferredVersion)
            && inferredVersion < currentVersion;
    }

    private static void MarkDeleted(string directory, ILogger logger)
    {
        var manifestPath = Path.Combine(directory, "meta.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            ClearReadOnlyAttribute(manifestPath, logger);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            if (manifest is null)
            {
                return;
            }

            manifest["status"] = "Deleted";
            manifest["autoUpdate"] = false;
            var temporaryPath = manifestPath + ".cleanup.tmp";
            File.WriteAllText(temporaryPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Unable to mark stale plugin directory {PluginDirectory} as deleted", directory);
        }
    }

    private static void ClearReadOnlyAttributes(string directory, ILogger logger)
    {
        try
        {
            foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                ClearReadOnlyAttribute(childDirectory, logger);
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ClearReadOnlyAttribute(file, logger);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Unable to enumerate attributes in stale plugin directory {PluginDirectory}", directory);
        }

        ClearReadOnlyAttribute(directory, logger);
    }

    private static void ClearReadOnlyAttribute(string path, ILogger logger)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Unable to clear read-only attribute on {PluginPath}", path);
        }
    }
}
