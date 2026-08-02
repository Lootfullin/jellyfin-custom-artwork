using Jellyfin.Plugin.CustomArtwork.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    internal const string PluginGuid = "6f2d1a54-9c6e-4f2b-9a7d-5c1e2b8a44f1";

    public static Plugin? Instance { get; private set; }

    public override string Name => "Cowabunga Custom Artwork";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Постеры и логотипы из облака Cowabunga.";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }
}
