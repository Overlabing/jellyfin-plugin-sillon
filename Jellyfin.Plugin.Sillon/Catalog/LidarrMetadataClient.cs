using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>A release group as Lidarr's metadata service lists it.</summary>
/// <param name="Id">The release group's MusicBrainz id.</param>
/// <param name="Title">The title.</param>
/// <param name="Type">Album, EP, Single, Broadcast or Other.</param>
/// <param name="SecondaryTypes">Compilation, Live, ...</param>
/// <param name="ReleaseStatuses">The statuses of its releases (Official, Bootleg, ...), empty when none is known.</param>
/// <param name="ReleaseDate">The date as the service gives it (<c>1969-09-26</c>), null when unknown.</param>
/// <param name="OldIds">The ids of the release groups merged into this one.</param>
public sealed record MetadataAlbum(string Id, string Title, string Type, IReadOnlyList<string> SecondaryTypes, IReadOnlyList<string> ReleaseStatuses, string? ReleaseDate, IReadOnlyList<string> OldIds);

/// <summary>An image of an artist as the metadata service lists it.</summary>
/// <param name="CoverType">The cover type, in lower case.</param>
/// <param name="Url">The absolute URL.</param>
public sealed record MetadataImage(string CoverType, string Url);

/// <summary>An artist and all of its release groups, as Lidarr's metadata service lists them.</summary>
/// <param name="Id">The artist's current MusicBrainz id.</param>
/// <param name="Name">The name.</param>
/// <param name="Disambiguation">The disambiguation text.</param>
/// <param name="ArtistType">Person, Group, ...</param>
/// <param name="Genres">The genres.</param>
/// <param name="Images">The images.</param>
/// <param name="Albums">The release groups, in no particular order. Only those that credit the artist first: not the joint albums that name someone else first.</param>
public sealed record MetadataArtist(string Id, string Name, string Disambiguation, string ArtistType, IReadOnlyList<string> Genres, IReadOnlyList<MetadataImage> Images, IReadOnlyList<MetadataAlbum> Albums);

/// <summary>Where a whole discography can be had in one request.</summary>
public interface IArtistMetadata
{
    /// <summary>Asks for an artist and all of its release groups.</summary>
    /// <param name="artistId">The artist's MusicBrainz id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artist; null when the service gave no usable answer, which says nothing about the artist.</returns>
    Task<MetadataArtist?> GetArtistAsync(string artistId, CancellationToken ct);
}

