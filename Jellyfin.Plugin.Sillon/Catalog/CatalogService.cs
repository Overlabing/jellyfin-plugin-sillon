using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Storage;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>The album catalogue: Lidarr's lookup, cross-referenced with the requests and Jellyfin's library.</summary>
public sealed class CatalogService
{
    private const int MaxSearchAlbums = 40;
    private const int MaxTrackAlbums = 8;
    internal const string OutOfTime = "no answer in time";
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan CatalogRetry = TimeSpan.FromMinutes(5);

    // Lidarr's votes on an artist named exactly as typed, from which the text is taken to mean the artist and no song is looked for. A real artist people
    // vote on has dozens (Radiohead); DJ Snake has 1, and a song title's namesake has few, so those keep the song search: too low costs a request, too high loses nothing.
    private const int SureArtistVotes = 10;

    // what a search has in all: what is known by then is its answer. The app gives up after 20 seconds, which is far too long to look at a spinner
    private static readonly TimeSpan DefaultSearchDeadline = TimeSpan.FromSeconds(5);

    // the artist page, from its first request; what is left of the 20 seconds goes to the search by name
    private static readonly TimeSpan DefaultArtistBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NameSearchBudget = TimeSpan.FromSeconds(3);

    private readonly ILidarrProvider _lidarr;
    private readonly RequestStore _requests;
    private readonly IJellyfinLibrary _library;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<CatalogService> _logger;
    private readonly IMusicBrainzApi? _musicBrainz;
    private readonly Func<DateTimeOffset> _now;
    private readonly ITrackHints? _hints;
    private readonly TimeSpan _searchDeadline;
    private readonly TimeSpan _artistBudget;
    private readonly ArtistDiscography? _discography;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private string? _catalogKey;
    private DateTime _catalogExpires;
    private Dictionary<string, LidarrAlbum> _catalog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="CatalogService"/> class.</summary>
    /// <param name="lidarr">The Lidarr client provider.</param>
    /// <param name="requests">The request store.</param>
    /// <param name="library">The Jellyfin library.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="musicBrainz">MusicBrainz, for the search by track title; without it a search only matches artist and album names.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    /// <param name="hints">The popularity hints, only asked when the administrator turned them on.</param>
    /// <param name="searchDeadline">How long a search may take, its answer being what is known by then; 5 seconds when null.</param>
    /// <param name="discographyBudget">How long the artist page's list of albums may take, keeping what it found by then; 15 seconds when null.</param>
    /// <param name="metadata">Lidarr's metadata service, for the whole list of an artist's albums at once; MusicBrainz and a lookup per album when null, or when it does not answer.</param>
    /// <param name="browseWait">How long the metadata service's list waits for the joint albums MusicBrainz adds to it; 3 seconds when null.</param>
    public CatalogService(
        ILidarrProvider lidarr,
        RequestStore requests,
        IJellyfinLibrary library,
        Func<PluginConfiguration> config,
        ILogger<CatalogService> logger,
        IMusicBrainzApi? musicBrainz = null,
        Func<DateTimeOffset>? now = null,
        ITrackHints? hints = null,
        TimeSpan? searchDeadline = null,
        TimeSpan? discographyBudget = null,
        IArtistMetadata? metadata = null,
        TimeSpan? browseWait = null)
    {
        _artistBudget = discographyBudget ?? DefaultArtistBudget;
        _hints = hints;
        _searchDeadline = searchDeadline ?? DefaultSearchDeadline;
        _musicBrainz = musicBrainz;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _lidarr = lidarr;
        _requests = requests;
        _library = library;
        _config = config;
        _logger = logger;
        _discography = musicBrainz is null ? null : new ArtistDiscography(musicBrainz, _now, metadata, browseWait);
    }

