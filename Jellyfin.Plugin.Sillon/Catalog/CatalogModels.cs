using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>An image of a catalogue artist.</summary>
/// <param name="CoverType">The cover type.</param>
/// <param name="Url">The absolute URL.</param>
public sealed record CatalogImage(
    [property: JsonPropertyName("coverType")] string CoverType,
    [property: JsonPropertyName("url")] string Url);

/// <summary>An artist of the catalogue.</summary>
/// <param name="Mbid">The MusicBrainz id.</param>
/// <param name="Name">The name.</param>
/// <param name="Disambiguation">The disambiguation text.</param>
/// <param name="Images">The images.</param>
/// <param name="ArtistType">The artist type (Person, Group, ...), empty when unknown.</param>
/// <param name="Genres">The genres.</param>
public sealed record CatalogArtist(
    [property: JsonPropertyName("mbid")] string Mbid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("disambiguation")] string Disambiguation,
    [property: JsonPropertyName("images")] IReadOnlyList<CatalogImage> Images,
    [property: JsonPropertyName("artistType")] string ArtistType,
    [property: JsonPropertyName("genres")] IReadOnlyList<string> Genres);

/// <summary>An album of the catalogue, with where it stands.</summary>
/// <param name="Mbid">The MusicBrainz id.</param>
/// <param name="ArtistMbid">The artist's MusicBrainz id.</param>
/// <param name="Title">The title.</param>
/// <param name="Artist">The artist name.</param>
/// <param name="Year">The release year, 0 when unknown.</param>
/// <param name="Type">The album type.</param>
/// <param name="CoverUrl">The cover URL, empty when unknown.</param>
/// <param name="State">library, requested, downloading, available, failed or absent.</param>
/// <param name="RequestId">The request id, when a request exists.</param>
/// <param name="AlbumType">Lidarr's album type: Album, EP, Single, Broadcast or Other.</param>
/// <param name="SecondaryTypes">Lidarr's secondary types (Compilation, Live, ...).</param>
/// <param name="DiscCount">The most discs of any release, 1 when unknown.</param>
/// <param name="TrackCount">The tracks of the primary release, 0 when unknown.</param>
/// <param name="Label">The label of the primary release, when known.</param>
/// <param name="ReleaseDate">The release date as Lidarr gives it, when known.</param>
/// <param name="Disambiguation">The disambiguation text, empty when none.</param>
/// <param name="MatchedTrack">The track the search matched on this album, absent for a match on the album or artist name. Read by apps newer than 1.1.1.</param>
/// <param name="Phase">searching or notFoundYet while the request waits for a release, absent otherwise. Read by apps newer than 1.1.1.</param>
public sealed record CatalogAlbum(
    [property: JsonPropertyName("mbid")] string Mbid,
    [property: JsonPropertyName("artistMbid")] string ArtistMbid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coverUrl")] string CoverUrl,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("requestId")] long? RequestId,
    [property: JsonPropertyName("albumType")] string AlbumType,
    [property: JsonPropertyName("secondaryTypes")] IReadOnlyList<string> SecondaryTypes,
    [property: JsonPropertyName("discCount")] int DiscCount,
    [property: JsonPropertyName("trackCount")] int TrackCount,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("disambiguation")] string Disambiguation,
    [property: JsonPropertyName("matchedTrack"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CatalogTrackMatch? MatchedTrack = null,
    [property: JsonPropertyName("phase"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase = null);

/// <summary>The track a search matched on an album.</summary>
/// <param name="Title">The track title.</param>
/// <param name="RecordingMbid">The recording's MusicBrainz id; the tracklist of the album carries the same id on the same track.</param>
/// <param name="Disc">The disc on the first release MusicBrainz listed, absent when unknown. The tracklist's release may differ: match on the recording id first.</param>
/// <param name="Position">The position on that disc, absent when unknown.</param>
public sealed record CatalogTrackMatch(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("recordingMbid")] string RecordingMbid,
    [property: JsonPropertyName("disc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Disc = null,
    [property: JsonPropertyName("position"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Position = null);

/// <summary>A track of a catalogue album.</summary>
/// <param name="Disc">The disc, from 1.</param>
/// <param name="Position">The position on the disc, from 1.</param>
/// <param name="Number">The number as printed on the release (<c>1</c>, <c>A1</c>).</param>
/// <param name="Title">The title.</param>
/// <param name="DurationMs">The length in milliseconds, absent when unknown.</param>
/// <param name="RecordingMbid">The recording's MusicBrainz id, empty when unknown.</param>
/// <param name="Matched">True for the track asked about (by recording id, else by title); absent otherwise.</param>
public sealed record CatalogTrack(
    [property: JsonPropertyName("disc")] int Disc,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("durationMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationMs,
    [property: JsonPropertyName("recordingMbid")] string RecordingMbid,
    [property: JsonPropertyName("matched"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Matched = null);

/// <summary>The tracklist of a catalogue album.</summary>
/// <param name="AlbumMbid">The release group's MusicBrainz id.</param>
/// <param name="ReleaseMbid">The release the tracks come from.</param>
/// <param name="Title">The release title.</param>
/// <param name="DiscCount">The number of discs.</param>
/// <param name="Tracks">The tracks, disc by disc.</param>
public sealed record CatalogTracklist(
    [property: JsonPropertyName("albumMbid")] string AlbumMbid,
    [property: JsonPropertyName("releaseMbid")] string ReleaseMbid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("discCount")] int DiscCount,
    [property: JsonPropertyName("tracks")] IReadOnlyList<CatalogTrack> Tracks);

/// <summary>Result of a catalogue search.</summary>
/// <param name="Artists">The artists.</param>
/// <param name="Albums">The albums.</param>
public sealed record CatalogSearchResult(
    [property: JsonPropertyName("artists")] IReadOnlyList<CatalogArtist> Artists,
    [property: JsonPropertyName("albums")] IReadOnlyList<CatalogAlbum> Albums);

/// <summary>An artist's albums.</summary>
/// <param name="Artist">The artist; absent when only albums were found.</param>
/// <param name="Albums">The albums.</param>
public sealed record CatalogArtistResult(
    [property: JsonPropertyName("artist")] CatalogArtist? Artist,
    [property: JsonPropertyName("albums")] IReadOnlyList<CatalogAlbum> Albums);

/// <summary>The catalogue cannot answer; carries the HTTP status to answer with.</summary>
public sealed class CatalogException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CatalogException"/> class.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="message">The message shown to the user.</param>
    public CatalogException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the HTTP status.</summary>
    public int StatusCode { get; }
}
