using Jellyfin.Plugin.Sillon.Lidarr;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>Everything a search found, before it is ordered. Each list is in the order its source gave.</summary>
/// <param name="Query">The search text.</param>
/// <param name="Artists">The artists, in Lidarr's order.</param>
/// <param name="StudioAlbums">The studio albums of the artist the text means, from Lidarr or the metadata service.</param>
/// <param name="SongAlbums">The albums that hold the song searched for, with MusicBrainz's account of the song.</param>
/// <param name="Albums">The albums of Lidarr's search, then those of its album lookup, then those of the allow list that match the text.</param>
/// <param name="Limited">Whether the catalogue is limited to an allow list, which has already vouched for every item.</param>
internal sealed record SearchPool(
    string Query,
    IReadOnlyList<LidarrArtist> Artists,
    IReadOnlyList<LidarrAlbum> StudioAlbums,
    IReadOnlyList<(LidarrAlbum Album, TrackAlbum Song)> SongAlbums,
    IReadOnlyList<LidarrAlbum> Albums,
    bool Limited);

/// <summary>One of the best answers, before the server's library is known.</summary>
/// <param name="Kind">artist or album.</param>
/// <param name="Id">The MusicBrainz id.</param>
/// <param name="Match">exact or partial.</param>
/// <param name="SongArtist">The artist a song album is credited to, null for anything else.</param>
internal sealed record TopPick(string Kind, string Id, string Match, string? SongArtist = null);

/// <summary>The one order of a search: the artists, the albums, and which of them are the best answers.</summary>
internal sealed class RankedSearch
{
    private readonly IReadOnlyList<TopPick> _picks;

    /// <summary>Initializes a new instance of the <see cref="RankedSearch"/> class.</summary>
    /// <param name="artists">The artists, best first.</param>
    /// <param name="albums">The albums, best first.</param>
    /// <param name="picks">The best answers, best first.</param>
    public RankedSearch(IReadOnlyList<LidarrArtist> artists, IReadOnlyList<LidarrAlbum> albums, IReadOnlyList<TopPick> picks)
    {
        Artists = artists;
        Albums = albums;
        _picks = picks;
    }

    /// <summary>Gets the artists, best first.</summary>
    public IReadOnlyList<LidarrArtist> Artists { get; }

    /// <summary>Gets the albums, best first.</summary>
    public IReadOnlyList<LidarrAlbum> Albums { get; }

    /// <summary>
    /// The best answers that are sent: no more than six, only items of the answer, and when the server already holds an album with the song,
    /// none of the other albums with that artist's song, which would only say the same thing again.
    /// </summary>
    /// <param name="sentArtists">The ids of the artists sent.</param>
    /// <param name="sentAlbums">The ids of the albums sent.</param>
    /// <param name="held">Whether the server's library holds the album.</param>
    /// <returns>The entries, null when none stands out.</returns>
    public IReadOnlyList<CatalogTop>? Top(IReadOnlySet<string> sentArtists, IReadOnlySet<string> sentAlbums, Func<string, bool> held)
    {
        var heldFor = _picks
            .Where(p => p.SongArtist is not null && sentAlbums.Contains(p.Id) && held(p.Id))
            .Select(p => p.SongArtist!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var top = _picks
            .Where(p => p.Kind == "artist" ? sentArtists.Contains(p.Id) : sentAlbums.Contains(p.Id))
            .Where(p => p.SongArtist is null || !heldFor.Contains(p.SongArtist) || held(p.Id))
            .Take(SearchRanking.MaxTop)
            .Select(p => new CatalogTop(p.Kind, p.Id, p.Match))
            .ToList();
        return top.Count == 0 ? null : top;
    }
}

/// <summary>
/// How a search is ordered, in one place. The albums go in tiers, each in the order its source gave: the studio albums of the artist the text means, the albums a popularity hint
/// names for the song, the albums whose title is the text, Lidarr's order, and last the plain song hits. The artists go the one meant first, then the exact namesakes, then the rest.
/// The best answers are the same things seen from the top: the artist meant (never one that only starts with the text or contains it), then its best albums, and without such an
/// artist the albums a person would take for the text: a title known by several editions, a song the recordings agree on.
/// </summary>
internal static class SearchRanking
{
    /// <summary>The most entries of the best answers.</summary>
    internal const int MaxTop = 6;

