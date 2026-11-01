using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>What was found of an artist's albums.</summary>
/// <param name="Albums">The albums: studio albums first, the newest first within a kind.</param>
/// <param name="Complete">Whether what the sources could tell was had in time, so that a search by the artist's name has nothing to add.</param>
/// <param name="Failure">What was not had, when anything; holds no search text.</param>
internal sealed record DiscographyAnswer(IReadOnlyList<LidarrAlbum> Albums, bool Complete, string? Failure);

/// <summary>How much an artist's albums may cost.</summary>
/// <param name="Budget">How long the whole may take; what was resolved by then is kept.</param>
/// <param name="Browse">No further page is asked of MusicBrainz past this time, so that the lookups have some of the budget.</param>
internal sealed record DiscographyLimits(TimeSpan Budget, TimeSpan Browse);

/// <summary>
/// An artist's albums when Lidarr does not hold the artist. Lidarr's metadata service lists them all in one request, without a lookup per album, since a request goes through
/// Lidarr by the release group's id anyway. When it gives no answer: MusicBrainz lists the release groups credited to the artist, and each one goes through Lidarr's id lookup.
/// Either covers joint albums and compilations that name the artist, not the various-artists ones the artist merely has a track on.
/// Answers are kept (the service's and MusicBrainz's by their clients, Lidarr's here for an hour), so what a budget cut short goes further the next time.
/// </summary>
internal sealed class ArtistDiscography
{
    private const int MaxPages = 10;
    private const int Concurrency = 3;
    private const int MaxArtistPageLookups = 150;
    private const int MaxSearchAlbums = 12;
    private const int MaxListedSearchAlbums = 40;
    private const int MaxKept = 2000;
    private const double BrowseShare = 0.6;
    private static readonly TimeSpan FoundTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(5);

    // The Beatles take 3 pages, at MusicBrainz's pace of one a little over a second
    private static readonly TimeSpan DefaultBrowseWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BackgroundBrowse = TimeSpan.FromSeconds(30);

