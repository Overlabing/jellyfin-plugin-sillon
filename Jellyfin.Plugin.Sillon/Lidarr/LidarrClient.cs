using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Sillon.Requests;

namespace Jellyfin.Plugin.Sillon.Lidarr;

/// <summary>Lidarr system status.</summary>
/// <param name="Version">Lidarr version.</param>
public sealed record LidarrStatus([property: JsonPropertyName("version")] string Version);

/// <summary>A Lidarr quality or metadata profile.</summary>
/// <param name="Id">Profile id.</param>
/// <param name="Name">Profile name.</param>
public sealed record LidarrProfile(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>A Lidarr root folder.</summary>
/// <param name="Id">Folder id.</param>
/// <param name="Path">Folder path.</param>
public sealed record LidarrRootFolder(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("path")] string Path);

/// <summary>A failure talking to Lidarr; the message is safe to show to an admin.</summary>
public sealed class LidarrException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LidarrException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public LidarrException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>What the plugin needs of Lidarr; implemented by <see cref="LidarrClient"/>.</summary>
public interface ILidarrApi
{
    /// <summary>The combined artist and album lookup.</summary>
    /// <param name="term">The search term.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The items.</returns>
    Task<IReadOnlyList<LidarrSearchItem>> SearchAsync(string term, CancellationToken ct = default);

    /// <summary>The album-only lookup.</summary>
    /// <param name="term">The search term.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The albums.</returns>
    Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct = default);

    /// <summary>Lists the albums of an artist Lidarr holds.</summary>
    /// <param name="artistId">The Lidarr artist id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The albums.</returns>
    Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct = default);

    /// <summary>Returns the album when Lidarr already holds it.</summary>
    /// <param name="foreignAlbumId">The MusicBrainz album id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Zero or one album.</returns>
    Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, CancellationToken ct = default);

    /// <summary>Adds an album (and its artist, unmonitored).</summary>
    /// <param name="album">The album.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The added album.</returns>
    Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum album, CancellationToken ct = default);

    /// <summary>Sets the monitored flag of albums.</summary>
    /// <param name="ids">The Lidarr album ids.</param>
    /// <param name="monitored">The flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task MonitorAlbumsAsync(IReadOnlyList<int> ids, bool monitored, CancellationToken ct = default);

    /// <summary>Queues an AlbumSearch command.</summary>
    /// <param name="ids">The Lidarr album ids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AlbumSearchAsync(IReadOnlyList<int> ids, CancellationToken ct = default);

    /// <summary>Returns one album with its statistics.</summary>
    /// <param name="id">The Lidarr album id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The album.</returns>
    Task<LidarrAlbum> GetAlbumAsync(int id, CancellationToken ct = default);

    /// <summary>Returns the queue.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The queue records.</returns>
    Task<IReadOnlyList<LidarrQueueRecord>> GetQueueAsync(CancellationToken ct = default);

    /// <summary>Lists the track files Lidarr holds for an album.</summary>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The track files.</returns>
    Task<IReadOnlyList<LidarrTrackFile>> GetTrackFilesAsync(int albumId, CancellationToken ct = default);

    /// <summary>Returns the folder, as Lidarr sees it, that holds an album's track files (above the disc folders).</summary>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folder, or null when Lidarr holds no file for the album.</returns>
    Task<string?> GetAlbumFolderAsync(int albumId, CancellationToken ct = default);
}

