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

    /// <summary>Gets the images.</summary>
    [JsonPropertyName("images")]
    public List<LidarrImage>? Images { get; init; }
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

    /// <summary>Gets the images.</summary>
    [JsonPropertyName("images")]
    public List<LidarrImage>? Images { get; init; }

    /// <summary>Gets the artist.</summary>
    [JsonPropertyName("artist")]
    public LidarrArtist? Artist { get; init; }

    /// <summary>Gets the file statistics.</summary>
    [JsonPropertyName("statistics")]
    public LidarrStats? Statistics { get; init; }
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

    /// <summary>Gets a value indicating whether the artist is monitored.</summary>
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
