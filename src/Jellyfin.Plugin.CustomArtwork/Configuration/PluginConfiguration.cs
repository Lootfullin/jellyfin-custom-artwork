using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CustomArtwork.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public const string JellyfinStorage = "Jellyfin";

    public const string MediaFolderStorage = "MediaFolder";

    public string WebDavUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string MediaPath { get; set; } = "Media";

    public int RefreshIntervalMinutes { get; set; } = 5;

    public bool Posters { get; set; } = true;

    public bool Logos { get; set; } = true;

    public string StorageMode { get; set; } = JellyfinStorage;

    public string LastIndexedUtc { get; set; } = string.Empty;

    public int LastIndexedCount { get; set; }

    public string LastRevision { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;
}
