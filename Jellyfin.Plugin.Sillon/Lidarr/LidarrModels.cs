using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sillon.Lidarr;

/// <summary>An image of an artist or album.</summary>
public sealed class LidarrImage
{
    /// <summary>Gets the cover type.</summary>
    [JsonPropertyName("coverType")]
    public string CoverType { get; init; } = string.Empty;

    /// <summary>Gets the Lidarr-relative or absolute URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets the remote URL.</summary>
    [JsonPropertyName("remoteUrl")]
    public string RemoteUrl { get; init; } = string.Empty;
}

/// <summary>An artist, in a search or nested in an album.</summary>
public sealed class LidarrArtist
{
    /// <summary>Gets the Lidarr id, 0 when Lidarr only knows the artist from a lookup.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the name.</summary>
    [JsonPropertyName("artistName")]
    public string ArtistName { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz id.</summary>
    [JsonPropertyName("foreignArtistId")]
    public string ForeignArtistId { get; init; } = string.Empty;

    /// <summary>Gets the disambiguation text.</summary>
    [JsonPropertyName("disambiguation")]
    public string Disambiguation { get; init; } = string.Empty;

    /// <summary>Gets the folder, as Lidarr sees it.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether Lidarr monitors the artist.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; init; }

    /// <summary>Gets the artist type (Person, Group, ...).</summary>
    [JsonPropertyName("artistType")]
    public string ArtistType { get; init; } = string.Empty;

    /// <summary>Gets the genres.</summary>
    [JsonPropertyName("genres")]
    public List<string>? Genres { get; init; }

    /// <summary>Gets the images.</summary>
    [JsonPropertyName("images")]
    public List<LidarrImage>? Images { get; init; }
}

/// <summary>A release of an album (one edition).</summary>
public sealed class LidarrRelease
{
    /// <summary>Gets the MusicBrainz id of the release.</summary>
    [JsonPropertyName("foreignReleaseId")]
    public string ForeignReleaseId { get; init; } = string.Empty;

    /// <summary>Gets the number of discs.</summary>
    [JsonPropertyName("mediumCount")]
    public int MediumCount { get; init; }

    /// <summary>Gets the number of tracks.</summary>
    [JsonPropertyName("trackCount")]
    public int TrackCount { get; init; }

    /// <summary>Gets the labels.</summary>
    [JsonPropertyName("label")]
    public List<string>? Label { get; init; }

    /// <summary>Gets the disambiguation text.</summary>
    [JsonPropertyName("disambiguation")]
    public string Disambiguation { get; init; } = string.Empty;

    /// <summary>Gets the countries the release came out in.</summary>
    [JsonPropertyName("country")]
    public List<string>? Country { get; init; }

    /// <summary>Gets the status (Official, ...).</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

/// <summary>An album's file statistics.</summary>
public sealed class LidarrStats
{
    /// <summary>Gets the percentage of tracks Lidarr holds.</summary>
    [JsonPropertyName("percentOfTracks")]
    public double PercentOfTracks { get; init; }

    /// <summary>Gets the number of track files.</summary>
    [JsonPropertyName("trackFileCount")]
    public int TrackFileCount { get; init; }

    /// <summary>Gets the number of tracks.</summary>
    [JsonPropertyName("trackCount")]
    public int TrackCount { get; init; }
}

/// <summary>A track file Lidarr holds.</summary>
public sealed class LidarrTrackFile
{
    /// <summary>Gets the path, as Lidarr sees it.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

/// <summary>An album; the id is 0 when Lidarr only knows it from a lookup.</summary>
public sealed class LidarrAlbum
{
    /// <summary>Gets the Lidarr id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz id.</summary>
    [JsonPropertyName("foreignAlbumId")]
    public string ForeignAlbumId { get; init; } = string.Empty;

    /// <summary>Gets the release date.</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }

    /// <summary>Gets the album type.</summary>
    [JsonPropertyName("albumType")]
    public string AlbumType { get; init; } = string.Empty;

    /// <summary>Gets the secondary types (Compilation, Live, ...).</summary>
    [JsonPropertyName("secondaryTypes")]
    public List<string>? SecondaryTypes { get; init; }

    /// <summary>Gets the disambiguation text.</summary>
    [JsonPropertyName("disambiguation")]
    public string Disambiguation { get; init; } = string.Empty;

    /// <summary>Gets the releases.</summary>
    [JsonPropertyName("releases")]
    public List<LidarrRelease>? Releases { get; init; }

    /// <summary>Gets the images.</summary>
    [JsonPropertyName("images")]
    public List<LidarrImage>? Images { get; init; }

    /// <summary>Gets the artist.</summary>
    [JsonPropertyName("artist")]
    public LidarrArtist? Artist { get; init; }

    /// <summary>Gets the Lidarr id of the artist, 0 when the album is only known from a lookup.</summary>
    [JsonPropertyName("artistId")]
    public int ArtistId { get; init; }

