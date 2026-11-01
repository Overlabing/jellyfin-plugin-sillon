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
/// <param name="Group">The release group, which is all a request needs of the album.</param>
public sealed record TrackAlbum(string ReleaseGroupId, string Track, int Rank, int Hits, int Year, string RecordingId = "", int Disc = 0, int Position = 0, bool Hinted = false, MbReleaseGroup? Group = null);

/// <summary>The one order of the kinds of release.</summary>
public static class ReleaseKind
{
    /// <summary>The rank of an EP.</summary>
    public const int Ep = 3;

    /// <summary>Ranks a release group: studio albums first, then other albums, compilations and live albums, EPs, singles, and anything else.</summary>
    /// <param name="primaryType">The primary type.</param>
    /// <param name="secondaryTypes">The secondary types.</param>
    /// <returns>The rank, the lower the better.</returns>
    public static int Rank(string primaryType, IReadOnlyList<string> secondaryTypes)
    {
        switch (primaryType.Trim().ToLowerInvariant())
        {
            case "album":
                if (secondaryTypes.Count == 0)
                {
                    return 0;
                }

                return secondaryTypes.Any(t => t.Trim().ToLowerInvariant() is "compilation" or "live") ? 2 : 1;
            case "ep":
                return Ep;
            case "single":
                return Ep + 1;
            default:
                return Ep + 2;
        }
    }
}

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
                // a request names the artist Lidarr knows the album by: without one it could not be made
                var rank = ReleaseKind.Rank(group.PrimaryType, group.SecondaryTypes);
                if (rank > ReleaseKind.Ep || (rank == ReleaseKind.Ep && hideEps) || group.ArtistId.Length == 0)
                {
                    continue;
                }

                found[group.Id] = found.TryGetValue(group.Id, out var seen)
                    ? seen with { Hits = seen.Hits + 1, Year = EarlierYear(seen.Year, group.Year), Group = Earlier(seen.Group!, group) }
                    : new TrackAlbum(group.Id, recording.Title, rank, 1, group.Year, recording.Id, group.Disc, group.Position, Group: group);
            }
        }

        return found.Values
            .OrderBy(a => a.Rank)
            .ThenByDescending(a => a.Hits)
            .ThenBy(a => a.Year == 0 ? int.MaxValue : a.Year)
            .ToList();
    }

    /// <summary>
    /// The albums of the hinted artists, from the one answer to a question about all of them, in the order of the hint. An artist offers its best
    /// albums only, so that one with many compilations leaves room for the next artists.
    /// </summary>
    /// <param name="recordings">The recordings found.</param>
    /// <param name="query">What was asked, naming the hinted artists.</param>
    /// <param name="hideEps">Whether EPs are dropped.</param>
    /// <returns>The albums, flagged as hinted, each release group once.</returns>
    public static IReadOnlyList<TrackAlbum> Hinted(IReadOnlyList<MbRecording> recordings, TrackQuery query, bool hideEps)
        => query.Artists
            .SelectMany(artist => Albums(recordings, query with { Artist = artist, OtherArtists = null }, hideEps).Take(MaxAlbumsPerHintedArtist))
            .Select(album => album with { Hinted = true })
            .DistinctBy(album => album.ReleaseGroupId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int EarlierYear(int a, int b) => a == 0 ? b : b == 0 ? a : Math.Min(a, b);

    private static MbReleaseGroup Earlier(MbReleaseGroup seen, MbReleaseGroup group)
        => group.Date.Length > 0 && (seen.Date.Length == 0 || string.CompareOrdinal(group.Date, seen.Date) < 0) ? seen with { Date = group.Date } : seen;
}
