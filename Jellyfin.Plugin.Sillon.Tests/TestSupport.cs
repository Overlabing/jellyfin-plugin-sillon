using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Sillon.Tests;

internal sealed class FakeLidarr : ILidarrApi, ILidarrProvider
{
    public Dictionary<string, LidarrAlbum> Existing { get; } = new();

    public List<LidarrAddAlbum> Added { get; } = new();

    public Dictionary<int, bool> Monitored { get; } = new();

    public List<int> Searched { get; } = new();

    public Dictionary<int, LidarrAlbum> Albums { get; } = new();

    public List<LidarrQueueRecord> Queue { get; set; } = new();

    public Dictionary<int, List<LidarrTrackFile>> Files { get; } = new();

    public List<string> SearchTerms { get; } = new();

    public Func<string, IReadOnlyList<LidarrSearchItem>> OnSearch { get; set; } = _ => Array.Empty<LidarrSearchItem>();

    public List<string> Calls { get; } = new();

    public Func<int, int, LidarrAlbum?>? OnGet { get; set; }

    private int _gets;

    public Exception? AddError { get; set; }

    public Exception? AlbumError { get; set; }

    public ILidarrApi Create() => this;

    public Task<IReadOnlyList<LidarrSearchItem>> SearchAsync(string term, CancellationToken ct = default)
    {
        SearchTerms.Add(term);
        return Task.FromResult(OnSearch(term));
    }

    public List<string> LookupTerms { get; } = new();

    public Func<string, IReadOnlyList<LidarrAlbum>> OnLookup { get; set; } = _ => Array.Empty<LidarrAlbum>();

