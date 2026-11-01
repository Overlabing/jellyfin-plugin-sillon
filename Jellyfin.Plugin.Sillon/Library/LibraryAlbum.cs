namespace Jellyfin.Plugin.Sillon.Library;

/// <summary>
/// The library's albums, read at most once in <see cref="Ttl"/>: reading them all is the slowest thing a search does on the server, and a search asks on every keystroke.
/// What the library changes drops the copy at once, and the state of an album someone asked for comes from its request, never from here.
/// </summary>
public sealed class LibraryAlbumCache
{
    /// <summary>How long a read is kept when nothing says the library changed.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<LibraryAlbum>> _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private IReadOnlyList<LibraryAlbum>? _albums;
    private DateTimeOffset _expires;
    private long _generation;

    /// <summary>Initializes a new instance of the <see cref="LibraryAlbumCache"/> class.</summary>
    /// <param name="read">Reads the library's albums.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    public LibraryAlbumCache(Func<IReadOnlyList<LibraryAlbum>> read, Func<DateTimeOffset>? now = null)
    {
        _read = read;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Tells which albums the library holds.</summary>
    /// <param name="wanted">The albums to look for.</param>
    /// <returns>The MusicBrainz ids of those it has.</returns>
    public IReadOnlySet<string> Held(IEnumerable<AlbumKey> wanted) => LibraryAlbum.Held(Albums(), wanted);

    /// <summary>Forgets the copy; the next question reads the library again.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _albums = null;
            _generation++;
        }
    }

    private IReadOnlyList<LibraryAlbum> Albums()
    {
        long generation;
        lock (_lock)
        {
            if (_albums is not null && _now() < _expires)
            {
                return _albums;
            }

            generation = _generation;
        }

        var albums = _read();
        lock (_lock)
        {
            // a change during the read may be missing from it: it serves this question, and is not kept for the next
            if (generation == _generation)
            {
                _albums = albums;
                _expires = _now() + Ttl;
            }
        }

        return albums;
    }
}

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
