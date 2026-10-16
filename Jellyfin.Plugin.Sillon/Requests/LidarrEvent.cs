using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>The part of Lidarr's webhook payload the plugin reads.</summary>
public sealed class LidarrEvent
{
    /// <summary>Gets the event type (Grab, Download, AlbumImport, ImportFailure, DownloadFailure, Test...).</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    /// <summary>Gets the Lidarr instance name.</summary>
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>Gets the failure message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the artist.</summary>
    [JsonPropertyName("artist")]
    public LidarrEventArtist? Artist { get; init; }

    /// <summary>Gets the album of single-album events.</summary>
    [JsonPropertyName("album")]
    public LidarrEventAlbum? Album { get; init; }

    /// <summary>Gets the albums of multi-album events.</summary>
    [JsonPropertyName("albums")]
    public List<LidarrEventAlbum>? Albums { get; init; }

    /// <summary>Gets the imported track files.</summary>
    [JsonPropertyName("trackFiles")]
    public List<LidarrEventTrackFile>? TrackFiles { get; init; }

    /// <summary>Gets the distinct MusicBrainz album ids of the event.</summary>
    /// <returns>The ids.</returns>
    public IReadOnlyList<string> AlbumIds()
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        Add(Album?.MbId);
        foreach (var album in Albums ?? new List<LidarrEventAlbum>())
        {
            Add(album.MbId);
        }

        return ids;
    }

    /// <summary>Gets the album folders (as Lidarr sees them) of an import: the directories of the track files, else the artist's folder.</summary>
    /// <returns>The folders.</returns>
    public IReadOnlyList<string> Folders()
    {
        var folders = new List<string>();
        foreach (var file in TrackFiles ?? new List<LidarrEventTrackFile>())
        {
            if (string.IsNullOrEmpty(file.Path))
            {
                continue;
            }

            var dir = PathMap.Dir(file.Path);
            if (!folders.Contains(dir))
            {
                folders.Add(dir);
            }
        }

        if (folders.Count == 0 && !string.IsNullOrEmpty(Artist?.Path))
        {
            folders.Add(Artist.Path);
        }

        return folders;
    }
}

/// <summary>Artist of a Lidarr event.</summary>
public sealed class LidarrEventArtist
{
    /// <summary>Gets the folder, as Lidarr sees it.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

/// <summary>Album of a Lidarr event.</summary>
public sealed class LidarrEventAlbum
{
    /// <summary>Gets the MusicBrainz album id.</summary>
    [JsonPropertyName("mbId")]
    public string MbId { get; init; } = string.Empty;
}

/// <summary>Track file of a Lidarr event.</summary>
public sealed class LidarrEventTrackFile
{
    /// <summary>Gets the path, as Lidarr sees it.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}