    // Votes on an artist named exactly as typed, measured on Lidarr's search of 2 November 2026. Radiohead has 80, The Beatles 86, Daft Punk 27: the artist people mean by a name has
    // tens. DJ Snake has 1, with photos; its two namesakes 0 and none. A song title's namesake ("Creep": 2 votes, no photo) has a few at most. So 10 votes are enough to be sure, and
    // 1 is enough when the artist has pictures, and being held by Lidarr is enough; fewer, or a vote without a picture, says nothing about who is meant.
    internal const int SureVotes = 10;

    // An exact namesake stays among the best answers when it has a tenth of the votes of the best one: The Beatles' namesakes have 0 or 1 against 86.
    internal const int NamesakeVoteShare = 10;

    // an album is a popular answer from this many editions on: Lemon Tree by Fools Garden has 8, its namesakes 1 or 2. A catalogue limited to a list needs no such proof.
    internal const int PopularEditions = 2;

    private const int MaxArtistsInTop = 2;
    private const int MaxStudioInTop = 3;
    private const int MaxTitlesInTop = 2;
    private const int MaxSongsInTop = 2;
    private const int MaxPlainSongs = 5;
    private const int MaxSongArtistsInTop = 2;
    private const string VariousArtistsId = "89ad4ac3-39f7-470e-963a-56509c546377";

    private const int StudioTier = 0;
    private const int HintedTier = 1;
    private const int ExactTitleTier = 2;
    private const int ListedTier = 3;
    private const int SongTier = 4;

    private sealed class Entry
    {
        public required LidarrAlbum Album { get; set; }

        public required int Tier { get; set; }

        public required int Seq { get; init; }

        public TrackAlbum? Song { get; set; }
    }

    /// <summary>Whether an album is credited to Various Artists, the credit of compilations that merely hold a song.</summary>
    /// <param name="album">The album.</param>
    /// <returns>True for a various-artists album.</returns>
    internal static bool IsVariousArtists(LidarrAlbum album)
        => album.Artist is { } artist
            && (artist.ForeignArtistId.Equals(VariousArtistsId, StringComparison.OrdinalIgnoreCase) || TrackQuery.Fold(artist.ArtistName) == "various artists");

    /// <summary>Whether every release Lidarr lists for the album is a bootleg or a promotional copy: nothing a request can be expected to find.</summary>
    /// <param name="album">The album.</param>
    /// <returns>True for an unofficial album.</returns>
    internal static bool IsUnofficial(LidarrAlbum album)
        => album.Releases is { Count: > 0 } releases && releases.All(r => TrackSearch.IsUnofficial(r.Status));

    /// <summary>
    /// The artist the text means, if any. One called exactly that (case, diacritics and punctuation aside) that people vote on, the best known among namesakes; else
    /// the first artist Lidarr lists when the text is a slip of the keyboard away from its name ("radiohed"), which only an artist with many votes is believed to be.
    /// </summary>
    /// <param name="artists">The artists, in Lidarr's order.</param>
    /// <param name="query">The search text.</param>
    /// <param name="limited">Whether the catalogue is limited to an allow list.</param>
    /// <returns>The artist, then the namesakes that are as well known as it; whether the name is exactly the text.</returns>
    internal static (LidarrArtist? Artist, IReadOnlyList<LidarrArtist> Namesakes, bool Exact) Meant(IReadOnlyList<LidarrArtist> artists, string query, bool limited)
    {
        var wanted = TrackQuery.Fold(query);
        var distinct = artists.Where(a => a.ForeignArtistId.Length > 0).DistinctBy(a => a.ForeignArtistId, StringComparer.Ordinal).ToList();
        var exact = distinct.Where(a => wanted.Length > 0 && TrackQuery.Fold(a.ArtistName) == wanted).ToList();
        var best = exact.Count == 0 ? 0 : exact.Max(Votes);
        var known = exact
            .Where(a => limited || a.Id > 0 || Votes(a) >= SureVotes || (Votes(a) >= 1 && a.Images is { Count: > 0 }))
            .Where(a => limited || Votes(a) * NamesakeVoteShare >= best)
            .OrderByDescending(Votes)
            .ThenByDescending(a => a.Id > 0)
            .ToList();
        if (known.Count > 0)
        {
            return (known[0], known, true);
        }

        // a slip of the keyboard: the first artist Lidarr lists, when it is no namesake of the text and has many votes
        var first = exact.Count == 0 ? distinct.FirstOrDefault() : null;
        return first is not null && Votes(first) >= SureVotes && !NamesTheText(first, wanted) && IsNear(TrackQuery.Fold(first.ArtistName), wanted)
            ? (first, new[] { first }, false)
            : (null, Array.Empty<LidarrArtist>(), false);
    }

