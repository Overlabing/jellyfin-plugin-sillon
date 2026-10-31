using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>Tells which artists a bare track title most likely means, so the catalogue can look those up first.</summary>
public interface ITrackHints
{
    /// <summary>Lists the artists best known for a track title.</summary>
    /// <param name="title">The title as typed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artists, most popular first; empty when the provider cannot tell.</returns>
    Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct);
}

/// <summary>
/// Deezer's public search, keyless and used sparingly: a short timeout, no cookie and nothing about the user or the server in the request,
/// answers kept for days. Any failure answers "no hint" and pauses the calls for a few minutes, logged once per outage.
/// </summary>
public sealed class DeezerHints : ITrackHints
{
    private const int MaxArtists = 3;
    private const int MaxCached = 1000;
    private const long MaxResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _baseUrl;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, IReadOnlyList<string> Artists)> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private bool _down;

    /// <summary>Initializes a new instance of the <see cref="DeezerHints"/> class.</summary>
    /// <param name="http">The HTTP client (see <see cref="CreateHttpClient"/>).</param>
    /// <param name="version">The plugin version, for the User-Agent.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    /// <param name="baseUrl">The service root; Deezer when null.</param>
    public DeezerHints(HttpClient http, string version, ILogger logger, Func<DateTimeOffset>? now = null, string? baseUrl = null)
    {
        _http = http;
        _userAgent = $"Sillon-Plugin/{version}";
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _baseUrl = (baseUrl ?? "https://api.deezer.com").TrimEnd('/');
    }

    /// <summary>Makes the handler for the client: Deezer sets tracking cookies, and Jellyfin's shared client would store and send them back.</summary>
    /// <returns>A handler that keeps no cookie and follows no redirect.</returns>
    public static SocketsHttpHandler CreateHandler()
        => new() { UseCookies = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>Makes the HTTP client this class needs.</summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateHttpClient()
        => new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct)
    {
        var key = TrackQuery.Fold(title);
        if (key.Length == 0)
        {
            return Array.Empty<string>();
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Expires > _now())
            {
                return entry.Artists;
            }

            if (_now() < _pausedUntil)
            {
                return Array.Empty<string>();
            }
        }

        IReadOnlyList<string> artists;
        try
        {
            artists = ParseArtists(await FetchAsync(title, ct).ConfigureAwait(false), key);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            NoteFailure(ex);
            return Array.Empty<string>();
        }

        lock (_lock)
        {
            _down = false;
            if (_cache.Count >= MaxCached)
            {
                _cache.Clear();
            }

            _cache[key] = (_now() + Ttl, artists);
        }

        if (artists.Count == 0)
        {
            _logger.LogDebug("Deezer gave no hint for a track title");
        }

        return artists;
    }

    private void NoteFailure(Exception ex)
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
            // the message of these exceptions holds no search text
            _logger.LogInformation("Deezer hints are unavailable, searching without them: {Reason}", ex.GetType().Name);
        }
    }

    private async Task<string> FetchAsync(string title, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/search?q={Uri.EscapeDataString(title.Trim())}&limit=5");
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Deezer answered {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The distinct artists, in Deezer's order, of the tracks titled exactly as asked. Deezer reports errors such as its quota with a 200 and no <c>data</c>.</summary>
    private static IReadOnlyList<string> ParseArtists(string body, string foldedTitle)
    {
        var tracks = JsonSerializer.Deserialize<DeezerSearch>(body, Json)?.Data
            ?? throw new JsonException("no data in the answer");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var artists = new List<string>(MaxArtists);
        foreach (var track in tracks)
        {
            if (TrackQuery.Fold(track.Title) != foldedTitle || track.Artist?.Name is not { } name)
            {
                continue;
            }

            var tight = TrackQuery.FoldTight(name);
            if (tight.Length > 0 && seen.Add(tight))
            {
                artists.Add(name.Trim());
                if (artists.Count == MaxArtists)
                {
                    break;
                }
            }
        }

        return artists;
    }

    private sealed class DeezerSearch
    {
        [JsonPropertyName("data")]
        public List<DeezerTrack>? Data { get; init; }
    }

    private sealed class DeezerTrack
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("artist")]
        public DeezerArtist? Artist { get; init; }
    }

    private sealed class DeezerArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
