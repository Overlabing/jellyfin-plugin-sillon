using System.Diagnostics;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>What was found of an artist's albums.</summary>
/// <param name="Albums">The albums, resolved through Lidarr: studio albums first, the newest first within a kind.</param>
/// <param name="Complete">Whether MusicBrainz and every lookup answered in time.</param>
/// <param name="Failure">Why it is not complete; holds no search text.</param>
internal sealed record DiscographyAnswer(IReadOnlyList<LidarrAlbum> Albums, bool Complete, string? Failure);

/// <summary>How much an artist's albums may cost.</summary>
/// <param name="Budget">How long the whole may take; what was resolved by then is kept.</param>
/// <param name="Browse">No further page is asked of MusicBrainz past this time, so that the lookups have some of the budget.</param>
/// <param name="MaxLookups">The most albums to resolve.</param>
/// <param name="StudioOnly">Whether only albums without a secondary type are wanted.</param>
internal sealed record DiscographyLimits(TimeSpan Budget, TimeSpan Browse, int MaxLookups, bool StudioOnly);

/// <summary>
/// An artist's albums when Lidarr does not hold the artist: MusicBrainz lists the release groups credited to the artist, and each one goes through Lidarr's id lookup so
/// that a request takes the usual path. That covers joint albums and compilations that name the artist, not the various-artists ones the artist merely has a track on.
/// Pages and answers are kept (by MusicBrainz's client for a day, here for an hour), so what a budget cut short goes further the next time.
/// </summary>
internal sealed class ArtistDiscography
{
    private const int MaxPages = 10;
    private const int Concurrency = 3;
    private const int MaxArtistPageLookups = 150;
    private const int MaxSearchLookups = 40;
    private const int MaxSearchAlbums = 12;
    private const int MaxKept = 2000;
    private const double BrowseShare = 0.6;
    private static readonly TimeSpan FoundTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(5);

    private readonly IMusicBrainzApi _musicBrainz;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, (DateTimeOffset Expires, LidarrAlbum? Album)> _kept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ArtistDiscography"/> class.</summary>
    /// <param name="musicBrainz">MusicBrainz.</param>
    /// <param name="now">The clock.</param>
    public ArtistDiscography(IMusicBrainzApi musicBrainz, Func<DateTimeOffset> now)
    {
        _musicBrainz = musicBrainz;
        _now = now;
    }

    /// <summary>The limits of an artist page.</summary>
    /// <param name="budget">The time left.</param>
    /// <returns>The limits.</returns>
    public static DiscographyLimits ArtistPage(TimeSpan budget) => new(budget, budget * BrowseShare, MaxArtistPageLookups, false);

    /// <summary>The limits of a search that names an artist.</summary>
    /// <param name="budget">The time left.</param>
    /// <returns>The limits.</returns>
    public static DiscographyLimits Search(TimeSpan budget) => new(budget, budget * BrowseShare, MaxSearchLookups, true);

    /// <summary>What a search puts first of an artist: the studio albums with the most editions, which leaves out the odd unofficial one, the newest first.</summary>
    /// <param name="albums">The artist's albums.</param>
    /// <returns>A few of them.</returns>
    public static IReadOnlyList<LidarrAlbum> BestStudioAlbums(IEnumerable<LidarrAlbum> albums)
        => albums
            .Where(a => a.AlbumType.Trim().Equals("album", StringComparison.OrdinalIgnoreCase) && a.SecondaryTypes is not { Count: > 0 } && IsOffered(a))
            .OrderByDescending(a => a.Releases?.Count ?? 0)
            .Take(MaxSearchAlbums)
            .OrderByDescending(a => CatalogService.YearOf(a.ReleaseDate))
            .ToList();

    /// <summary>Lists the albums. Never fails for MusicBrainz, Lidarr or the budget, only when the caller gives up.</summary>
    /// <param name="lidarr">Lidarr.</param>
    /// <param name="artistId">The artist's MusicBrainz id.</param>
    /// <param name="config">The settings, for the types they hide.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>What was found.</returns>
    public async Task<DiscographyAnswer> AlbumsAsync(ILidarrApi lidarr, string artistId, PluginConfiguration config, DiscographyLimits limits, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(limits.Budget > TimeSpan.Zero ? limits.Budget : TimeSpan.FromMilliseconds(1));

        var (groups, failure) = await BrowseAsync(artistId, config, limits, budget.Token, ct).ConfigureAwait(false);

        // the oldest first: when the limits cut the list short, what goes is the late unofficial compilations an old artist has by the hundred, not the albums
        var wanted = groups
            .Where(g => !limits.StudioOnly || IsStudio(g))
            .OrderBy(Rank)
            .ThenBy(g => g.Year == 0 ? int.MaxValue : g.Year)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .Take(limits.MaxLookups)
            .ToList();

        using var gate = new SemaphoreSlim(Concurrency);
        var lookups = wanted.Select(g => ResolveAsync(lidarr, g.Id, gate, budget.Token, ct)).ToList();
        var answers = await Task.WhenAll(lookups).ConfigureAwait(false);
        failure ??= answers.Select(a => a.Failure).FirstOrDefault(f => f is not null);

        // the order the app shows them in: the newest first
        var albums = wanted
            .Zip(answers, (group, answer) => (Group: group, answer.Album))
            .Where(a => a.Album is not null && IsOffered(a.Album))
            .OrderBy(a => Rank(a.Group))
            .ThenByDescending(a => a.Group.Year)
            .ThenBy(a => a.Group.Title, StringComparer.Ordinal)
            .Select(a => a.Album!)
            .ToList();
        return new DiscographyAnswer(albums, failure is null, failure);
    }