    /// <summary>Orders a search and picks its best answers.</summary>
    /// <param name="pool">What was found.</param>
    /// <param name="albumCap">How many albums to keep.</param>
    /// <returns>The artists, the albums and the best answers.</returns>
    public static RankedSearch Rank(SearchPool pool, int albumCap)
    {
        var wanted = TrackQuery.Fold(pool.Query);
        var (_, namesakes, exactArtist) = Meant(pool.Artists, pool.Query, pool.Limited);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var seq = 0;

        void Add(LidarrAlbum album, int tier, TrackAlbum? song = null)
        {
            if (album.ForeignAlbumId.Length == 0)
            {
                return;
            }

            // an album named by its title is a better answer than one that merely holds a song of that name
            if (tier >= ListedTier && wanted.Length > 0 && TrackQuery.Fold(album.Title) == wanted)
            {
                tier = ExactTitleTier;
            }

            if (entries.TryGetValue(album.ForeignAlbumId, out var entry))
            {
                entry.Tier = Math.Min(entry.Tier, tier);
                entry.Song ??= song;

                // the combined search can omit the releases the album lookup carries
                if (entry.Album.Releases is not { Count: > 0 } && album.Releases is { Count: > 0 })
                {
                    entry.Album = album;
                }
            }
            else
            {
                entries[album.ForeignAlbumId] = new Entry { Album = album, Tier = tier, Seq = seq++, Song = song };
            }
        }

        foreach (var album in pool.StudioAlbums)
        {
            Add(album, StudioTier);
        }

        foreach (var (album, song) in pool.SongAlbums.Where(s => s.Song.Hinted))
        {
            Add(album, HintedTier, song);
        }

        foreach (var album in pool.Albums)
        {
            Add(album, ListedTier);
        }

        foreach (var (album, song) in pool.SongAlbums.Where(s => !s.Song.Hinted))
        {
            Add(album, SongTier, song);
        }

        // among the albums named by their title, those known by the most editions first: they are the ones people mean
        var songsSeen = 0;
        var ordered = entries.Values
            .OrderBy(e => e.Tier)
            .ThenByDescending(e => e.Tier == ExactTitleTier ? Editions(e.Album) : 0)
            .ThenBy(e => e.Tier == SongTier && IsVariousArtists(e.Album) ? 1 : 0)
            .ThenBy(e => e.Seq)
            .Where(e => e.Tier != SongTier || ++songsSeen <= MaxPlainSongs)
            .Take(albumCap)
            .ToList();
        var albums = CatalogService.PreferFullRelease(ordered.Select(e => e.Album).ToList());
        var byId = ordered.ToDictionary(e => e.Album.ForeignAlbumId, StringComparer.Ordinal);
        var kept = albums.Select(a => byId[a.ForeignAlbumId]).ToList();

        // the artist meant first, then its namesakes that are as well known, then those called exactly that, then the rest in Lidarr's order
        var distinct = pool.Artists.Where(a => a.ForeignArtistId.Length > 0).DistinctBy(a => a.ForeignArtistId, StringComparer.Ordinal).ToList();
        var exact = distinct.Where(a => wanted.Length > 0 && TrackQuery.Fold(a.ArtistName) == wanted).ToList();
        var artists = namesakes.Concat(exact.Where(a => !namesakes.Contains(a))).Concat(distinct.Where(a => !namesakes.Contains(a) && !exact.Contains(a))).ToList();
        return new RankedSearch(artists, albums, Picks(pool, wanted, namesakes, exactArtist, kept));
    }

