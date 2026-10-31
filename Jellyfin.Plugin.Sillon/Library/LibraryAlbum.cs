namespace Jellyfin.Plugin.Sillon.Library;

/// <summary>What tells an album of the library from another.</summary>
/// <param name="MusicBrainzIds">The MusicBrainz ids it carries.</param>
/// <param name="Name">The name.</param>
/// <param name="AlbumArtist">The album artist.</param>
public sealed record LibraryAlbum(IReadOnlyList<string> MusicBrainzIds, string? Name, string? AlbumArtist)
{
    /// <summary>Tells which albums a library holds: by MusicBrainz id when an item carries it, else by exact title and artist, whatever the case.</summary>
    /// <param name="library">The albums of the library.</param>
    /// <param name="wanted">The albums to look for.</param>
    /// <returns>The MusicBrainz ids of those the library has, whatever their case.</returns>
    public static IReadOnlySet<string> Held(IEnumerable<LibraryAlbum> library, IEnumerable<AlbumKey> wanted)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<(string, string)>();
        foreach (var album in library)
        {
            ids.UnionWith(album.MusicBrainzIds);
            names.Add(NameKey(album.Name, album.AlbumArtist));
        }

        return wanted
            .Where(a => (!string.IsNullOrWhiteSpace(a.ForeignAlbumId) && ids.Contains(a.ForeignAlbumId))
                || (!string.IsNullOrWhiteSpace(a.Title) && names.Contains(NameKey(a.Title, a.Artist))))
            .Select(a => a.ForeignAlbumId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static (string, string) NameKey(string? name, string? artist)
        => ((name ?? string.Empty).ToUpperInvariant(), (artist ?? string.Empty).ToUpperInvariant());
}
