using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>The album catalogue: Lidarr's lookup, cross-referenced with the requests and Jellyfin's library.</summary>
public sealed class CatalogService
{
    private readonly ILidarrProvider _lidarr;
    private readonly RequestStore _requests;
    private readonly IJellyfinLibrary _library;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<CatalogService> _logger;

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
        IReadOnlyList<LidarrSearchItem> items;
        try
        {
            items = await lidarr.SearchAsync(query, ct).ConfigureAwait(false);
        }
        catch (LidarrException ex)
        {
            _logger.LogError("Lidarr search failed: {Message}", ex.Message);
            throw new CatalogException(StatusCodes.Status502BadGateway, "Lidarr indisponible");
        }

        var artists = new List<CatalogArtist>();
        var albums = new List<LidarrAlbum>();
        foreach (var item in items)
        {
            if (item.Artist is { ForeignArtistId.Length: > 0 })
            {
                artists.Add(ToArtist(item.Artist));
            }
            else if (item.Album is { ForeignAlbumId.Length: > 0 })
            {
                albums.Add(item.Album);
            }
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

        LidarrArtist? artist = null;
        var albums = new List<LidarrAlbum>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(LidarrAlbum album)
        {
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

        if (artist is null && albums.Count == 0)
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

    private static CatalogArtist ToArtist(LidarrArtist artist)
        => new(
            artist.ForeignArtistId,
            artist.ArtistName,
            artist.Disambiguation,
            (artist.Images ?? new List<LidarrImage>())
                .Where(i => ImageUrl(i).Length > 0)
                .Select(i => new CatalogImage(i.CoverType, ImageUrl(i)))
                .ToList());

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
                requestId));
        }

        return result;
    }
}
