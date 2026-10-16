using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sillon.Storage;

/// <summary>Request states.</summary>
public static class RequestStatus
{
    /// <summary>Waiting for a release.</summary>
    public const string Requested = "requested";

    /// <summary>A release is being downloaded.</summary>
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
}
