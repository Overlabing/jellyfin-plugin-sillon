using Jellyfin.Plugin.Sillon.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Sillon;

/// <summary>
/// The Sillon plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        if (Configuration.EnsureWebhookSecret())
        {
            SaveConfiguration();
        }
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Sillon";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("1daed904-1b05-483c-8544-16cb6dcced54");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            incoming.CarryOverSecrets(Configuration);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