/// <summary>
/// The service Lidarr itself gets its metadata from. It is not a documented public API, so nothing here relies on it: one request with a short timeout and no retry,
/// any failure or answer of an unexpected shape counts as no answer and leaves it alone for a minute, and only complete answers are kept. Nothing about the user or the
/// server goes in the request, and the User-Agent only names the plugin and its version.
/// </summary>
public sealed class LidarrMetadataClient : IArtistMetadata
{
    private const int MaxCached = 200;
    private const long MaxResponseBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, MetadataArtist Artist)> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private bool _down;

    /// <summary>Initializes a new instance of the <see cref="LidarrMetadataClient"/> class.</summary>
    /// <param name="http">The HTTP client (see <see cref="CreateHttpClient"/>).</param>
    /// <param name="version">The plugin version, for the User-Agent.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    /// <param name="baseUrl">The service root; Lidarr's when null.</param>
    /// <param name="timeout">How long it may take to answer; 3 seconds when null, which the 15 of an artist page and the 8 of a search can afford before asking MusicBrainz.</param>
    public LidarrMetadataClient(HttpClient http, string version, ILogger logger, Func<DateTimeOffset>? now = null, string? baseUrl = null, TimeSpan? timeout = null)
    {
        _http = http;
        _userAgent = $"Sillon-Plugin/{version}";
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _baseUrl = (baseUrl ?? "https://api.lidarr.audio").TrimEnd('/');
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Makes the HTTP client this class needs: no cookie, compressed answers (The Beatles weigh 232 KB, 52 KB compressed), and the service's redirect from a merged artist's old id.</summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateHttpClient()
        => new(new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes
        };

    /// <inheritdoc />
    public async Task<MetadataArtist?> GetArtistAsync(string artistId, CancellationToken ct)
    {
        var key = artistId.ToLowerInvariant();
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var kept) && kept.Expires > _now())
            {
                return kept.Artist;
            }

            if (_pausedUntil > _now())
            {
                return null;
            }
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0.4/artist/{Uri.EscapeDataString(artistId)}");
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            // the service is fine and does not know this artist: nothing to pause for, and MusicBrainz may still know it
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Up();
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var artist = Parse(body, key);
            Up();
            if (artist.Albums.Count > 0)
            {
                Keep(key, artist);
                return artist;
            }

            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            Down(ex is OperationCanceledException ? "timed out" : ex is JsonException ? "unexpected answer" : ex.Message);
            return null;
        }
    }

    /// <summary>Reads an answer, refusing anything but the shape the service is known to give: a field gone or renamed must not pass for an artist without albums or for a type the settings hide.</summary>
    private static MetadataArtist Parse(string body, string requested)
    {
        var raw = JsonSerializer.Deserialize<RawArtist>(body, Json) ?? throw new JsonException("empty answer");
        if (string.IsNullOrEmpty(raw.Id)
            || string.IsNullOrEmpty(raw.Name)
            || raw.Albums is null
            || (!raw.Id.Equals(requested, StringComparison.OrdinalIgnoreCase) && raw.OldIds?.Contains(requested, StringComparer.OrdinalIgnoreCase) != true))
        {
            throw new JsonException("not the artist asked for");
        }

        var albums = new List<MetadataAlbum>(raw.Albums.Count);
        foreach (var album in raw.Albums)
        {
            if (string.IsNullOrEmpty(album.Id) || string.IsNullOrEmpty(album.Title) || string.IsNullOrEmpty(album.Type) || album.SecondaryTypes is null || album.ReleaseStatuses is null)
            {
                throw new JsonException("not an album");
            }

            albums.Add(new MetadataAlbum(album.Id, album.Title, album.Type, album.SecondaryTypes, album.ReleaseStatuses, album.ReleaseDate, album.OldIds ?? new List<string>()));
        }

        var images = (raw.Images ?? new List<RawImage>())
            .Where(i => !string.IsNullOrEmpty(i.CoverType) && (i.Url?.StartsWith("http", StringComparison.Ordinal) ?? false))
            .Select(i => new MetadataImage(i.CoverType!.ToLowerInvariant(), i.Url!))
            .ToList();
        return new MetadataArtist(raw.Id, raw.Name, raw.Disambiguation ?? string.Empty, raw.Type ?? string.Empty, raw.Genres ?? new List<string>(), images, albums);
    }

    private void Keep(string key, MetadataArtist artist)
    {
        lock (_lock)
        {
            if (_cache.Count >= MaxCached)
            {
                _cache.Clear();
            }

            _cache[key] = (_now() + Ttl, artist);
        }
    }

    private void Up()
    {
        lock (_lock)
        {
            _down = false;
        }
    }

    private void Down(string reason)
    {
        bool first;
        lock (_lock)
        {
            first = !_down;
            _down = true;
            _pausedUntil = _now() + FailurePause;
        }

        if (first)
        {
            _logger.LogWarning("Lidarr's metadata service unavailable ({Reason}): asking MusicBrainz instead", reason);
        }
    }

    private sealed class RawArtist
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("oldids")]
        public List<string>? OldIds { get; init; }

        [JsonPropertyName("artistname")]
        public string? Name { get; init; }

        [JsonPropertyName("disambiguation")]
        public string? Disambiguation { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("genres")]
        public List<string>? Genres { get; init; }

        [JsonPropertyName("images")]
        public List<RawImage>? Images { get; init; }

        [JsonPropertyName("Albums")]
        public List<RawAlbum>? Albums { get; init; }
    }

    private sealed class RawImage
    {
        [JsonPropertyName("CoverType")]
        public string? CoverType { get; init; }

        [JsonPropertyName("Url")]
        public string? Url { get; init; }
    }

    private sealed class RawAlbum
    {
        [JsonPropertyName("Id")]
        public string? Id { get; init; }

        [JsonPropertyName("OldIds")]
        public List<string>? OldIds { get; init; }

        [JsonPropertyName("Title")]
        public string? Title { get; init; }

        [JsonPropertyName("Type")]
        public string? Type { get; init; }

        [JsonPropertyName("SecondaryTypes")]
        public List<string>? SecondaryTypes { get; init; }

        [JsonPropertyName("ReleaseStatuses")]
        public List<string>? ReleaseStatuses { get; init; }

        [JsonPropertyName("ReleaseDate")]
        public string? ReleaseDate { get; init; }
    }
}
