using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Storage;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>The album catalogue: Lidarr's lookup, cross-referenced with the requests and Jellyfin's library.</summary>
public sealed class CatalogService
{
    private const int MaxSearchAlbums = 40;
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan CatalogRetry = TimeSpan.FromMinutes(5);

    private readonly ILidarrProvider _lidarr;
    private readonly RequestStore _requests;
    private readonly IJellyfinLibrary _library;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<CatalogService> _logger;
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
    public CatalogService(
        ILidarrProvider lidarr, RequestStore requests, IJellyfinLibrary library, Func<PluginConfiguration> config, ILogger<CatalogService> logger)
    {
        _lidarr = lidarr;
        _requests = requests;
        _library = library;
        _config = config;
        _logger = logger;
    }

    /// <summary>Searches artists and albums.</summary>
    /// <param name="query">The search text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artists and albums.</returns>
    /// <exception cref="CatalogException">The catalogue is unavailable.</exception>
    public async Task<CatalogSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        var lidarr = RequireLidarr();
        var allowed = _config().AllowedReleaseGroups();
        var lookup = LookupAlbumsOrNoneAsync(lidarr, query, ct);
        IReadOnlyList<LidarrSearchItem> items;
        try
        {
            items = await lidarr.SearchAsync(query, ct).ConfigureAwait(false);
        }
        catch (LidarrException ex)
        {
            _logger.LogError("Lidarr search failed: {Message}", ex.Message);
            await lookup.ConfigureAwait(false);

            // the cached allow-list catalogue can still answer
            if (allowed.Count == 0)
            {
                throw new CatalogException(StatusCodes.Status502BadGateway, "Lidarr indisponible");
            }

            items = Array.Empty<LidarrSearchItem>();
            lookup = Task.FromResult<IReadOnlyList<LidarrAlbum>>(Array.Empty<LidarrAlbum>());
        }

        var artists = new List<CatalogArtist>();
        var albums = new List<LidarrAlbum>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(LidarrAlbum album)
        {
            if (allowed.Count > 0 && !allowed.Contains(album.ForeignAlbumId))
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
            else if (albums.Count < MaxSearchAlbums)
            {
                seen[album.ForeignAlbumId] = albums.Count;
                albums.Add(album);
            }
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

        if (allowed.Count > 0)
        {
            // Lidarr answers a search by artist name with the artist alone: match the cached allowed albums too
            foreach (var album in await AllowedCatalogAsync(lidarr, allowed, ct).ConfigureAwait(false))
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

        return new CatalogSearchResult(artists, ToAlbums(albums, null));
    }

    /// <summary>Lists an artist's albums. Resolution, in order: Lidarr's id lookup (<c>lidarr:&lt;mbid&gt;</c>) gives the artist; if Lidarr already holds the artist, all of its albums; else (or when that is empty) a search by the artist's name filtered to the albums of that artist. The name comes from the id lookup, or from the optional hint when that found nothing.</summary>
    /// <param name="mbid">The artist's MusicBrainz id.</param>
    /// <param name="nameHint">The artist's name, when the app knows it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artist and its albums.</returns>
    /// <exception cref="CatalogException">The catalogue is unavailable or the artist is unknown.</exception>
    public async Task<CatalogArtistResult> ArtistAlbumsAsync(string mbid, string? nameHint, CancellationToken ct)
    {
        var lidarr = RequireLidarr();
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

        var allowed = _config().AllowedReleaseGroups();
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

        var name = artist?.ArtistName ?? nameHint?.Trim() ?? string.Empty;
        if (albums.Count == 0 && name.Length > 0)
        {
            try
            {
                foreach (var item in await lidarr.SearchAsync(name, ct).ConfigureAwait(false))
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
        }

        if ((artist is null || allowed.Count > 0) && albums.Count == 0)
        {
            throw new CatalogException(StatusCodes.Status404NotFound, "artiste introuvable");
        }

        return new CatalogArtistResult(artist is null ? null : ToArtist(artist), ToAlbums(albums, artist));
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

    private async Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsOrNoneAsync(ILidarrApi lidarr, string query, CancellationToken ct)
    {
        try
        {
            return await lidarr.LookupAlbumsAsync(query, ct).ConfigureAwait(false);
        }
        catch (LidarrException ex)
        {
            _logger.LogWarning("Lidarr album lookup failed: {Message}", ex.Message);
            return Array.Empty<LidarrAlbum>();
        }
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

    private bool InLibrary(string foreignAlbumId, string title, string artist)
    {
        try
        {
            return _library.FindAlbum(foreignAlbumId, title, artist) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Library album lookup failed: {Message}", ex.Message);
            return false;
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

    private IReadOnlyList<CatalogAlbum> ToAlbums(IReadOnlyList<LidarrAlbum> albums, LidarrArtist? owner)
    {
        var requests = _requests.Newest(albums.Select(a => a.ForeignAlbumId));
        var result = new List<CatalogAlbum>(albums.Count);
        foreach (var album in albums)
        {
            var artist = album.Artist ?? owner;
            var title = album.Title;
            var artistName = artist?.ArtistName ?? string.Empty;
            string state;
            long? requestId = null;
            if (requests.TryGetValue(album.ForeignAlbumId, out var request))
            {
                (state, requestId) = (request.Status, request.Id);
            }
            else if (album.Statistics is { PercentOfTracks: >= 100 } || InLibrary(album.ForeignAlbumId, title, artistName))
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
                album.Releases is { Count: > 0 } releases ? Math.Max(1, releases.Max(r => r.MediumCount)) : 1,
                album.Releases is { Count: > 0 } ? album.Releases[0].TrackCount : 0,
                album.Releases is { Count: > 0 } && album.Releases[0].Label is { Count: > 0 } labels ? labels[0] : null,
                album.ReleaseDate,
                album.Disambiguation));
        }

        return result;
    }
}
