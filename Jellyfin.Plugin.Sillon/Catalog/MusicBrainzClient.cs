using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>A release group a recording appears on.</summary>
/// <param name="Id">The release group's MusicBrainz id.</param>
/// <param name="Title">The title.</param>
/// <param name="PrimaryType">Album, Single, EP, ... (empty when unknown).</param>
/// <param name="SecondaryTypes">Compilation, Live, ...</param>
/// <param name="Year">The earliest release year seen, 0 when unknown.</param>
/// <param name="Disc">The disc the track is on in the first release listed, 0 when unknown.</param>
/// <param name="Position">The track's position on that disc, 0 when unknown.</param>
public sealed record MbReleaseGroup(string Id, string Title, string PrimaryType, IReadOnlyList<string> SecondaryTypes, int Year, int Disc = 0, int Position = 0);

/// <summary>A recording found by a track search.</summary>
/// <param name="Title">The track title.</param>
/// <param name="Artist">The credited artist, as a single string.</param>
/// <param name="Score">MusicBrainz's relevance score, 0 to 100.</param>
/// <param name="ReleaseGroups">The release groups it appears on.</param>
/// <param name="Id">The recording's MusicBrainz id.</param>
public sealed record MbRecording(string Title, string Artist, int Score, IReadOnlyList<MbReleaseGroup> ReleaseGroups, string Id = "");

/// <summary>One release (edition) of a release group.</summary>
/// <param name="Id">The release's MusicBrainz id.</param>
/// <param name="Status">Official, Promotion, Bootleg, ... (empty when unknown).</param>
/// <param name="Date">The release date as MusicBrainz gives it, empty when unknown.</param>
public sealed record MbRelease(string Id, string Status, string Date);

/// <summary>A track of a release.</summary>
/// <param name="Disc">The disc, from 1.</param>
/// <param name="Position">The position on the disc, from 1.</param>
/// <param name="Number">The number as printed (<c>A1</c>, <c>1</c>), empty when unknown.</param>
/// <param name="Title">The title.</param>
/// <param name="LengthMs">The length in milliseconds, null when unknown.</param>
/// <param name="RecordingId">The recording's MusicBrainz id, empty when unknown.</param>
public sealed record MbTrack(int Disc, int Position, string Number, string Title, int? LengthMs, string RecordingId);

/// <summary>The tracks of a release.</summary>
/// <param name="ReleaseId">The release's MusicBrainz id.</param>
/// <param name="Title">The release title.</param>
/// <param name="Tracks">The tracks, disc by disc.</param>
public sealed record MbTracklist(string ReleaseId, string Title, IReadOnlyList<MbTrack> Tracks);

/// <summary>One page of an artist's release groups.</summary>
/// <param name="Groups">The release groups of this page, in no particular order.</param>
/// <param name="Total">How many the artist has in all.</param>
public sealed record MbReleaseGroupPage(IReadOnlyList<MbReleaseGroup> Groups, int Total);

/// <summary>MusicBrainz gave no answer, which says nothing about whether the thing asked for exists.</summary>
public sealed class MusicBrainzUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="MusicBrainzUnavailableException"/> class.</summary>
    /// <param name="message">What happened; holds no search text.</param>
    /// <param name="busy">Whether MusicBrainz asked to slow down.</param>
    /// <param name="retryAfter">How long it asked to wait, when it said.</param>
    public MusicBrainzUnavailableException(string message, bool busy = false, TimeSpan? retryAfter = null)
        : base(message)
    {
        Busy = busy;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets a value indicating whether MusicBrainz asked to slow down, so more requests right now would not help.</summary>
    public bool Busy { get; }

    /// <summary>Gets how long MusicBrainz asked to wait, null when it did not say.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>What the catalogue needs of MusicBrainz.</summary>
public interface IMusicBrainzApi
{
    /// <summary>Searches recordings.</summary>
    /// <param name="query">The track query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recordings, best first; empty only when MusicBrainz answered that there are none.</returns>
    /// <exception cref="MusicBrainzUnavailableException">MusicBrainz could not answer.</exception>
    Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct);

    /// <summary>Lists the releases of a release group.</summary>
    /// <param name="releaseGroupId">The release group's MusicBrainz id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The releases; null when MusicBrainz cannot answer.</returns>
    Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct);

    /// <summary>Reads the tracks of a release.</summary>
    /// <param name="releaseId">The release's MusicBrainz id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tracklist; null when MusicBrainz cannot answer.</returns>
    Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct);

    /// <summary>Lists one page of the release groups credited to an artist.</summary>
    /// <param name="artistId">The artist's MusicBrainz id.</param>
    /// <param name="types">The primary types wanted, in lower case (<c>album</c>, <c>ep</c>, <c>single</c>).</param>
    /// <param name="offset">How many release groups to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page.</returns>
    /// <exception cref="MusicBrainzUnavailableException">MusicBrainz could not answer.</exception>
    Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct);
}

