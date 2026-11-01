using Jellyfin.Plugin.Sillon.Library;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class LibraryAlbumCacheTests
{
    private static readonly AlbumKey Wanted = new("mbid", "Title", "Artist");

    private sealed class Library
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public int Reads { get; private set; }

        public List<LibraryAlbum> Albums { get; } = new();

        public Action? DuringRead { get; set; }

        public IReadOnlyList<LibraryAlbum> Read()
        {
            Reads++;
            DuringRead?.Invoke();
            return Albums.ToList();
        }
    }

    private static readonly LibraryAlbum Held = new(new[] { "mbid" }, "Title", "Artist");

    [Fact]
    public void AKeystrokeAfterAnother_ReadsTheLibraryOnce_UntilTheCopyIsOld()
    {
        var library = new Library();
        var cache = new LibraryAlbumCache(library.Read, () => library.Now);

        cache.Held(new[] { Wanted });
        library.Now += LibraryAlbumCache.Ttl - TimeSpan.FromSeconds(1);
        cache.Held(new[] { Wanted });
        Assert.Equal(1, library.Reads);

        library.Now += TimeSpan.FromSeconds(1);
        cache.Held(new[] { Wanted });
        Assert.Equal(2, library.Reads);
    }

    [Fact]
    public void AnAlbumThatArrives_IsThereAtOnce()
    {
        var library = new Library();
        var cache = new LibraryAlbumCache(library.Read, () => library.Now);
        Assert.Empty(cache.Held(new[] { Wanted }));

        library.Albums.Add(Held);
        cache.Invalidate();

        Assert.Contains("mbid", cache.Held(new[] { Wanted }));
    }

    [Fact]
    public void AnAlbumThatArrivesDuringARead_IsNotLostToTheCopy()
    {
        var library = new Library();
        var cache = new LibraryAlbumCache(library.Read, () => library.Now);
        library.DuringRead = () =>
        {
            library.DuringRead = null;
            cache.Invalidate();
            library.Albums.Add(Held);
        };

        cache.Held(new[] { Wanted });

        Assert.Contains("mbid", cache.Held(new[] { Wanted }));
    }

    [Fact]
    public void OneCopy_AnswersForAnyAlbum()
    {
        var library = new Library();
        library.Albums.Add(Held);
        var cache = new LibraryAlbumCache(library.Read, () => library.Now);

        Assert.Empty(cache.Held(new[] { new AlbumKey("other", "Other", "Artist") }));
        Assert.Contains("mbid", cache.Held(new[] { Wanted }));
        Assert.Equal(1, library.Reads);
    }
}
