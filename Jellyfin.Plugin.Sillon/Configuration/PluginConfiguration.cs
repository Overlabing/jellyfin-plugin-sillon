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
    public string LidarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Lidarr API key as stored in the plugin's XML file; not part of the configuration API.
    /// </summary>
    [JsonIgnore]
    public string LidarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Lidarr API key as the configuration API shows it: <c>***</c> when a key is stored, never the key.
    /// Setting <c>***</c> keeps the stored key (see <see cref="CarryOverSecrets"/>), anything else replaces it.
    /// </summary>
    [XmlIgnore]
    [JsonPropertyName("LidarrApiKey")]
    public string LidarrApiKeyMasked
    {
        get => string.IsNullOrEmpty(LidarrApiKey) ? string.Empty : MaskedSecret;
        set
        {
            if (value == MaskedSecret)
            {
                KeepLidarrApiKey = true;
                return;
            }

            KeepLidarrApiKey = false;
            LidarrApiKey = value?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether a saved configuration asked to keep the stored Lidarr API key.
    /// </summary>
    [JsonIgnore]
    [XmlIgnore]
    public bool KeepLidarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets how many requests a user may have in progress at once; 0 is no limit.
    /// </summary>
    public int MaxActiveRequestsPerUser { get; set; }

    /// <summary>
    /// Gets or sets how many requests a user may make in 24 hours; 0 is no limit.
    /// </summary>
    public int MaxRequestsPerUserPerDay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether administrators are exempt from the request limits.
    /// </summary>
    public bool ExemptAdminsFromLimits { get; set; } = true;

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
    public string LidarrPathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the same folder as Jellyfin sees it.
    /// </summary>
    public string JellyfinPathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the wishlist is offered to the apps through <c>features</c>.
    /// </summary>
    public bool ExposeToApps { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days a request may wait for a release before it fails.
    /// </summary>
    public int RequestTimeoutDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets the release-group MBIDs the catalogue offers, one per line (blank lines and <c>#</c> comments ignored); empty offers the whole catalogue.
    /// </summary>
    public string CatalogAllowList { get; set; } = string.Empty;

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
    public string ApnsTeamId { get; set; } = string.Empty;

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
        if (KeepLidarrApiKey)
        {
            LidarrApiKey = previous.LidarrApiKey;
            KeepLidarrApiKey = false;
        }

        if (KeepApnsPrivateKey)
        {
            ApnsPrivateKeyValue = previous.ApnsPrivateKeyValue;
            KeepApnsPrivateKey = false;
        }
    }

    /// <summary>
    /// Clamps the values an administrator typed to sane ranges and trims the text ones.
    /// </summary>
    public void Sanitize()
    {
        RequestTimeoutDays = Math.Clamp(RequestTimeoutDays, 1, 365);
        MaxActiveRequestsPerUser = Math.Clamp(MaxActiveRequestsPerUser, 0, 1000);
        MaxRequestsPerUserPerDay = Math.Clamp(MaxRequestsPerUserPerDay, 0, 1000);
        LidarrUrl = (LidarrUrl ?? string.Empty).Trim().TrimEnd('/');
        LidarrPathPrefix = (LidarrPathPrefix ?? string.Empty).Trim();
        JellyfinPathPrefix = (JellyfinPathPrefix ?? string.Empty).Trim();
        ApnsKeyId = (ApnsKeyId ?? string.Empty).Trim();
        ApnsTeamId = (ApnsTeamId ?? string.Empty).Trim();
        ApnsTopic = (ApnsTopic ?? string.Empty).Trim();
    }

    /// <summary>
    /// Checks that a Lidarr URL is empty or an absolute http(s) URL.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns><c>true</c> when it is acceptable.</returns>
    public static bool IsValidLidarrUrl(string? url)
        => string.IsNullOrWhiteSpace(url)
            || (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");

    /// <summary>
    /// Gets a value indicating whether album requests can be taken: always in manual mode, in Lidarr mode once Lidarr is set up.
    /// </summary>
    /// <returns><c>true</c> when the <c>requests</c> feature is available.</returns>
    public bool RequestsEnabled() => FulfillmentMode == FulfillmentMode.Manual
        || (!string.IsNullOrWhiteSpace(LidarrUrl) && !string.IsNullOrWhiteSpace(LidarrApiKey));

    /// <summary>
    /// Parses <see cref="CatalogAllowList"/>.
    /// </summary>
    /// <returns>The allowed release-group MBIDs, lower-cased; empty when the whole catalogue is offered.</returns>
    public HashSet<string> AllowedReleaseGroups()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (CatalogAllowList ?? string.Empty).Split('\n'))
        {
            var entry = line.Trim();
            if (entry.Length > 0 && entry[0] != '#')
            {
                allowed.Add(entry);
            }
        }

        return allowed;
    }

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