/// <summary>
/// MusicBrainz's web service, used politely: at most one request a second, an identifying User-Agent, a short timeout,
/// and answers kept for a day so a repeated question never reaches it twice. A 503 is its way of declining a request, and it also sheds load
/// across all clients however slowly this one goes (<c>X-RateLimit-Who: search-shed</c> or <c>global</c>, with <c>Retry-After: 0</c>): it is tried
/// again a little later, or as told by a <c>Retry-After</c>, then every request pauses briefly. A 503 is never remembered against the question;
/// any other failure is, for seconds.
/// </summary>
public sealed class MusicBrainzClient : IMusicBrainzApi
{
    /// <summary>The most release groups MusicBrainz gives per page of a browse.</summary>
    public const int BrowsePageSize = 100;

    private const int MaxCached = 500;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const int MaxAttempts = 3;
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan BusyPause = TimeSpan.FromSeconds(3);

    // a longer Retry-After is not waited for inside a search, whose hints have 6 seconds
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _baseUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expires, object? Value)> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="MusicBrainzClient"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="version">The plugin version, for the User-Agent.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    /// <param name="delay">Waits between requests; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    /// <param name="baseUrl">The service root; MusicBrainz when null.</param>
    public MusicBrainzClient(
        HttpClient http,
        string version,
        ILogger logger,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? baseUrl = null)
    {
        _http = http;
        _userAgent = $"Sillon-Plugin/{version} ( https://github.com/Overlabing/jellyfin-plugin-sillon )";
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _baseUrl = (baseUrl ?? "https://musicbrainz.org").TrimEnd('/');
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct)
    {
        var lucene = query.ToLucene();

        // a hinted artist's recordings are what is wanted, and its studio album can rank past the first compilations
        var limit = query.AnySpelling ? 100 : 25;
        var url = $"/ws/2/recording?fmt=json&limit={limit}&query={Uri.EscapeDataString(lucene)}";
        return CallAsync("search:" + TrackQuery.Fold(lucene), url, ParseSearch, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct)
        => OrNullAsync(CallAsync("rg:" + releaseGroupId.ToLowerInvariant(), $"/ws/2/release-group/{Uri.EscapeDataString(releaseGroupId)}?fmt=json&inc=releases", ParseReleases, ct));

    /// <inheritdoc />
    public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct)
        => OrNullAsync(CallAsync("release:" + releaseId.ToLowerInvariant(), $"/ws/2/release/{Uri.EscapeDataString(releaseId)}?fmt=json&inc=recordings", ParseTracklist, ct));

    /// <inheritdoc />
    public Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
    {
        var wanted = string.Join('|', types);
        var url = $"/ws/2/release-group?fmt=json&artist={Uri.EscapeDataString(artistId)}&type={Uri.EscapeDataString(wanted)}&limit={BrowsePageSize}&offset={offset}";
        return CallAsync($"browse:{artistId.ToLowerInvariant()}:{wanted}:{offset}", url, ParseBrowse, ct);
    }

    private async Task<T?> OrNullAsync<T>(Task<T> call)
        where T : class
    {
        try
        {
            return await call.ConfigureAwait(false);
        }
        catch (MusicBrainzUnavailableException ex)
        {
            _logger.LogWarning("MusicBrainz request failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Asks MusicBrainz once per key and day, one request at a time, no more than one a second, trying again when it declines.</summary>
    /// <exception cref="MusicBrainzUnavailableException">It gave no answer.</exception>
    private async Task<T> CallAsync<T>(string key, string path, Func<string, T> parse, CancellationToken ct)
        where T : class
    {
        if (TryCached(key, out var cached))
        {
            return Known<T>(cached);
        }

        // the gate stays held across a retry: nothing else may reach MusicBrainz meanwhile anyway
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // another caller may have filled the cache while this one waited
            if (TryCached(key, out cached))
            {
                return Known<T>(cached);
            }

            var paused = _pausedUntil - _now();
            if (paused > MaxRetryWait)
            {
                throw new MusicBrainzUnavailableException("MusicBrainz asked to slow down", busy: true);
            }

            if (paused > TimeSpan.Zero)
            {
                await _delay(paused, ct).ConfigureAwait(false);
            }

            for (var attempt = 1; ; attempt++)
            {
                var wait = _lastCall + MinGap - _now();
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, ct).ConfigureAwait(false);
                }

                _lastCall = _now();
                try
                {
                    var value = parse(await FetchAsync(path, ct).ConfigureAwait(false));
                    Remember(key, Ttl, value);
                    return value;
                }
                catch (MusicBrainzUnavailableException declined) when (declined.Busy)
                {
                    var asked = declined.RetryAfter ?? TimeSpan.Zero;
                    if (attempt == MaxAttempts || asked > MaxRetryWait)
                    {
                        _pausedUntil = _now() + (asked > BusyPause ? (asked < MaxPause ? asked : MaxPause) : BusyPause);
                        throw;
                    }

                    var backoff = (FirstBackoff * attempt) + TimeSpan.FromMilliseconds(Random.Shared.Next(250));
                    var pause = asked > backoff ? asked : backoff;

                    // still owed if this caller runs out of time during the wait
                    _pausedUntil = _now() + pause;
                    _logger.LogDebug("MusicBrainz declined attempt {Attempt}, trying again", attempt);
                    await _delay(pause, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    Remember(key, FailureTtl, null);
                    throw new MusicBrainzUnavailableException(ex is OperationCanceledException ? "MusicBrainz timed out" : ex.Message);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static T Known<T>(object? cached)
        where T : class
        => (T?)cached ?? throw new MusicBrainzUnavailableException("MusicBrainz failed moments ago");

    private void Remember(string key, TimeSpan ttl, object? value)
    {
        lock (_cache)
        {
            if (_cache.Count >= MaxCached)
            {
                _cache.Clear();
            }

            _cache[key] = (_now() + ttl, value);
        }
    }

    private bool TryCached(string key, out object? value)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Expires > _now())
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private async Task<string> FetchAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new MusicBrainzUnavailableException($"MusicBrainz answered 503{Declined(response)}", busy: true, retryAfter: RetryAfter(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MusicBrainz answered {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Which of its limits declined the request, as it names it; tells a load shed from this server's own rate.</summary>
    private static string Declined(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-RateLimit-Who", out var who)
            && who.FirstOrDefault() is { Length: > 0 and <= 40 } name
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                ? $" ({name})"
                : string.Empty;

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - _now() : null);
        return wait > TimeSpan.Zero ? wait : null;
    }

    private static IReadOnlyList<MbRecording> ParseSearch(string body)
    {
        var parsed = JsonSerializer.Deserialize<MbSearch>(body, Json);
        var list = new List<MbRecording>();
        foreach (var r in parsed?.Recordings ?? new List<MbRaw>())
        {
            var groups = new List<MbReleaseGroup>();
            foreach (var release in r.Releases ?? new List<MbRawRelease>())
            {
                if (release.ReleaseGroup is not { Id.Length: > 0 } g)
                {
                    continue;
                }

                var year = release.Date is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), out var y) ? y : 0;
                var medium = release.Media?.FirstOrDefault(m => m.Track is { Count: > 0 });
                var number = medium?.Track?[0].Number;
                var position = int.TryParse(number, out var n) ? n : 0;
                groups.Add(new MbReleaseGroup(
                    g.Id, g.Title ?? release.Title ?? string.Empty, g.PrimaryType ?? string.Empty, g.SecondaryTypes ?? new List<string>(), year,
                    position > 0 ? Math.Max(1, medium!.Position) : 0, position));
            }

            list.Add(new MbRecording(
                r.Title ?? string.Empty,
                string.Concat((r.ArtistCredit ?? new List<MbCredit>()).Select(c => (c.Name ?? c.Artist?.Name ?? string.Empty) + (c.JoinPhrase ?? string.Empty))),
                r.Score,
                groups,
                r.Id ?? string.Empty));
        }

        return list;
    }

    private static IReadOnlyList<MbRelease> ParseReleases(string body)
        => (JsonSerializer.Deserialize<MbGroupBody>(body, Json)?.Releases ?? new List<MbRawRelease>())
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .Select(r => new MbRelease(r.Id!, r.Status ?? string.Empty, r.Date ?? string.Empty))
            .ToList();

    private static MbReleaseGroupPage ParseBrowse(string body)
    {
        var parsed = JsonSerializer.Deserialize<MbBrowse>(body, Json) ?? throw new JsonException("empty browse");
        var groups = (parsed.ReleaseGroups ?? new List<MbRawGroup>())
            .Where(g => g.Id.Length > 0)
            .Select(g => new MbReleaseGroup(
                g.Id,
                g.Title ?? string.Empty,
                g.PrimaryType ?? string.Empty,
                g.SecondaryTypes ?? new List<string>(),
                g.FirstReleaseDate is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), out var year) ? year : 0))
            .ToList();
        return new MbReleaseGroupPage(groups, parsed.Count);
    }

    private static MbTracklist ParseTracklist(string body)
    {
        var release = JsonSerializer.Deserialize<MbRawRelease>(body, Json) ?? throw new JsonException("empty release");
        var tracks = new List<MbTrack>();
        var disc = 0;
        foreach (var medium in release.Media ?? new List<MbRawMedium>())
        {
            disc = medium.Position > 0 ? medium.Position : disc + 1;
            var index = 0;
            foreach (var t in medium.Tracks ?? new List<MbRawTrack>())
            {
                index++;
                var length = t.Length ?? t.Recording?.Length;
                tracks.Add(new MbTrack(
                    disc,
                    t.Position > 0 ? t.Position : index,
                    t.Number ?? string.Empty,
                    t.Title ?? t.Recording?.Title ?? string.Empty,
                    length is > 0 ? length : null,
                    t.Recording?.Id ?? string.Empty));
            }
        }

        return new MbTracklist(release.Id ?? string.Empty, release.Title ?? string.Empty, tracks);
    }

    private sealed class MbSearch
    {
        [JsonPropertyName("recordings")]
        public List<MbRaw>? Recordings { get; init; }
    }

    private sealed class MbBrowse
    {
        [JsonPropertyName("release-group-count")]
        public int Count { get; init; }

        [JsonPropertyName("release-groups")]
        public List<MbRawGroup>? ReleaseGroups { get; init; }
    }

    private sealed class MbGroupBody
    {
        [JsonPropertyName("releases")]
        public List<MbRawRelease>? Releases { get; init; }
    }

    private sealed class MbRaw
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("score")]
        public int Score { get; init; }

        [JsonPropertyName("artist-credit")]
        public List<MbCredit>? ArtistCredit { get; init; }

        [JsonPropertyName("releases")]
        public List<MbRawRelease>? Releases { get; init; }
    }

    private sealed class MbCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("joinphrase")]
        public string? JoinPhrase { get; init; }

        [JsonPropertyName("artist")]
        public MbArtist? Artist { get; init; }
    }

    private sealed class MbArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class MbRawRelease
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("media")]
        public List<MbRawMedium>? Media { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("date")]
        public string? Date { get; init; }

        [JsonPropertyName("release-group")]
        public MbRawGroup? ReleaseGroup { get; init; }
    }

    private sealed class MbRawGroup
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("primary-type")]
        public string? PrimaryType { get; init; }

        [JsonPropertyName("secondary-types")]
        public List<string>? SecondaryTypes { get; init; }

        [JsonPropertyName("first-release-date")]
        public string? FirstReleaseDate { get; init; }
    }

    private sealed class MbRawMedium
    {
        [JsonPropertyName("position")]
        public int Position { get; init; }

        // in a search hit: the matching track only, with its number
        [JsonPropertyName("track")]
        public List<MbRawTrack>? Track { get; init; }

        // in a release lookup: every track
        [JsonPropertyName("tracks")]
        public List<MbRawTrack>? Tracks { get; init; }
    }

    private sealed class MbRawTrack
    {
        [JsonPropertyName("position")]
        public int Position { get; init; }

        [JsonPropertyName("number")]
        public string? Number { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("length")]
        public int? Length { get; init; }

        [JsonPropertyName("recording")]
        public MbRawRecording? Recording { get; init; }
    }

    private sealed class MbRawRecording
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("length")]
        public int? Length { get; init; }
    }
}