/// <summary>Minimal Lidarr API client. Never logs or echoes the API key.</summary>
public sealed class LidarrClient : ILidarrApi
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    /// <summary>Initializes a new instance of the <see cref="LidarrClient"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseUrl">Lidarr base URL.</param>
    /// <param name="apiKey">Lidarr API key.</param>
    public LidarrClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        _apiKey = apiKey ?? string.Empty;
    }

    /// <summary>Gets the system status.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The status.</returns>
    public Task<LidarrStatus> GetStatusAsync(CancellationToken ct = default)
        => SendAsync<LidarrStatus>(HttpMethod.Get, "/api/v1/system/status", null, Quick, ct);

    /// <summary>Lists quality profiles.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The profiles.</returns>
    public Task<IReadOnlyList<LidarrProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrProfile>>(HttpMethod.Get, "/api/v1/qualityprofile", null, Quick, ct);

    /// <summary>Lists metadata profiles.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The profiles.</returns>
    public Task<IReadOnlyList<LidarrProfile>> GetMetadataProfilesAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrProfile>>(HttpMethod.Get, "/api/v1/metadataprofile", null, Quick, ct);

    /// <summary>Lists root folders.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folders.</returns>
    public Task<IReadOnlyList<LidarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrRootFolder>>(HttpMethod.Get, "/api/v1/rootfolder", null, Quick, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<LidarrSearchItem>> SearchAsync(string term, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrSearchItem>>(HttpMethod.Get, "/api/v1/search?term=" + Uri.EscapeDataString(term), null, Slow, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrAlbum>>(HttpMethod.Get, "/api/v1/album/lookup?term=" + Uri.EscapeDataString(term), null, Slow, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrAlbum>>(HttpMethod.Get, $"/api/v1/album?artistId={artistId}", null, Normal, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrAlbum>>(
            HttpMethod.Get, "/api/v1/album?includeAllArtistAlbums=false&foreignAlbumId=" + Uri.EscapeDataString(foreignAlbumId), null, Normal, ct);

    /// <inheritdoc />
    public Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum album, CancellationToken ct = default)
        => SendAsync<LidarrAlbum>(HttpMethod.Post, "/api/v1/album", album, Slow, ct);

    /// <inheritdoc />
    public Task MonitorAlbumsAsync(IReadOnlyList<int> ids, bool monitored, CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Put, "/api/v1/album/monitor", new { albumIds = ids, monitored }, Normal, ct, allowEmpty: true);

    /// <inheritdoc />
    public Task AlbumSearchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/command", new { name = "AlbumSearch", albumIds = ids }, Normal, ct, allowEmpty: true);

    /// <inheritdoc />
    public Task<LidarrAlbum> GetAlbumAsync(int id, CancellationToken ct = default)
        => SendAsync<LidarrAlbum>(HttpMethod.Get, $"/api/v1/album/{id}", null, Normal, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<LidarrQueueRecord>> GetQueueAsync(CancellationToken ct = default)
    {
        var page = await SendAsync<LidarrQueuePage>(HttpMethod.Get, "/api/v1/queue?includeAlbum=true&pageSize=200", null, Normal, ct).ConfigureAwait(false);
        return page.Records ?? new List<LidarrQueueRecord>();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LidarrTrackFile>> GetTrackFilesAsync(int albumId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<LidarrTrackFile>>(HttpMethod.Get, $"/api/v1/trackfile?albumId={albumId}", null, Normal, ct);

    /// <inheritdoc />
    public async Task<string?> GetAlbumFolderAsync(int albumId, CancellationToken ct = default)
        => PathMap.AlbumFolder((await GetTrackFilesAsync(albumId, ct).ConfigureAwait(false)).Select(f => f.Path));

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct, bool allowEmpty = false)
    {
        if (!Uri.TryCreate(_baseUrl + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LidarrException("URL Lidarr invalide.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", _apiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new LidarrException("Clé API refusée par Lidarr.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LidarrException($"Lidarr a répondu {(int)response.StatusCode}.");
            }

            if (allowEmpty)
            {
                return default!;
            }

            var result = await response.Content.ReadFromJsonAsync<T>(cts.Token).ConfigureAwait(false);
            return result ?? throw new LidarrException("Réponse Lidarr vide.");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LidarrException($"Lidarr ne répond pas (délai de {(int)timeout.TotalSeconds} s dépassé).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LidarrException("Lidarr injoignable : " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new LidarrException("Réponse Lidarr illisible.", ex);
        }
    }

    private sealed class LidarrQueuePage
    {
        [JsonPropertyName("records")]
        public List<LidarrQueueRecord>? Records { get; init; }
    }
}
