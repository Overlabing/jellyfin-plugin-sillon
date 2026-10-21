using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sillon.Storage;

/// <summary>Request states.</summary>
public static class RequestStatus
{
    /// <summary>Waiting for a release.</summary>
    public const string Requested = "requested";

    /// <summary>Lidarr has picked up a release.</summary>
    public const string Downloading = "downloading";

    /// <summary>The album is in the library.</summary>
    public const string Available = "available";

    /// <summary>The request ended without the album.</summary>
    public const string Failed = "failed";

    /// <summary>The request was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The states of a request that is still being worked on.</summary>
    public static readonly IReadOnlyList<string> Live = new[] { Requested, Downloading };
}

/// <summary>One album request, as stored in <c>requests.json</c>.</summary>
public sealed record RequestRecord
{
    /// <summary>Gets the id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the requester's Jellyfin user id.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Gets the requester's name.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz album id.</summary>
    public string ForeignAlbumId { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz artist id.</summary>
    public string ForeignArtistId { get; init; } = string.Empty;

    /// <summary>Gets the Lidarr album id.</summary>
    public long? LidarrAlbumId { get; init; }

    /// <summary>Gets the artist name.</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the album title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the release year, 0 when unknown.</summary>
    public int Year { get; init; }

    /// <summary>Gets the cover URL.</summary>
    public string CoverUrl { get; init; } = string.Empty;

    /// <summary>Gets the status.</summary>
    public string Status { get; init; } = RequestStatus.Requested;

    /// <summary>Gets the Jellyfin album id once the album is in the library.</summary>
    public string JellyfinItemId { get; init; } = string.Empty;

    /// <summary>Gets the error text.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Gets the number of imported tracks of a partial album.</summary>
    public int TracksImported { get; init; }

    /// <summary>Gets the number of tracks of the album.</summary>
    public int TracksTotal { get; init; }

    /// <summary>Gets when the requester was told the album is available.</summary>
    public DateTimeOffset? NotifiedAt { get; init; }

    /// <summary>Gets when the album was marked favourite for the requester.</summary>
    public DateTimeOffset? LibraryAddedAt { get; init; }

    /// <summary>Gets when Lidarr was last asked to search for the album.</summary>
    public DateTimeOffset? LastSearchAt { get; init; }

    /// <summary>Gets how many times adding the album to Lidarr failed while the request had no Lidarr album id.</summary>
    public int AddAttempts { get; init; }

    /// <summary>Gets when the last failed Lidarr add ended.</summary>
    public DateTimeOffset? LastAddAttemptAt { get; init; }

    /// <summary>Gets when the reconcile last searched the album again on its own; absent in records written before 0.5.1.0.</summary>
    public DateTimeOffset? LastReSearchAt { get; init; }

    /// <summary>Gets when the reconcile last retried a failed import with any release accepted; absent in records written before 0.6.1.0.</summary>
    public DateTimeOffset? LastImportRetryAt { get; init; }

    /// <summary>Gets the album folder as Lidarr sees it; absent in records written before 0.6.0.0.</summary>
    public string? LidarrFolder { get; init; }

    /// <summary>Gets when the library was last asked to scan the album folder.</summary>
    public DateTimeOffset? LastFolderRefreshAt { get; init; }

    /// <summary>Gets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the last change time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets a value indicating whether the request is still being worked on.</summary>
    [JsonIgnore]
    public bool IsActive => Status is RequestStatus.Requested or RequestStatus.Downloading;
}

/// <summary>A registered push token.</summary>
public sealed record DeviceRecord
{
    /// <summary>Gets the token.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>Gets the owner's Jellyfin user id.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Gets the platform.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Gets the APNs environment, <c>development</c>, <c>production</c> or empty for the default.</summary>
    public string Env { get; init; } = string.Empty;

    /// <summary>Gets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the last change time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the device public key (65 bytes, base64url) of a device registered for the relay; empty for a token-only device.</summary>
    public string DevicePublicKey { get; init; } = string.Empty;

    /// <summary>Gets how the device is notified: <c>relay</c> (no token, a device key) or empty for a token device (direct mode).</summary>
    public string PushMode { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the device is notified through the relay.</summary>
    [JsonIgnore]
    public bool IsRelay => PushMode == "relay" && DevicePublicKey.Length > 0;

    /// <summary>Gets the relay device id: the SHA-256 of the device public key, base64url; empty without a key.</summary>
    [JsonIgnore]
    public string DeviceId
        => DevicePublicKey.Length == 0 ? string.Empty : Push.RelayCrypto.B64(System.Security.Cryptography.SHA256.HashData(Push.RelayCrypto.FromB64(DevicePublicKey) ?? Array.Empty<byte>()));

    /// <summary>Gets an id safe to show and to pass around: the first 16 hex digits of the token's SHA-256 (of the device id for a relay device), never the token.</summary>
    [JsonIgnore]
    public string PublicId => IsRelay && Token.Length == 0
        ? Convert.ToHexString(Push.RelayCrypto.FromB64(DeviceId) ?? Array.Empty<byte>())[..16].ToLowerInvariant()
        : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Token)))[..16].ToLowerInvariant();

    /// <summary>Gets the last 6 characters of the token (of the device id for a relay device), to tell devices apart.</summary>
    [JsonIgnore]
    public string TokenHint => Token.Length == 0 ? (DeviceId.Length <= 6 ? DeviceId : DeviceId[..6]) : (Token.Length <= 6 ? Token : Token[^6..]);
}
