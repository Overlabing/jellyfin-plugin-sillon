using System.Security.Cryptography;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sillon.Configuration;

/// <summary>
/// How requests are fulfilled.
/// </summary>
public enum FulfillmentMode
{
    /// <summary>
    /// Requests are handled by hand.
    /// </summary>
    Manual,

    /// <summary>
    /// Requests are forwarded to Lidarr.
    /// </summary>
    Lidarr
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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
    /// Gets or sets how many days a request may wait for a release before it fails.
    /// </summary>
    public int RequestTimeoutDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets the secret protecting the Lidarr webhook.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether APNs notifications are enabled (later phase).
    /// </summary>
    public bool ApnsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the APNs key id (later phase).
    /// </summary>
    public string ApnsKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the APNs team id (later phase).
    /// </summary>
    public string ApnsTeamId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the APNs topic (later phase).
    /// </summary>
    public string ApnsTopic { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the APNs private key (later phase).
    /// </summary>
    public string ApnsPrivateKey { get; set; } = string.Empty;

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
