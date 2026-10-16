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
    [property: JsonPropertyName("disambiguation")] string Disambiguation);

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
