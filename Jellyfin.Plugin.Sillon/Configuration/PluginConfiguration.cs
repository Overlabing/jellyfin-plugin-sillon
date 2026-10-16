using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sillon.Configuration;

/// <summary>
/// How wishlist entries are fulfilled.
/// </summary>
public enum FulfillmentMode
{
    /// <summary>
    /// Entries are handled by hand.
    /// </summary>
    Manual,

    /// <summary>
    /// Entries are forwarded to Lidarr.
    /// </summary>
    Lidarr
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The placeholder the configuration page gets instead of the private key.
    /// </summary>
    public const string MaskedSecret = "***";

    /// <summary>
    /// The default APNs team id.
    /// </summary>
    public const string DefaultApnsTeamId = "XXXXXXXXXX";

    /// <summary>
    /// The default APNs topic.
    /// </summary>
    public const string DefaultApnsTopic = "com.overglass.sillon";

    private const string SecretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Gets or sets how requests are fulfilled.
    /// </summary>
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Manual;

    /// <summary>
    /// Gets or sets the Lidarr base URL.
    /// </summary>
    public string LidarrUrl { get; set; } = "http://lidarr:8686";

    /// <summary>
    /// Gets or sets the Lidarr API key.
    /// </summary>
    public string LidarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Lidarr quality profile id.
    /// </summary>
    public int LidarrQualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the Lidarr metadata profile id.
    /// </summary>
    public int LidarrMetadataProfileId { get; set; }

    /// <summary>
    /// Gets or sets the Lidarr root folder.
    /// </summary>
    public string LidarrRootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the music folder as Lidarr sees it.
    /// </summary>
    public string LidarrPathPrefix { get; set; } = "/data/media/music";

    /// <summary>
    /// Gets or sets the same folder as Jellyfin sees it.
    /// </summary>
    public string JellyfinPathPrefix { get; set; } = "/data/music";

    /// <summary>
    /// Gets or sets a value indicating whether the wishlist is offered to the apps through <c>features</c>.
    /// </summary>
    public bool ExposeToApps { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days a request may wait for a release before it fails.
    /// </summary>
    public int RequestTimeoutDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets the secret protecting the Lidarr webhook.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether APNs notifications are enabled.
    /// </summary>
    public bool ApnsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the APNs key id.
    /// </summary>
    public string ApnsKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the APNs team id.
    /// </summary>
    public string ApnsTeamId { get; set; } = DefaultApnsTeamId;

    /// <summary>
    /// Gets or sets the APNs topic (the app's bundle id).
    /// </summary>
    public string ApnsTopic { get; set; } = DefaultApnsTopic;

    /// <summary>
    /// Gets or sets the APNs private key as the configuration API shows it: <c>***</c> when a key is stored, never the key.
    /// Setting <c>***</c> keeps the stored key (see <see cref="CarryOverSecrets"/>), anything else replaces it.
    /// </summary>
    [XmlIgnore]
    public string ApnsPrivateKey
    {
        get => string.IsNullOrEmpty(ApnsPrivateKeyValue) ? string.Empty : MaskedSecret;
        set
        {
            if (value == MaskedSecret)
            {
                KeepApnsPrivateKey = true;
                return;
            }

            KeepApnsPrivateKey = false;
            ApnsPrivateKeyValue = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets or sets the APNs private key as stored in the plugin's XML file; not part of the configuration API.
    /// </summary>
    [JsonIgnore]
    public string ApnsPrivateKeyValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a saved configuration asked to keep the stored private key.
    /// </summary>
    [JsonIgnore]
    [XmlIgnore]
    public bool KeepApnsPrivateKey { get; set; }

    /// <summary>
    /// Takes the stored private key from the configuration being replaced when the new one only carries the mask.
    /// </summary>
    /// <param name="previous">The configuration being replaced.</param>
    public void CarryOverSecrets(PluginConfiguration previous)
    {
        if (KeepApnsPrivateKey)
        {
            ApnsPrivateKeyValue = previous.ApnsPrivateKeyValue;
            KeepApnsPrivateKey = false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether album requests can be taken: always in manual mode, in Lidarr mode once Lidarr is set up.
    /// </summary>
    /// <returns><c>true</c> when the <c>requests</c> feature is available.</returns>
    public bool RequestsEnabled() => FulfillmentMode == FulfillmentMode.Manual
        || (!string.IsNullOrWhiteSpace(LidarrUrl) && !string.IsNullOrWhiteSpace(LidarrApiKey));

    /// <summary>
    /// Generates a random 32-character alphanumeric token.
    /// </summary>
    /// <returns>The token.</returns>
    public static string GenerateSecret() => RandomNumberGenerator.GetString(SecretAlphabet, 32);

    /// <summary>
    /// Generates the webhook secret when it is empty.
    /// </summary>
    /// <returns><c>true</c> when a secret was generated and the configuration needs saving.</returns>
    public bool EnsureWebhookSecret()
    {
        if (!string.IsNullOrWhiteSpace(WebhookSecret))
        {
            return false;
        }

        WebhookSecret = GenerateSecret();
        return true;
    }
}