    private static List<TopPick> Picks(SearchPool pool, string wanted, IReadOnlyList<LidarrArtist> meant, bool exactArtist, IReadOnlyList<Entry> albums)
    {
        var picks = new List<TopPick>();
        foreach (var artist in meant.Take(MaxArtistsInTop))
        {
            picks.Add(new TopPick("artist", artist.ForeignArtistId, exactArtist ? "exact" : "partial"));
        }

        var songTitle = TrackQuery.Parse(pool.Query) is { } parsed ? TrackQuery.Fold(parsed.Title) : wanted;
        var owners = meant.Select(a => a.ForeignArtistId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // an artist is meant: nothing by anyone else is as good, neither a namesake's album nor a tribute whose title holds the text
        var eligible = albums
            .Where(e => !IsVariousArtists(e.Album) && !IsUnofficial(e.Album))
            .Where(e => owners.Count == 0 || e.Tier == HintedTier || (e.Album.Artist is { } owner && owners.Contains(owner.ForeignArtistId)))
            .ToList();

        // the albums an artist is known by are those with the most editions, when Lidarr tells them all; the metadata service lists none, and its order stands
        var studio = eligible.Where(e => e.Tier == StudioTier && IsPlain(e.Album)).ToList();
        picks.AddRange((studio.All(e => Editions(e.Album) > 0) ? studio.OrderByDescending(e => Editions(e.Album)).ToList() : studio)
            .Take(MaxStudioInTop)
            .Select(e => new TopPick("album", e.Album.ForeignAlbumId, TrackQuery.Fold(e.Album.Title) == wanted ? "exact" : "partial")));

        picks.AddRange(eligible
            .Where(e => e.Tier == HintedTier && e.Song is { Rank: <= 1 })
            .Select(e => new TopPick("album", e.Album.ForeignAlbumId, TrackQuery.Fold(e.Song!.Track) == songTitle ? "exact" : "partial", e.Album.Artist?.ForeignArtistId)));

        picks.AddRange(eligible
            .Where(e => e.Tier == ExactTitleTier && IsPlain(e.Album) && (pool.Limited || Editions(e.Album) >= PopularEditions))
            .Take(MaxTitlesInTop)
            .Select(e => new TopPick("album", e.Album.ForeignAlbumId, "exact")));

        picks.AddRange(eligible
            .Where(e => e.Tier == ListedTier && IsPlain(e.Album) && (pool.Limited || Editions(e.Album) >= PopularEditions) && wanted.Length > 0 && TrackQuery.Fold(e.Album.Title).Contains(wanted, StringComparison.Ordinal))
            .Take(MaxTitlesInTop)
            .Select(e => new TopPick("album", e.Album.ForeignAlbumId, "partial")));

        // the recordings do not say which song is the famous one: without a hint, a song makes the best answers when few artists have it ("La main verte" is Tryo's), not when many do ("Creep")
        var songs = eligible.Where(e => e.Tier == SongTier && e.Song is { Rank: <= 1 }).ToList();
        if (songs.Select(e => e.Album.Artist?.ForeignArtistId).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= MaxSongArtistsInTop)
        {
            picks.AddRange(songs
                .Take(MaxSongsInTop)
                .Select(e => new TopPick("album", e.Album.ForeignAlbumId, TrackQuery.Fold(e.Song!.Track) == songTitle ? "exact" : "partial", e.Album.Artist?.ForeignArtistId)));
        }

        return picks.DistinctBy(p => (p.Kind, p.Id)).ToList();
    }

    private static int Votes(LidarrArtist artist) => artist.Ratings?.Votes ?? 0;

    private static int Editions(LidarrAlbum album) => album.Releases?.Count ?? 0;

    /// <summary>Whether it is an album as such: a compilation, a live recording or a mix is not what a text names.</summary>
    private static bool IsPlain(LidarrAlbum album) => album.SecondaryTypes is not { Count: > 0 };

    /// <summary>Whether the name merely starts with the text or contains it as words: "DJ Snake Eye" for "dj snake".</summary>
    private static bool NamesTheText(LidarrArtist artist, string wanted)
        => wanted.Length > 0 && $" {TrackQuery.Fold(artist.ArtistName)} ".Contains($" {wanted} ", StringComparison.Ordinal);

    /// <summary>Whether the two differ by a slip of the keyboard: a fourth of the letters at most.</summary>
    private static bool IsNear(string a, string b)
    {
        var allowed = Math.Max(1, b.Length / 4);
        if (a.Length == 0 || b.Length == 0 || Math.Abs(a.Length - b.Length) > allowed)
        {
            return false;
        }

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length] <= allowed;
    }
}