    /// <summary>Searches artists and albums. Everything has one deadline, and what is known by then is the answer: whatever failed or ran out of time only costs its own results.</summary>
    /// <param name="query">The search text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artists and albums.</returns>
    /// <exception cref="CatalogException">The catalogue is unavailable, and nothing else could answer.</exception>
    public async Task<CatalogSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        var lidarr = RequireLidarr();
        var config = _config();
        var allowed = config.AllowedReleaseGroups();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_searchDeadline);
        var lookup = LookupAlbumsOrNoneAsync(lidarr, query, deadline.Token, ct);
        var search = SearchOrFailedAsync(lidarr, query, deadline.Token, ct);
        var trackHits = TrackAlbumsOrNoneAsync(query, config, search, deadline.Token, ct);
        var (items, failed) = await search.ConfigureAwait(false);

        var artists = new List<CatalogArtist>();
        var albums = new List<LidarrAlbum>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var limit = MaxSearchAlbums;

        // only worth asking for when the query names an artist, and never when the catalogue is limited to a list
        var named = allowed.Count == 0 ? NamedArtist(items, query) : null;
        var studio = named is null ? Task.FromResult<IReadOnlyList<LidarrAlbum>>(Array.Empty<LidarrAlbum>()) : StudioAlbumsAsync(lidarr, named, config, deadline.Token, ct);

        // the list is resolved through Lidarr as a whole and kept, whatever one search has left: without this, a long one would never get to the end of it
        var listed = allowed.Count > 0 ? await AllowedCatalogAsync(lidarr, allowed, ct).ConfigureAwait(false) : Array.Empty<LidarrAlbum>();

        void Add(LidarrAlbum album)
        {
            if (allowed.Count > 0 && !allowed.Contains(album.ForeignAlbumId))
            {
                return;
            }

            if (IsHidden(album, config, allowed))
            {
                return;
            }

            if (seen.TryGetValue(album.ForeignAlbumId, out var index))
            {
                // the combined search can omit the releases the album lookup carries
                if (albums[index].Releases is not { Count: > 0 } && album.Releases is { Count: > 0 })
                {
                    albums[index] = album;
                }
            }
            else if (albums.Count < limit)
            {
                seen[album.ForeignAlbumId] = albums.Count;
                albums.Add(album);
            }
        }

        // the albums that hold the track searched for, as MusicBrainz tells them: a request goes through Lidarr by the ids alone. An allowed album stays exactly what the list resolved
        var tracks = new Dictionary<string, TrackAlbum>(StringComparer.OrdinalIgnoreCase);
        var hits = (await trackHits.ConfigureAwait(false)).Where(h => allowed.Count == 0 || allowed.Contains(h.ReleaseGroupId)).Take(MaxTrackAlbums).ToList();
        var trackAlbums = new List<LidarrAlbum>(hits.Count);
        foreach (var hit in hits)
        {
            tracks[hit.ReleaseGroupId] = hit;
            if (allowed.Count == 0)
            {
                trackAlbums.Add(ArtistDiscography.ToAlbum(hit.Group!));
            }
            else if (listed.FirstOrDefault(a => a.ForeignAlbumId.Equals(hit.ReleaseGroupId, StringComparison.OrdinalIgnoreCase)) is { } album)
            {
                trackAlbums.Add(album);
            }
        }

        // a popular artist's album comes before the many same-title albums Lidarr knows
        foreach (var album in trackAlbums.Where(a => tracks[a.ForeignAlbumId].Hinted))
        {
            Add(album);
        }

        // on top of the usual cap: what the search finds otherwise keeps its room
        var artistAlbums = await studio.ConfigureAwait(false);
        limit += artistAlbums.Count;
        foreach (var album in artistAlbums)
        {
            Add(album);
        }

        foreach (var item in items)
        {
            if (item.Artist is { ForeignArtistId.Length: > 0 })
            {
                artists.Add(ToArtist(item.Artist));
            }
            else if (item.Album is { ForeignAlbumId.Length: > 0 })
            {
                Add(item.Album);
            }
        }

        foreach (var album in await lookup.ConfigureAwait(false))
        {
            if (album.ForeignAlbumId.Length > 0)
            {
                Add(album);
            }
        }

        foreach (var album in trackAlbums.Where(a => !tracks[a.ForeignAlbumId].Hinted))
        {
            Add(album);
        }

        if (allowed.Count > 0)
        {
            // Lidarr answers a search by artist name with the artist alone: match the cached allowed albums too
            foreach (var album in listed)
            {
                if (Matches(album.Title, query) || Matches(album.Artist?.ArtistName, query))
                {
                    Add(album);
                    if (album.Artist is { ForeignArtistId.Length: > 0 } owner && !artists.Any(a => a.Mbid == owner.ForeignArtistId))
                    {
                        artists.Add(ToArtist(owner));
                    }
                }
            }

            // an artist is offered only through an allowed album
            var offered = albums.Select(a => a.Artist?.ForeignArtistId).ToHashSet(StringComparer.Ordinal);
            artists.RemoveAll(a => !offered.Contains(a.Mbid));
        }

        // only what answers nothing at all is a failure: Lidarr being down must not hide what MusicBrainz gave
        if (failed && albums.Count == 0 && artists.Count == 0 && allowed.Count == 0)
        {
            throw new CatalogException(StatusCodes.Status502BadGateway, "Lidarr indisponible");
        }

        return new CatalogSearchResult(artists, ToAlbums(PreferFullRelease(albums), null, tracks));
    }

    /// <summary>Lists an artist's albums. Resolution, in order: Lidarr's id lookup (<c>lidarr:&lt;mbid&gt;</c>) gives the artist; if Lidarr already holds the artist, all of its albums; else the whole list of Lidarr's metadata service, or when it gives no answer the release groups MusicBrainz credits to the artist, each resolved by the same id lookup; when that is empty or incomplete, a search by the artist's name filtered to the albums of that artist. The name comes from the id lookup, or from the optional hint when that found nothing.</summary>
    /// <param name="mbid">The artist's MusicBrainz id.</param>
    /// <param name="nameHint">The artist's name, when the app knows it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artist and its albums.</returns>
    /// <exception cref="CatalogException">The catalogue is unavailable or the artist is unknown.</exception>
    public async Task<CatalogArtistResult> ArtistAlbumsAsync(string mbid, string? nameHint, CancellationToken ct)
    {
        var lidarr = RequireLidarr();
        var started = Stopwatch.GetTimestamp();
        IReadOnlyList<LidarrSearchItem> items;
        try
        {
            items = await lidarr.SearchAsync("lidarr:" + mbid, ct).ConfigureAwait(false);
        }
        catch (LidarrException ex)
        {
            _logger.LogError("Lidarr artist lookup failed: {Message}", ex.Message);
            throw new CatalogException(StatusCodes.Status502BadGateway, "Lidarr indisponible");
        }

        var config = _config();
        var allowed = config.AllowedReleaseGroups();
        if (allowed.Count > 0)
        {
            var cached = (await AllowedCatalogAsync(lidarr, allowed, ct).ConfigureAwait(false))
                .Where(a => a.Artist?.ForeignArtistId == mbid)
                .ToList();
            if (cached.Count > 0)
            {
                return new CatalogArtistResult(ToArtist(cached[0].Artist!), ToAlbums(cached, cached[0].Artist));
            }
        }

        LidarrArtist? artist = null;
        var albums = new List<LidarrAlbum>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(LidarrAlbum album)
        {
            if (allowed.Count > 0 && !allowed.Contains(album.ForeignAlbumId))
            {
                return;
            }

            if (IsHidden(album, config, allowed))
            {
                return;
            }

            if (album.ForeignAlbumId.Length > 0 && seen.Add(album.ForeignAlbumId))
            {
                albums.Add(album);
            }
        }

        foreach (var item in items)
        {
            if (item.Artist is not null && item.Artist.ForeignArtistId == mbid && artist is null)
            {
                artist = item.Artist;
            }

            if (item.Album?.Artist is not null && item.Album.Artist.ForeignArtistId == mbid)
            {
                Add(item.Album);
            }
        }

        if (artist is { Id: > 0 })
        {
            try
            {
                foreach (var album in await lidarr.GetAlbumsByArtistAsync(artist.Id, ct).ConfigureAwait(false))
                {
                    Add(album);
                }
            }
            catch (LidarrException ex)
            {
                _logger.LogWarning("Lidarr albums of the artist failed: {Message}", ex.Message);
            }
        }

        // the allow list is answered from its own albums above: nothing to ask MusicBrainz
        var listed = false;
        var complete = false;
        if (_discography is not null && allowed.Count == 0 && artist is not { Id: > 0 })
        {
            listed = true;
            var budget = _artistBudget - Stopwatch.GetElapsedTime(started);
            var answer = await _discography.AlbumsAsync(lidarr, mbid, config, ArtistDiscography.ArtistPage(budget), ct).ConfigureAwait(false);
            complete = answer.Complete;
            WarnIfDiscographyIncomplete(answer);
            foreach (var album in answer.Albums)
            {
                Add(album);
                if (artist is null && album.Artist is not null && album.Artist.ForeignArtistId == mbid)
                {
                    artist = album.Artist;
                }
            }
        }

        var name = artist?.ArtistName ?? nameHint?.Trim() ?? string.Empty;
        if ((albums.Count == 0 || (listed && !complete)) && name.Length > 0)
        {
            using var nameBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (listed)
            {
                nameBudget.CancelAfter(NameSearchBudget);
            }

            try
            {
                foreach (var item in await lidarr.SearchAsync(name, nameBudget.Token).ConfigureAwait(false))
                {
                    if (item.Album?.Artist is not null && item.Album.Artist.ForeignArtistId == mbid)
                    {
                        Add(item.Album);
                    }

                    if (artist is null && item.Artist is not null && item.Artist.ForeignArtistId == mbid)
                    {
                        artist = item.Artist;
                    }
                }
            }
            catch (LidarrException ex)
            {
                _logger.LogWarning("Lidarr search by artist name failed: {Message}", ex.Message);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Lidarr search by artist name failed: {Message}", OutOfTime);
            }
        }

        // an artist whose albums are all hidden singles still exists: only an unknown artist is a 404
        if ((artist is null || allowed.Count > 0) && albums.Count == 0)
        {
            throw new CatalogException(StatusCodes.Status404NotFound, "artiste introuvable");
        }

        return new CatalogArtistResult(artist is null ? null : ToArtist(artist), ToAlbums(albums, artist));
    }

    /// <summary>
    /// Lists the tracks of a catalogue album: the release Lidarr shows first for the album (so the count matches the album's <c>trackCount</c>), else the earliest official release
    /// MusicBrainz lists; its tracks come from MusicBrainz (<c>release?inc=recordings</c>). Works for albums not yet on the server.
    /// </summary>
    /// <param name="mbid">The album's MusicBrainz release group id.</param>
    /// <param name="track">The track title to flag as matched, when the app comes from a track search.</param>
    /// <param name="recording">The recording id to flag as matched (wins over the title).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tracklist.</returns>
    /// <exception cref="CatalogException">The catalogue is unavailable, the album is unknown or not offered.</exception>
    public async Task<CatalogTracklist> AlbumTracksAsync(string mbid, string? track, string? recording, CancellationToken ct)
    {
        var lidarr = RequireLidarr();
        if (_musicBrainz is null)
        {
            throw new CatalogException(StatusCodes.Status503ServiceUnavailable, "MusicBrainz indisponible");
        }

        var allowed = _config().AllowedReleaseGroups();
        if (allowed.Count > 0 && !allowed.Contains(mbid))
        {
            throw new CatalogException(StatusCodes.Status404NotFound, "album introuvable");
        }

        string? releaseId = null;
        try
        {
            var album = (await lidarr.LookupAlbumsAsync("lidarr:" + mbid, ct).ConfigureAwait(false))
                .FirstOrDefault(a => string.Equals(a.ForeignAlbumId, mbid, StringComparison.OrdinalIgnoreCase));
            releaseId = album?.Releases?.FirstOrDefault()?.ForeignReleaseId;
            if (album is null && allowed.Count == 0)
            {
                throw new CatalogException(StatusCodes.Status404NotFound, "album introuvable");
            }
        }
        catch (LidarrException ex)
        {
            // the tracks do not need Lidarr: MusicBrainz can pick the release itself
            _logger.LogWarning("Lidarr album lookup failed: {Message}", ex.Message);
        }

        if (string.IsNullOrEmpty(releaseId))
        {
            var releases = await _musicBrainz.GetReleasesAsync(mbid, ct).ConfigureAwait(false)
                ?? throw new CatalogException(StatusCodes.Status502BadGateway, "MusicBrainz indisponible");
            releaseId = releases
                .OrderBy(r => r.Status.Equals("Official", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(r => r.Date.Length == 0 ? "9999" : r.Date, StringComparer.Ordinal)
                .FirstOrDefault()?.Id;
        }

        if (string.IsNullOrEmpty(releaseId))
        {
            throw new CatalogException(StatusCodes.Status404NotFound, "album introuvable");
        }

        var list = await _musicBrainz.GetTracklistAsync(releaseId, ct).ConfigureAwait(false)
            ?? throw new CatalogException(StatusCodes.Status502BadGateway, "MusicBrainz indisponible");
        var byRecording = !string.IsNullOrEmpty(recording) && list.Tracks.Any(t => string.Equals(t.RecordingId, recording, StringComparison.OrdinalIgnoreCase));
        var wantedTitle = string.IsNullOrWhiteSpace(track) ? null : TrackQuery.Fold(track);
        var matchedOnce = false;
        var tracks = new List<CatalogTrack>(list.Tracks.Count);
        foreach (var t in list.Tracks)
        {
            var hit = !matchedOnce && (byRecording
                ? string.Equals(t.RecordingId, recording, StringComparison.OrdinalIgnoreCase)
                : wantedTitle is not null && TrackQuery.Fold(t.Title) == wantedTitle);
            matchedOnce |= hit;
            tracks.Add(new CatalogTrack(t.Disc, t.Position, t.Number, t.Title, t.LengthMs, t.RecordingId, hit ? true : null));
        }

        return new CatalogTracklist(mbid, list.ReleaseId, list.Title, tracks.Count == 0 ? 0 : tracks.Max(t => t.Disc), tracks);
    }

    /// <summary>Picks the URL of an image: the remote one, else Lidarr's own when absolute (it is otherwise relative, /MediaCover/..., and unreachable by the app).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The URL, or empty.</returns>
    public static string ImageUrl(LidarrImage image)
    {
        if (!string.IsNullOrEmpty(image.RemoteUrl))
        {
            return image.RemoteUrl;
        }

        return image.Url.StartsWith("http", StringComparison.Ordinal) ? image.Url : string.Empty;
    }

    /// <summary>Picks a cover: a "cover" image first, then a "remoteCover" one.</summary>
    /// <param name="images">The images.</param>
    /// <returns>The URL, or empty.</returns>
    public static string CoverOf(IEnumerable<LidarrImage>? images)
    {
        var list = images?.ToList() ?? new List<LidarrImage>();
        foreach (var wanted in new[] { "cover", "remotecover" })
        {
            foreach (var image in list)
            {
                if (string.Equals(image.CoverType, wanted, StringComparison.OrdinalIgnoreCase) && ImageUrl(image).Length > 0)
                {
                    return ImageUrl(image);
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Reads the year from the first four characters of a date.</summary>
    /// <param name="date">The date text.</param>
    /// <returns>The year, 0 when unreadable.</returns>
    public static int YearOf(string? date)
        => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) ? year : 0;

    /// <summary>
    /// Drops the EPs, singles and the like of an artist that has an album of the same title (case, diacritics and punctuation aside). Same-title albums are distinct release groups
    /// (cast recordings, soundtracks, live albums), so all stay, the most likely one first: Lidarr's order can open "Les Misérables" on a Japanese cast. They take the places the group held.
    /// </summary>
    /// <param name="albums">The albums found by a search.</param>
    /// <returns>The albums without the lesser kinds, same-title albums ordered by likelihood.</returns>
    public static IReadOnlyList<LidarrAlbum> PreferFullRelease(IReadOnlyList<LidarrAlbum> albums)
    {
        static bool IsAlbum(LidarrAlbum album) => album.AlbumType.Trim().Equals("album", StringComparison.OrdinalIgnoreCase);
        static (string Artist, string Title)? KeyOf(LidarrAlbum album)
            => album.Artist is { ForeignArtistId.Length: > 0 } artist && TrackQuery.Fold(album.Title) is { Length: > 0 } title ? (artist.ForeignArtistId, title) : null;

        var sameTitle = albums
            .Where(a => IsAlbum(a) && KeyOf(a) is not null)
            .GroupBy(a => KeyOf(a)!.Value)
            .ToDictionary(g => g.Key, g => new Queue<LidarrAlbum>(g.OrderBy(a => a.SecondaryTypes is { Count: > 0 } ? 1 : 0).ThenByDescending(a => a.Releases?.Count ?? 0).ThenBy(DateOf)));
        var result = new List<LidarrAlbum>(albums.Count);
        foreach (var album in albums)
        {
            if (KeyOf(album) is not { } key || (!IsAlbum(album) && !sameTitle.ContainsKey(key)))
            {
                result.Add(album);
            }
            else if (IsAlbum(album))
            {
                result.Add(sameTitle[key].Dequeue());
            }
        }

        return result;
    }

    private static DateTime DateOf(LidarrAlbum album)
        => album.ReleaseDate is { Length: > 0 } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTime.MaxValue;

    /// <summary>What tells an album from its same-title siblings: the release group's disambiguation, else that of its first release.</summary>
    private static string DisambiguationOf(LidarrAlbum album)
        => album.Disambiguation.Trim().Length > 0
            ? album.Disambiguation
            : album.Releases?.Select(r => r.Disambiguation.Trim()).FirstOrDefault(d => d.Length > 0) ?? string.Empty;

    private static bool Matches(string? text, string query)
        => !string.IsNullOrEmpty(text)
            && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query.Trim(), CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase) >= 0;

    /// <summary>The allowed albums, resolved once each by Lidarr's id lookup, sequentially to spare its upstream rate limit; kept for an hour, rebuilt when the list changes, a failed lookup is retried after a few minutes.</summary>
    private async Task<IReadOnlyList<LidarrAlbum>> AllowedCatalogAsync(ILidarrApi lidarr, HashSet<string> allowed, CancellationToken ct)
    {
        var key = string.Join('\n', allowed.Select(a => a.ToLowerInvariant()).Order(StringComparer.Ordinal));
        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (key == _catalogKey && DateTime.UtcNow < _catalogExpires)
            {
                return _catalog.Values.ToList();
            }

            var previous = key == _catalogKey ? _catalog : new Dictionary<string, LidarrAlbum>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, LidarrAlbum>(StringComparer.OrdinalIgnoreCase);
            var failed = false;
            foreach (var id in allowed)
            {
                try
                {
                    var found = (await lidarr.LookupAlbumsAsync("lidarr:" + id, ct).ConfigureAwait(false))
                        .FirstOrDefault(a => string.Equals(a.ForeignAlbumId, id, StringComparison.OrdinalIgnoreCase));
                    if (found is not null)
                    {
                        next[id] = found;
                    }
                    else if (previous.TryGetValue(id, out var old))
                    {
                        next[id] = old;
                    }
                }
                catch (LidarrException ex)
                {
                    failed = true;
                    _logger.LogWarning("Lidarr lookup of the allowed album {Album} failed: {Message}", id, ex.Message);
                    if (previous.TryGetValue(id, out var old))
                    {
                        next[id] = old;
                    }
                }
            }

            _catalogKey = key;
            _catalog = next;
            _catalogExpires = DateTime.UtcNow + (failed ? CatalogRetry : CatalogTtl);
            return next.Values.ToList();
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>Whether the settings hide the album: singles by default, EPs on request. An explicit allow list is never second-guessed.</summary>
    private static bool IsHidden(LidarrAlbum album, PluginConfiguration config, HashSet<string> allowed)
    {
        if (allowed.Count > 0)
        {
            return false;
        }

        var type = album.AlbumType.Trim();
        return (config.HideSingles && type.Equals("single", StringComparison.OrdinalIgnoreCase))
            || (config.HideEps && type.Equals("ep", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The albums that hold the song searched for: those of the artists the hints name when they give any, else those of the title alone, unless Lidarr shows
    /// the text is an artist's name. At most one question to MusicBrainz, two when one about the hinted artists finds nothing. Whatever goes wrong only costs the song results.
    /// </summary>
    private async Task<IReadOnlyList<TrackAlbum>> TrackAlbumsOrNoneAsync(string query, PluginConfiguration config, Task<(IReadOnlyList<LidarrSearchItem> Items, bool Failed)> search, CancellationToken deadline, CancellationToken ct)
    {
        if (_musicBrainz is null || !config.SearchTracks || TrackQuery.Parse(query) is not { } parsed)
        {
            return Array.Empty<TrackAlbum>();
        }

        try
        {
            if (_hints is not null && config.DeezerPopularityHints && parsed.Artist is null)
            {
                var hinted = await HintedTrackAlbumsAsync(_hints, _musicBrainz, parsed.Title, config, deadline, ct).ConfigureAwait(false);
                if (hinted.Count > 0)
                {
                    return hinted;
                }
            }

            var (items, _) = await search.ConfigureAwait(false);
            if (NamesASureArtist(items, query))
            {
                return Array.Empty<TrackAlbum>();
            }

            return TrackSearch.Albums(await _musicBrainz.SearchRecordingsAsync(parsed, deadline).ConfigureAwait(false), parsed, config.HideEps);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            WarnSongSearchIncomplete(OutOfTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnSongSearchIncomplete(ex is MusicBrainzUnavailableException ? ex.Message : ex.GetType().Name);
        }

        return Array.Empty<TrackAlbum>();
    }

    /// <summary>One line per search, however many requests and retries it took: the results are short, and the next search asks again.</summary>
    private void WarnSongSearchIncomplete(string reason)
        => _logger.LogWarning("Song search incomplete, no answer from MusicBrainz ({Reason}): returning the other results", reason);

    /// <summary>The albums of the artists the hints name, all asked for in one question through MusicBrainz's queue and its cache. A hint service that fails, or knows nothing, gives nothing, which leaves the plain path.</summary>
    private async Task<IReadOnlyList<TrackAlbum>> HintedTrackAlbumsAsync(ITrackHints hints, IMusicBrainzApi musicBrainz, string title, PluginConfiguration config, CancellationToken deadline, CancellationToken ct)
    {
        IReadOnlyList<string> artists;
        try
        {
            artists = await hints.ArtistsForTitleAsync(title, deadline).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Track hints failed: {Reason}", ex.GetType().Name);
            return Array.Empty<TrackAlbum>();
        }

        if (artists.Count == 0)
        {
            return Array.Empty<TrackAlbum>();
        }

        var question = new TrackQuery(title, artists[0], AnySpelling: true, OtherArtists: artists.Skip(1).ToList());
        return TrackSearch.Hinted(await musicBrainz.SearchRecordingsAsync(question, deadline).ConfigureAwait(false), question, config.HideEps);
    }

    /// <summary>Whether Lidarr's first artist is called exactly what was typed, with the votes to show it is who was meant.</summary>
    private static bool NamesASureArtist(IReadOnlyList<LidarrSearchItem> items, string query)
    {
        var top = items.Select(i => i.Artist).OfType<LidarrArtist>().FirstOrDefault(a => a.ForeignArtistId.Length > 0);
        return top is { Ratings.Votes: >= SureArtistVotes } && TrackQuery.Fold(top.ArtistName) == TrackQuery.Fold(query) && TrackQuery.Fold(query).Length > 0;
    }

    /// <summary>
    /// The artist a query names: Lidarr's first artist must be called exactly that (case, diacritics and punctuation aside), or the query is taken for something else.
    /// Among namesakes, the one with the most votes, then the one Lidarr holds, then the first listed. Nobody when that one has no votes and Lidarr does not hold it: nothing says it is who was meant.
    /// </summary>
    private static LidarrArtist? NamedArtist(IReadOnlyList<LidarrSearchItem> items, string query)
    {
        var wanted = TrackQuery.Fold(query);
        var artists = items.Select(i => i.Artist).OfType<LidarrArtist>().Where(a => a.ForeignArtistId.Length > 0).ToList();
        if (wanted.Length == 0 || artists.Count == 0 || TrackQuery.Fold(artists[0].ArtistName) != wanted)
        {
            return null;
        }

        var named = artists
            .Where(a => TrackQuery.Fold(a.ArtistName) == wanted)
            .OrderByDescending(a => a.Ratings?.Votes ?? 0)
            .ThenByDescending(a => a.Id > 0)
            .First();
        return named.Id > 0 || (named.Ratings?.Votes ?? 0) > 0 ? named : null;
    }

    /// <summary>The studio albums of the artist a search names, from Lidarr when it holds the artist, else from the metadata service's one answer; nothing, rather than a failure, when they cannot be had.</summary>
    private async Task<IReadOnlyList<LidarrAlbum>> StudioAlbumsAsync(ILidarrApi lidarr, LidarrArtist artist, PluginConfiguration config, CancellationToken deadline, CancellationToken ct)
    {
        try
        {
            if (artist.Id > 0)
            {
                return ArtistDiscography.BestStudioAlbums(await lidarr.GetAlbumsByArtistAsync(artist.Id, deadline).ConfigureAwait(false));
            }

            return _discography is null ? Array.Empty<LidarrAlbum>() : await _discography.ListedStudioAlbumsAsync(artist.ForeignArtistId, config, deadline).ConfigureAwait(false);
        }
        catch (LidarrException ex)
        {
            _logger.LogWarning("Lidarr albums of the artist failed: {Message}", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Artist albums incomplete: {Reason}", OutOfTime);
        }

        return Array.Empty<LidarrAlbum>();
    }

    private void WarnIfDiscographyIncomplete(DiscographyAnswer answer)
    {
        if (answer.Failure is not null)
        {
            _logger.LogWarning("Artist albums incomplete, {Found} found ({Reason}): returning those", answer.Albums.Count, answer.Failure);
        }
    }

    private async Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsOrNoneAsync(ILidarrApi lidarr, string query, CancellationToken deadline, CancellationToken ct)
    {
        try
        {
            return await lidarr.LookupAlbumsAsync(query, deadline).ConfigureAwait(false);
        }
        catch (LidarrException ex)
        {
            _logger.LogWarning("Lidarr album lookup failed: {Message}", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Lidarr album lookup failed: {Message}", OutOfTime);
        }

        return Array.Empty<LidarrAlbum>();
    }

    private async Task<(IReadOnlyList<LidarrSearchItem> Items, bool Failed)> SearchOrFailedAsync(ILidarrApi lidarr, string query, CancellationToken deadline, CancellationToken ct)
    {
        try
        {
            return (await lidarr.SearchAsync(query, deadline).ConfigureAwait(false), false);
        }
        catch (LidarrException ex)
        {
            _logger.LogError("Lidarr search failed: {Message}", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("Lidarr search failed: {Message}", OutOfTime);
        }

        return (Array.Empty<LidarrSearchItem>(), true);
    }

    private static CatalogArtist ToArtist(LidarrArtist artist)
        => new(
            artist.ForeignArtistId,
            artist.ArtistName,
            artist.Disambiguation,
            (artist.Images ?? new List<LidarrImage>())
                .Where(i => ImageUrl(i).Length > 0)
                .Select(i => new CatalogImage(i.CoverType, ImageUrl(i)))
                .ToList(),
            artist.ArtistType,
            artist.Genres ?? new List<string>());

    /// <summary>Which albums the library holds, asked at once: a discography can run to hundreds.</summary>
    private IReadOnlySet<string> InLibrary(IReadOnlyList<AlbumKey> albums)
    {
        if (albums.Count == 0)
        {
            return new HashSet<string>();
        }

        try
        {
            return _library.FindAlbums(albums);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Library album lookup failed: {Message}", ex.Message);
            return new HashSet<string>();
        }
    }

    private ILidarrApi RequireLidarr()
    {
        var config = _config();
        if (config.FulfillmentMode == FulfillmentMode.Manual)
        {
            throw new CatalogException(
                StatusCodes.Status503ServiceUnavailable,
                "La liste d'envies est indisponible en mode manuel : la recherche dans le catalogue exige le mode Lidarr, choisissez le mode Lidarr dans la configuration du plugin.");
        }

        return _lidarr.Create();
    }

    /// <summary>
    /// The discs of most editions, the fewer when tied. Not the most of any edition, and not those of the one Lidarr monitors, which tends to be the
    /// largest (a 6-disc box for OK Computer): a one-disc studio album with deluxe editions is not a box set, which is what the apps take a count above one for.
    /// </summary>
    private static int DiscCountOf(LidarrAlbum album)
    {
        if (album.Releases is not { Count: > 0 } releases)
        {
            return 1;
        }

        return Math.Max(1, releases.GroupBy(r => r.MediumCount).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key);
    }

    private IReadOnlyList<CatalogAlbum> ToAlbums(IReadOnlyList<LidarrAlbum> albums, LidarrArtist? owner, IReadOnlyDictionary<string, TrackAlbum>? tracks = null)
    {
        var notFoundAfter = TimeSpan.FromDays(Math.Max(1, _config().NotFoundYetAfterDays));
        var requests = _requests.Newest(albums.Select(a => a.ForeignAlbumId));
        var inLibrary = InLibrary(albums
            .Where(a => !requests.ContainsKey(a.ForeignAlbumId) && a.Statistics is not { PercentOfTracks: >= 100 })
            .Select(a => new AlbumKey(a.ForeignAlbumId, a.Title, (a.Artist ?? owner)?.ArtistName ?? string.Empty))
            .ToList());
        var result = new List<CatalogAlbum>(albums.Count);
        foreach (var album in albums)
        {
            var artist = album.Artist ?? owner;
            var title = album.Title;
            var artistName = artist?.ArtistName ?? string.Empty;
            string state;
            long? requestId = null;
            string? phase = null;
            if (requests.TryGetValue(album.ForeignAlbumId, out var request))
            {
                (state, requestId) = (request.Status, request.Id);
                phase = request.PhaseAt(_now(), notFoundAfter);
            }
            else if (album.Statistics is { PercentOfTracks: >= 100 } || inLibrary.Contains(album.ForeignAlbumId))
            {
                state = "library";
            }
            else
            {
                state = "absent";
            }

            result.Add(new CatalogAlbum(
                album.ForeignAlbumId,
                artist?.ForeignArtistId ?? string.Empty,
                title,
                artistName,
                YearOf(album.ReleaseDate),
                album.AlbumType,
                CoverOf(album.Images),
                state,
                requestId,
                album.AlbumType,
                album.SecondaryTypes ?? new List<string>(),
                DiscCountOf(album),
                album.Releases is { Count: > 0 } ? album.Releases[0].TrackCount : 0,
                album.Releases is { Count: > 0 } && album.Releases[0].Label is { Count: > 0 } labels ? labels[0] : null,
                album.ReleaseDate,
                DisambiguationOf(album),
                tracks is not null && tracks.TryGetValue(album.ForeignAlbumId, out var track)
                    ? new CatalogTrackMatch(track.Track, track.RecordingId, track.Disc > 0 ? track.Disc : null, track.Position > 0 ? track.Position : null)
                    : null,
                phase,
                album.Releases is { Count: > 0 } && album.Releases[0].Country is { Count: > 0 } countries ? countries[0] : null));
        }

        return result;
    }
}