    /// <summary>Gets the file statistics.</summary>
    [JsonPropertyName("statistics")]
    public LidarrStats? Statistics { get; init; }

    /// <summary>Gets the duration in milliseconds, 0 until Lidarr has refreshed the album.</summary>
    [JsonPropertyName("duration")]
    public long Duration { get; init; }

    /// <summary>Gets a value indicating whether Lidarr monitors the album, null when the field is absent.</summary>
    [JsonPropertyName("monitored")]
    public bool? Monitored { get; init; }
}

/// <summary>One /search result: an artist or an album.</summary>
public sealed class LidarrSearchItem
{
    /// <summary>Gets the artist, when the item is one.</summary>
    [JsonPropertyName("artist")]
    public LidarrArtist? Artist { get; init; }

    /// <summary>Gets the album, when the item is one.</summary>
    [JsonPropertyName("album")]
    public LidarrAlbum? Album { get; init; }
}

/// <summary>One item of the queue.</summary>
public sealed class LidarrQueueRecord
{
    /// <summary>Gets the album id.</summary>
    [JsonPropertyName("albumId")]
    public int AlbumId { get; init; }

    /// <summary>Gets the album, when the queue was asked to include it.</summary>
    [JsonPropertyName("album")]
    public LidarrAlbum? Album { get; init; }

    /// <summary>Gets the tracked download state (importFailed, ...).</summary>
    [JsonPropertyName("trackedDownloadState")]
    public string? TrackedDownloadState { get; init; }

    /// <summary>Gets the folder the download client wrote, as Lidarr sees it.</summary>
    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; init; }

    /// <summary>Gets the status messages.</summary>
    [JsonPropertyName("statusMessages")]
    public List<LidarrStatusMessage>? StatusMessages { get; init; }
}

/// <summary>A status message of a queue item.</summary>
public sealed class LidarrStatusMessage
{
    /// <summary>Gets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Gets the messages.</summary>
    [JsonPropertyName("messages")]
    public List<string>? Messages { get; init; }
}

/// <summary>Body of POST /album.</summary>
public sealed class LidarrAddAlbum
{
    /// <summary>Gets the MusicBrainz album id.</summary>
    [JsonPropertyName("foreignAlbumId")]
    public string ForeignAlbumId { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the album is monitored.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; init; }

    /// <summary>Gets a value indicating whether Lidarr imports any edition of the album, not only the monitored release.</summary>
    [JsonPropertyName("anyReleaseOk")]
    public bool AnyReleaseOk { get; init; }

    /// <summary>Gets the artist block.</summary>
    [JsonPropertyName("artist")]
    public LidarrAddArtist Artist { get; init; } = new();

    /// <summary>Gets the add options.</summary>
    [JsonPropertyName("addOptions")]
    public LidarrAddAlbumOptions AddOptions { get; init; } = new();
}

/// <summary>Artist block of <see cref="LidarrAddAlbum"/>.</summary>
public sealed class LidarrAddArtist
{
    /// <summary>Gets the MusicBrainz artist id.</summary>
    [JsonPropertyName("foreignArtistId")]
    public string ForeignArtistId { get; init; } = string.Empty;

    /// <summary>Gets the quality profile id.</summary>
    [JsonPropertyName("qualityProfileId")]
    public int QualityProfileId { get; init; }

    /// <summary>Gets the metadata profile id.</summary>
    [JsonPropertyName("metadataProfileId")]
    public int MetadataProfileId { get; init; }

    /// <summary>Gets the root folder.</summary>
    [JsonPropertyName("rootFolderPath")]
    public string RootFolderPath { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the artist is monitored. Lidarr's wanted list and RSS sync only consider a monitored album of a monitored artist.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; init; }

    /// <summary>Gets what to do with the artist's future albums.</summary>
    [JsonPropertyName("monitorNewItems")]
    public string MonitorNewItems { get; init; } = "none";

    /// <summary>Gets the artist's add options.</summary>
    [JsonPropertyName("addOptions")]
    public LidarrAddArtistOptions AddOptions { get; init; } = new();
}

/// <summary>Add options of an artist.</summary>
public sealed class LidarrAddArtistOptions
{
    /// <summary>Gets which albums to monitor.</summary>
    [JsonPropertyName("monitor")]
    public string Monitor { get; init; } = "none";

    /// <summary>Gets a value indicating whether to search for the artist's missing albums.</summary>
    [JsonPropertyName("searchForMissingAlbums")]
    public bool SearchForMissingAlbums { get; init; }
}

/// <summary>Add options of an album.</summary>
public sealed class LidarrAddAlbumOptions
{
    /// <summary>Gets a value indicating whether to search for the album once added.</summary>
    [JsonPropertyName("searchForNewAlbum")]
    public bool SearchForNewAlbum { get; init; }
}