    public Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct = default)
    {
        LookupTerms.Add(term);
        return Task.FromResult(OnLookup(term));
    }

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LidarrAlbum>>(Albums.Values.Where(a => a.Artist?.Id == artistId).ToList());

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LidarrAlbum>>(Existing.TryGetValue(foreignAlbumId, out var a) ? new[] { a } : Array.Empty<LidarrAlbum>());

    public Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum album, CancellationToken ct = default)
    {
        if (AddError is not null)
        {
            throw AddError;
        }

        Added.Add(album);
        return Task.FromResult(new LidarrAlbum { Id = 100 + Added.Count, ForeignAlbumId = album.ForeignAlbumId });
    }

    public Task MonitorAlbumsAsync(IReadOnlyList<int> ids, bool monitored, CancellationToken ct = default)
    {
        Calls.Add("monitor");
        foreach (var id in ids)
        {
            Monitored[id] = monitored;
        }

        return Task.CompletedTask;
    }

    public Task AlbumSearchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        Calls.Add("search");
        Searched.AddRange(ids);
        return Task.CompletedTask;
    }

    public Task<LidarrAlbum> GetAlbumAsync(int id, CancellationToken ct = default)
    {
        if (AlbumError is not null)
        {
            throw AlbumError;
        }

        Calls.Add("get");
        if (OnGet?.Invoke(id, ++_gets) is { } custom)
        {
            return Task.FromResult(custom);
        }

        return Task.FromResult(Albums.TryGetValue(id, out var a) ? a : new LidarrAlbum { Id = id, Duration = 1000 });
    }

    public Task<IReadOnlyList<LidarrQueueRecord>> GetQueueAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LidarrQueueRecord>>(Queue);

    public Task<IReadOnlyList<LidarrTrackFile>> GetTrackFilesAsync(int albumId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LidarrTrackFile>>(Files.TryGetValue(albumId, out var f) ? f : new List<LidarrTrackFile>());

    public Task<string?> GetAlbumFolderAsync(int albumId, CancellationToken ct = default)
        => Task.FromResult(PathMap.AlbumFolder(Files.TryGetValue(albumId, out var f) ? f.Select(x => x.Path) : Array.Empty<string>()));

    public int CutoffSearches { get; private set; }

    public Task CutoffUnmetSearchAsync(CancellationToken ct = default)
    {
        CutoffSearches++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeLibrary : IJellyfinLibrary
{
    private int _lookups;

    public Dictionary<string, string> Users { get; } = new();

    /// <summary>Gets the lookups that find nothing before the album appears; negative: never.</summary>
    public int MissesBeforeFound { get; set; }

    public string ItemId { get; set; } = "0123456789abcdef0123456789abcdef";

    public bool AlbumPresent { get; set; }

    public HashSet<string> KnownTitles { get; } = new();

    public List<string> Reported { get; } = new();

    public List<(string UserId, string ItemId)> Favorites { get; } = new();

    public bool FavoriteFails { get; set; }

    /// <summary>Gets the MusicBrainz ids the library knows; looked up when the title is blank.</summary>
    public HashSet<string> KnownMbids { get; } = new();

    /// <summary>Gets the album folders (as Jellyfin sees them) the library lists, with their item id.</summary>
    public Dictionary<string, string> KnownFolders { get; } = new();

    /// <summary>Gets the folders Jellyfin was asked to scan.</summary>
    public List<string> Refreshed { get; } = new();

    public string? FindAlbumByPath(string jellyfinFolder) => KnownFolders.TryGetValue(jellyfinFolder, out var id) ? id : null;

    public void RefreshPath(string jellyfinFolder) => Refreshed.Add(jellyfinFolder);

    public string? FindAlbum(string foreignAlbumId, string title, string artist)
    {
        if (string.IsNullOrEmpty(title))
        {
            return KnownMbids.Contains(foreignAlbumId) ? ItemId : null;
        }

        if (KnownTitles.Contains(title))
        {
            return ItemId;
        }

        if (!AlbumPresent)
        {
            return null;
        }

        return ++_lookups > MissesBeforeFound ? ItemId : null;
    }

    public void ReportPathChanged(string path) => Reported.Add(path);

    public bool MarkFavorite(string userId, string itemId)
    {
        if (FavoriteFails)
        {
            return false;
        }

        Favorites.Add((userId, itemId));
        return true;
    }

    public JellyfinUserInfo? GetUser(string userId) => Users.TryGetValue(userId, out var n) ? new JellyfinUserInfo(userId, n) : null;
}

internal sealed class FakeNotifier : INotifier
{
    public List<(string UserId, string Body, IReadOnlyDictionary<string, object> Extra)> Sent { get; } = new();

    public bool Result { get; set; } = true;

    public bool Enabled => true;

    public Task<bool> NotifyAsync(string userId, string body, IReadOnlyDictionary<string, object> extra, CancellationToken ct)
    {
        Sent.Add((userId, body, extra));
        return Task.FromResult(Result);
    }
}

internal sealed class TestEnv : IDisposable
{
    public const string User = "0000000000000000000000000000aaaa";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sillon-tests-" + Guid.NewGuid().ToString("N"));

    public TestEnv(FulfillmentMode mode = FulfillmentMode.Lidarr)
    {
        Directory.CreateDirectory(_dir);
        Config = new PluginConfiguration
        {
            FulfillmentMode = mode,
            LidarrUrl = "http://lidarr:8686",
            LidarrApiKey = "lk",
            LidarrQualityProfileId = 2,
            LidarrMetadataProfileId = 1,
            LidarrRootFolder = "/data/media/music",
            WebhookSecret = "s3cret"
        };
        Now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Store = new RequestStore(_dir, () => Now);
        Devices = new DeviceStore(_dir, () => Now);
        Service = BuildService();
    }

    public string DataDir => _dir;

    public PluginConfiguration Config { get; }

    public DateTimeOffset Now { get; set; }

    public FakeLidarr Lidarr { get; } = new();

    public FakeLibrary Library { get; } = new();

    public FakeNotifier Notifier { get; } = new();

    public RequestStore Store { get; }

    public DeviceStore Devices { get; }

    public RequestService Service { get; }

    public static NewRequest Input(string user = User, string album = "alb-1")
        => new(user, "name-" + user, album, "artist-1", "XEU", "Vald", 2022, "https://img/xeu.jpg");

    public RequestService BuildService(RequestTiming? timing = null)
        => new(
            Store,
            Lidarr,
            Library,
            Notifier,
            () => Config,
            NullLogger<RequestService>.Instance,
            timing ?? new RequestTiming
            {
                PollEvery = TimeSpan.FromMilliseconds(1),
                PollMax = TimeSpan.FromMilliseconds(200),
                ResumePollMax = TimeSpan.FromMilliseconds(50),
                RecheckDelay = TimeSpan.FromSeconds(-1),
                RefreshPollEvery = TimeSpan.FromMilliseconds(1),
                RefreshWaitMax = TimeSpan.FromMilliseconds(20)
            },
            () => Now);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