    private async Task<(IReadOnlyList<MbReleaseGroup> Groups, string? Failure)> BrowseAsync(string artistId, PluginConfiguration config, DiscographyLimits limits, CancellationToken budget, CancellationToken ct)
    {
        var types = new List<string> { "album" };
        if (!config.HideEps)
        {
            types.Add("ep");
        }

        if (!config.HideSingles)
        {
            types.Add("single");
        }

        var groups = new Dictionary<string, MbReleaseGroup>(StringComparer.OrdinalIgnoreCase);
        var started = Stopwatch.GetTimestamp();
        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                if (page > 0 && Stopwatch.GetElapsedTime(started) >= limits.Browse)
                {
                    return (groups.Values.ToList(), CatalogService.OutOfTime);
                }

                var answer = await _musicBrainz.BrowseReleaseGroupsAsync(artistId, types, page * MusicBrainzClient.BrowsePageSize, budget).ConfigureAwait(false);
                foreach (var group in answer.Groups)
                {
                    groups.TryAdd(group.Id, group);
                }

                if (answer.Groups.Count == 0 || (page + 1) * MusicBrainzClient.BrowsePageSize >= answer.Total)
                {
                    break;
                }
            }

            return (groups.Values.ToList(), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (groups.Values.ToList(), CatalogService.OutOfTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (groups.Values.ToList(), ex is MusicBrainzUnavailableException ? ex.Message : ex.GetType().Name);
        }
    }

    private async Task<(LidarrAlbum? Album, string? Failure)> ResolveAsync(ILidarrApi lidarr, string id, SemaphoreSlim gate, CancellationToken budget, CancellationToken ct)
    {
        if (TryKept(id, out var kept))
        {
            return (kept, null);
        }

        try
        {
            await gate.WaitAsync(budget).ConfigureAwait(false);
            try
            {
                var album = (await lidarr.LookupAlbumsAsync("lidarr:" + id, budget).ConfigureAwait(false))
                    .FirstOrDefault(a => string.Equals(a.ForeignAlbumId, id, StringComparison.OrdinalIgnoreCase));
                Keep(id, album);
                return (album, null);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, CatalogService.OutOfTime);
        }
        catch (LidarrException ex)
        {
            return (null, ex.Message);
        }
    }

    private bool TryKept(string id, out LidarrAlbum? album)
    {
        lock (_kept)
        {
            if (_kept.TryGetValue(id, out var entry) && entry.Expires > _now())
            {
                album = entry.Album;
                return true;
            }
        }

        album = null;
        return false;
    }

    private void Keep(string id, LidarrAlbum? album)
    {
        lock (_kept)
        {
            if (_kept.Count >= MaxKept)
            {
                _kept.Clear();
            }

            _kept[id] = (_now() + (album is null ? NotFoundTtl : FoundTtl), album);
        }
    }

    private static bool IsStudio(MbReleaseGroup group)
        => group.PrimaryType.Equals("album", StringComparison.OrdinalIgnoreCase) && group.SecondaryTypes.Count == 0;

    /// <summary>Studio albums first, so that a budget or the cap only costs the rest: EPs, other albums (compilations, live, ...), then whatever has both a lesser type and a secondary one.</summary>
    private static int Rank(MbReleaseGroup group)
    {
        var album = group.PrimaryType.Equals("album", StringComparison.OrdinalIgnoreCase);
        var ep = group.PrimaryType.Equals("ep", StringComparison.OrdinalIgnoreCase);
        var plain = group.SecondaryTypes.Count == 0;
        return (album, ep, plain) switch
        {
            (true, _, true) => 0,
            (_, true, true) => 1,
            (true, _, false) => 2,
            _ => 3,
        };
    }

    /// <summary>A release group known from unofficial releases alone is not something a request can be expected to find.</summary>
    private static bool IsOffered(LidarrAlbum album)
        => album.Releases is not { Count: > 0 } releases
            || releases.Any(r => !r.Status.Equals("Bootleg", StringComparison.OrdinalIgnoreCase));
}
