namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>An album that contains the track searched for.</summary>
/// <param name="ReleaseGroupId">The release group's MusicBrainz id.</param>
/// <param name="Track">The track title as MusicBrainz spells it.</param>
/// <param name="Rank">0 studio album, 1 album with another secondary type, 2 compilation or live, 3 EP.</param>
/// <param name="Hits">How many recordings put the track on this album.</param>
/// <param name="Year">The earliest year seen, 0 when unknown.</param>
/// <param name="RecordingId">The recording's MusicBrainz id (of the first recording that put the track here).</param>
/// <param name="Disc">The disc of the track on the first release MusicBrainz listed, 0 when unknown.</param>
/// <param name="Position">The track's position on that disc, 0 when unknown.</param>
/// <param name="Hinted">Whether it comes from an artist a popularity hint named.</param>
public sealed record TrackAlbum(string ReleaseGroupId, string Track, int Rank, int Hits, int Year, string RecordingId = "", int Disc = 0, int Position = 0, bool Hinted = false);

/// <summary>Turns MusicBrainz recordings into the albums to offer for a track search.</summary>
public static class TrackSearch
{
    private const int MinScore = 70;
    private const int MaxAlbumsPerHintedArtist = 2;

    /// <summary>
    /// Keeps the albums the recordings appear on: studio albums first, then albums with another secondary type, then compilations and live
    /// albums, then EPs (unless hidden); singles and anything else are dropped. Recordings that are not the track (by title) or not by the
    /// artist named are ignored. Each release group appears once; the order is rank, then how many recordings agree, then the earliest year.
    /// </summary>
    /// <param name="recordings">The recordings found.</param>
    /// <param name="query">What was asked.</param>
    /// <param name="hideEps">Whether EPs are dropped.</param>
    /// <returns>The albums, best first.</returns>
    public static IReadOnlyList<TrackAlbum> Albums(IReadOnlyList<MbRecording> recordings, TrackQuery query, bool hideEps)
    {
        var title = TrackQuery.Fold(query.Title);
        var artist = query.Artist is null ? null : TrackQuery.FoldTight(query.Artist);
        var found = new Dictionary<string, TrackAlbum>(StringComparer.OrdinalIgnoreCase);
        foreach (var recording in recordings)
        {
            // a recording found under the artist's other spelling scores low, and the title and artist checks here are what vouch for it
            if ((recording.Score < MinScore && !query.AnySpelling) || TrackQuery.Fold(recording.Title) != title)
            {
                continue;
            }

            if (artist is { Length: > 0 } && !TrackQuery.FoldTight(recording.Artist).Contains(artist, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var group in recording.ReleaseGroups)
            {
                var rank = RankOf(group, hideEps);
                if (rank < 0)
                {
                    continue;
                }

                found[group.Id] = found.TryGetValue(group.Id, out var seen)
                    ? seen with { Hits = seen.Hits + 1, Year = EarlierYear(seen.Year, group.Year) }
                    : new TrackAlbum(group.Id, recording.Title, rank, 1, group.Year, recording.Id, group.Disc, group.Position);
            }
        }

        return found.Values
            .OrderBy(a => a.Rank)
            .ThenByDescending(a => a.Hits)
            .ThenBy(a => a.Year == 0 ? int.MaxValue : a.Year)
            .ToList();
    }

    /// <summary>
    /// Puts the albums of the hinted artists first, in the order of the hint, then the plain results, each release group once. An artist
    /// offers its best albums only, so that one with many compilations leaves room for the next artists and the plain results.
    /// </summary>
    /// <param name="hinted">The albums found for each hinted artist, most popular artist first.</param>
    /// <param name="plain">The albums found by the title alone.</param>
    /// <returns>The albums, hinted ones flagged.</returns>
    public static IReadOnlyList<TrackAlbum> WithHints(IReadOnlyList<IReadOnlyList<TrackAlbum>> hinted, IReadOnlyList<TrackAlbum> plain)
        => hinted
            .SelectMany(albums => albums.Take(MaxAlbumsPerHintedArtist))
            .Select(album => album with { Hinted = true })
            .Concat(plain)
            .DistinctBy(album => album.ReleaseGroupId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int EarlierYear(int a, int b) => a == 0 ? b : b == 0 ? a : Math.Min(a, b);

    private static int RankOf(MbReleaseGroup group, bool hideEps)
    {
        var secondary = group.SecondaryTypes.Select(t => t.Trim().ToLowerInvariant()).ToList();
        switch (group.PrimaryType.Trim().ToLowerInvariant())
        {
            case "album":
                if (secondary.Count == 0)
                {
                    return 0;
                }

                return secondary.Contains("compilation") || secondary.Contains("live") ? 2 : 1;
            case "ep":
                return hideEps ? -1 : 3;
            default:
                return -1;
        }
    }
}
