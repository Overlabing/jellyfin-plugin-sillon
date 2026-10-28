using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>An album request as the Sillon app reads it (the shape the former gateway served).</summary>
public sealed record RequestDto
{
    /// <summary>Gets the id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>Gets the requester's user id.</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    /// <summary>Gets the requester's name.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz album id.</summary>
    [JsonPropertyName("foreignAlbumId")]
    public string ForeignAlbumId { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz artist id.</summary>
    [JsonPropertyName("foreignArtistId")]
    public string ForeignArtistId { get; init; } = string.Empty;

    /// <summary>Gets the Lidarr album id, absent when unknown.</summary>
    [JsonPropertyName("lidarrAlbumId")]
    public long? LidarrAlbumId { get; init; }

    /// <summary>Gets the artist name.</summary>
    [JsonPropertyName("artist")]
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the year.</summary>
    [JsonPropertyName("year")]
    public int Year { get; init; }

    /// <summary>Gets the cover URL.</summary>
    [JsonPropertyName("coverUrl")]
    public string CoverUrl { get; init; } = string.Empty;

    /// <summary>Gets the status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the Jellyfin album id, absent until the album is in the library.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public string? JellyfinItemId { get; init; }

    /// <summary>Gets the error text, absent when there is none.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Gets the imported tracks of a partial album, absent when zero.</summary>
    [JsonPropertyName("tracksImported")]
    public int? TracksImported { get; init; }

    /// <summary>Gets the tracks of a partial album, absent when zero.</summary>
    [JsonPropertyName("tracksTotal")]
    public int? TracksTotal { get; init; }

    /// <summary>Gets when the album was added to the requester's favourites, absent until then.</summary>
    [JsonPropertyName("libraryAddedAt")]
    public DateTimeOffset? LibraryAddedAt { get; init; }

    /// <summary>Gets when Lidarr was last asked to search for the album, absent until then.</summary>
    [JsonPropertyName("lastSearchAt")]
    public DateTimeOffset? LastSearchAt { get; init; }

    /// <summary>Gets searching or notFoundYet while the request waits for a release; absent otherwise. The status stays requested: apps that predate the field show it as waiting.</summary>
    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; init; }

    /// <summary>Gets the creation time.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the last change time.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Builds the API shape of a stored request.</summary>
    /// <param name="r">The stored request.</param>
    /// <param name="phase">The phase of the request, when it waits for a release.</param>
    /// <returns>The DTO.</returns>
    public static RequestDto From(RequestRecord r, string? phase = null) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        UserName = r.UserName,
        ForeignAlbumId = r.ForeignAlbumId,
        ForeignArtistId = r.ForeignArtistId,
        LidarrAlbumId = r.LidarrAlbumId,
        Artist = r.Artist,
        Title = r.Title,
        Year = r.Year,
        CoverUrl = r.CoverUrl,
        Status = r.Status,
        JellyfinItemId = r.JellyfinItemId.Length > 0 ? r.JellyfinItemId : null,
        Error = r.Error.Length > 0 ? r.Error : null,
        TracksImported = r.TracksImported > 0 ? r.TracksImported : null,
        TracksTotal = r.TracksTotal > 0 ? r.TracksTotal : null,
        LibraryAddedAt = r.LibraryAddedAt,
        LastSearchAt = r.LastSearchAt,
        Phase = phase,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt
    };
}

/// <summary>JSON the app reads: written by hand so it does not depend on the server's MVC serializer settings.</summary>
public static class ApiJson
{
    /// <summary>The serializer options of every Sillon response.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Builds a JSON response.</summary>
    /// <param name="value">The body.</param>
    /// <param name="status">The HTTP status.</param>
    /// <returns>The result.</returns>
    public static ContentResult Result(object value, int status = 200) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), Options),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status
    };

    /// <summary>Builds an <c>{"error": ...}</c> response.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="message">The message.</param>
    /// <returns>The result.</returns>
    public static ContentResult Error(int status, string message) => Result(new ErrorBody(message), status);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new Rfc3339Converter());
        return options;
    }

    private sealed record ErrorBody([property: JsonPropertyName("error")] string Error);

    // UTC with up to seven fractional digits, "Z": what Go's time.Time writes and the app parses
    private sealed class Rfc3339Converter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => DateTimeOffset.Parse(reader.GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