    private readonly IMusicBrainzApi _musicBrainz;
    private readonly Func<DateTimeOffset> _now;
    private readonly IArtistMetadata? _metadata;
    private readonly TimeSpan _browseWait;
    private readonly Dictionary<string, Task<(IReadOnlyList<MbReleaseGroup> Groups, string? Failure)>> _browses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Expires, LidarrAlbum? Album)> _kept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ArtistDiscography"/> class.</summary>
    /// <param name="musicBrainz">MusicBrainz.</param>
    /// <param name="now">The clock.</param>
    /// <param name="metadata">Lidarr's metadata service; MusicBrainz alone when null.</param>
    /// <param name="browseWait">How long a listed answer waits for MusicBrainz's joint albums; 3 seconds when null.</param>
    public ArtistDiscography(IMusicBrainzApi musicBrainz, Func<DateTimeOffset> now, IArtistMetadata? metadata = null, TimeSpan? browseWait = null)
    {
        _musicBrainz = musicBrainz;
        _now = now;
        _metadata = metadata;
        _browseWait = browseWait ?? DefaultBrowseWait;
    }

    /// <summary>The limits of an artist page.</summary>
    /// <param name="budget">The time left.</param>
    /// <returns>The limits.</returns>
    public static DiscographyLimits ArtistPage(TimeSpan budget) => new(budget, budget * BrowseShare);

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

    /// <summary>The studio albums a search puts first for an artist Lidarr does not hold: from the metadata service's one answer, or none. A keystroke asks nothing of MusicBrainz and looks nothing up.</summary>
    /// <param name="artistId">The artist's MusicBrainz id.</param>
    /// <param name="config">The settings, for the types they hide.</param>
    /// <param name="ct">The search's deadline.</param>
    /// <returns>The albums, the newest first.</returns>
    public async Task<IReadOnlyList<LidarrAlbum>> ListedStudioAlbumsAsync(string artistId, PluginConfiguration config, CancellationToken ct)
    {
        if (_metadata is null || await _metadata.GetArtistAsync(artistId, ct).ConfigureAwait(false) is not { } answer)
        {
            return Array.Empty<LidarrAlbum>();
        }

        var artist = ToArtist(answer);
        return answer.Albums
            .Where(a => IsListed(a, config, studioOnly: true))
            .OrderByDescending(a => CatalogService.YearOf(a.ReleaseDate))
            .ThenBy(a => a.Title, StringComparer.Ordinal)
            .Take(MaxListedSearchAlbums)
            .Select(a => ToAlbum(a, artist))
            .ToList();
    }

    /// <summary>A release group found by a search for a song, in the form Lidarr gives it, named after the artist it is credited to.</summary>
    /// <param name="group">The release group.</param>
    /// <returns>The album.</returns>
    public static LidarrAlbum ToAlbum(MbReleaseGroup group)
        => ToAlbum(group, new LidarrArtist { ForeignArtistId = group.ArtistId, ArtistName = group.ArtistName });

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

        if (await ListedAsync(artistId, config, limits, budget.Token, ct).ConfigureAwait(false) is { } listed)
        {
            return listed;
        }

        var (groups, failure) = await BrowseAsync(artistId, config, limits, budget.Token, ct).ConfigureAwait(false);

        // the oldest first: when the limits cut the list short, what goes is the late unofficial compilations an old artist has by the hundred, not the albums
        var wanted = groups
            .OrderBy(Rank)
            .ThenBy(g => g.Year == 0 ? int.MaxValue : g.Year)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .Take(MaxArtistPageLookups)
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

    /// <summary>The cover MusicBrainz's Cover Art Archive holds for a release group. It answers 404 when it has none, which the apps show as their placeholder.</summary>
    /// <param name="releaseGroupId">The release group's MusicBrainz id.</param>
    /// <returns>The URL.</returns>
    public static string CoverUrl(string releaseGroupId) => $"https://coverartarchive.org/release-group/{Uri.EscapeDataString(releaseGroupId)}/front-500";

    private async Task<DiscographyAnswer?> ListedAsync(string artistId, PluginConfiguration config, DiscographyLimits limits, CancellationToken budget, CancellationToken ct)
    {
        if (_metadata is null)
        {
            return null;
        }

        MetadataArtist? answer;
        try
        {
            answer = await _metadata.GetArtistAsync(artistId, budget).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        if (answer is null)
        {
            return null;
        }

        var artist = ToArtist(answer);
        var (others, failure) = await JointAlbumsAsync(artistId, answer, config, limits, ct).ConfigureAwait(false);
        var albums = answer.Albums
            .Where(a => IsListed(a, config, studioOnly: false))
            .Select(a => (Group: new MbReleaseGroup(a.Id, a.Title, a.Type, a.SecondaryTypes, CatalogService.YearOf(a.ReleaseDate)), Album: ToAlbum(a, artist)))
            .Concat(others.Select(g => (Group: g, Album: ToAlbum(g, artist))))
            .OrderBy(a => Rank(a.Group))
            .ThenByDescending(a => a.Group.Year)
            .ThenBy(a => a.Group.Title, StringComparer.Ordinal)
            .Select(a => a.Album)
            .ToList();
        return new DiscographyAnswer(albums, true, failure);
    }

    private static LidarrArtist ToArtist(MetadataArtist answer)
        => new()
        {
            ForeignArtistId = answer.Id,
            ArtistName = answer.Name,
            Disambiguation = answer.Disambiguation,
            ArtistType = answer.ArtistType,
            Genres = answer.Genres.ToList(),
            Images = answer.Images.Select(i => new LidarrImage { CoverType = i.CoverType, RemoteUrl = i.Url }).ToList(),
        };

    /// <summary>
    /// What MusicBrainz credits to the artist and the service does not list: joint albums that name someone else first, and whatever the service has not caught up with.
    /// Waits for it a little, and whoever comes next finds its pages kept by MusicBrainz's client. MusicBrainz's filter has already left out what has only bootleg or promotional releases.
    /// </summary>
    private async Task<(IReadOnlyList<MbReleaseGroup> Groups, string? Failure)> JointAlbumsAsync(string artistId, MetadataArtist listed, PluginConfiguration config, DiscographyLimits limits, CancellationToken ct)
    {
        var browse = SharedBrowse(artistId, config, limits);
        await Task.WhenAny(browse, Task.Delay(_browseWait, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!browse.IsCompleted)
        {
            return (Array.Empty<MbReleaseGroup>(), CatalogService.OutOfTime);
        }

        var (groups, failure) = await browse.ConfigureAwait(false);
        var known = listed.Albums.SelectMany(a => a.OldIds.Append(a.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (groups.Where(g => !known.Contains(g.Id) && !IsSpokenOnly(g.SecondaryTypes)).ToList(), failure);
    }

    /// <summary>One browse per artist at most, whoever asks and however soon they give up: it goes on after the wait, at MusicBrainz's pace, for the next visit to find.</summary>
    private Task<(IReadOnlyList<MbReleaseGroup> Groups, string? Failure)> SharedBrowse(string artistId, PluginConfiguration config, DiscographyLimits limits)
    {
        var key = $"{artistId.ToLowerInvariant()}|{config.HideEps}|{config.HideSingles}";
        lock (_browses)
        {
            if (_browses.TryGetValue(key, out var running))
            {
                return running;
            }

            var browse = Task.Run(async () =>
            {
                using var budget = new CancellationTokenSource(BackgroundBrowse);
                try
                {
                    return await BrowseAsync(artistId, config, limits with { Browse = BackgroundBrowse }, budget.Token, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    lock (_browses)
                    {
                        _browses.Remove(key);
                    }
                }
            });
            _browses[key] = browse;
            return browse;
        }
    }

    private static bool IsListed(MetadataAlbum album, PluginConfiguration config, bool studioOnly)
    {
        var wantedType = album.Type.ToLowerInvariant() switch
        {
            "album" => true,
            "ep" => !config.HideEps && !studioOnly,
            "single" => !config.HideSingles && !studioOnly,
            _ => false,
        };
        return wantedType
            && (!studioOnly || album.SecondaryTypes.Count == 0)
            && IsOfficial(album)
            && !IsSpokenOnly(album.SecondaryTypes);
    }

    /// <summary>Most of what the service lists of a famous artist are bootlegs. A release group without any known status tells nothing either way: a date is what shows it is more than an unfinished entry.</summary>
    private static bool IsOfficial(MetadataAlbum album)
        => album.ReleaseStatuses.Count == 0
            ? !string.IsNullOrEmpty(album.ReleaseDate)
            : album.ReleaseStatuses.Contains("Official", StringComparer.OrdinalIgnoreCase);

    /// <summary>Interviews and the like are nothing to listen to, and the apps would show them among the albums, having no other place for them.</summary>
    private static bool IsSpokenOnly(IReadOnlyList<string> secondaryTypes)
        => secondaryTypes.Count > 0 && secondaryTypes.All(t =>
            t.Equals("Interview", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Spokenword", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Audiobook", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Audio drama", StringComparison.OrdinalIgnoreCase));

    /// <summary>What the rest of the catalogue expects of an album, in the form Lidarr gives it. It has no releases: what those would tell (tracks, discs, label) stays unknown until the album is opened.</summary>
    private static LidarrAlbum ToAlbum(MetadataAlbum album, LidarrArtist artist)
        => new()
        {
            ForeignAlbumId = album.Id,
            Title = album.Title,
            AlbumType = album.Type,
            SecondaryTypes = album.SecondaryTypes.ToList(),
            ReleaseDate = DateTime.TryParseExact(album.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture)
                : album.ReleaseDate,
            Images = new List<LidarrImage> { new() { CoverType = "cover", RemoteUrl = CoverUrl(album.Id) } },
            Artist = artist,
        };

    /// <summary>A joint album in the name of the artist credited first, as Lidarr has it, so that a request names the same artist as it does for a lookup.</summary>
    private static LidarrAlbum ToAlbum(MbReleaseGroup group, LidarrArtist listed)
        => ToAlbum(
            new MetadataAlbum(group.Id, group.Title, group.PrimaryType, group.SecondaryTypes, Array.Empty<string>(), group.Date.Length > 0 ? group.Date : null, Array.Empty<string>()),
            group.ArtistId.Length == 0 || group.ArtistId.Equals(listed.ForeignArtistId, StringComparison.OrdinalIgnoreCase)
                ? listed
                : new LidarrArtist { ForeignArtistId = group.ArtistId, ArtistName = group.ArtistName });

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

    private static int Rank(MbReleaseGroup group) => ReleaseKind.Rank(group.PrimaryType, group.SecondaryTypes);

    /// <summary>A release group known from unofficial releases alone is not something a request can be expected to find.</summary>
    private static bool IsOffered(LidarrAlbum album)
        => album.Releases is not { Count: > 0 } releases
            || releases.Any(r => !r.Status.Equals("Bootleg", StringComparison.OrdinalIgnoreCase));
}
