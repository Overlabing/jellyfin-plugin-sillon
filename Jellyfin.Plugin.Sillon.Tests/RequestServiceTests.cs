using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class RequestServiceTests
{
    private const string Folder = "/data/media/music/Vald/XEU (2022)";

    private static async Task<(TestEnv Env, RequestRecord Request)> Requested(TestEnv? existing = null)
    {
        var env = existing ?? new TestEnv();
        var request = await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);
        return (env, request);
    }

    [Fact]
    public async Task Create_InLidarrMode_AddsTheAlbumUnmonitoredArtist_MonitorsAndSearches()
    {
        var (env, request) = await Requested();
        using var _ = env;

        Assert.Equal(RequestStatus.Requested, request.Status);
        Assert.Equal(101, request.LidarrAlbumId);
        var added = Assert.Single(env.Lidarr.Added);
        Assert.Equal("alb-1", added.ForeignAlbumId);
        Assert.True(added.Monitored);
        Assert.False(added.Artist.Monitored);
        Assert.Equal("none", added.Artist.MonitorNewItems);
        Assert.Equal("none", added.Artist.AddOptions.Monitor);
        Assert.Equal(2, added.Artist.QualityProfileId);
        Assert.Equal(1, added.Artist.MetadataProfileId);
        Assert.Equal("/data/media/music", added.Artist.RootFolderPath);
        Assert.True(env.Lidarr.Monitored[101]);
        Assert.Equal(new[] { 101 }, env.Lidarr.Searched);
    }

    [Fact]
    public async Task Create_WithAnAllowList_RefusesAnUnlistedAlbum_WithoutARecord_AndAcceptsAListedOne()
    {
        using var env = new TestEnv();
        env.Config.CatalogAllowList = "ALB-2";

        var ex = await Assert.ThrowsAsync<AlbumNotAllowedException>(() => env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None));

        Assert.Equal("Cet album n'est pas proposé sur ce serveur.", ex.Message);
        Assert.Empty(env.Store.Newest(new[] { "alb-1" }));
        Assert.Empty(env.Lidarr.Added);

        var created = await env.Service.CreateAsync(TestEnv.Input(album: "alb-2"), CancellationToken.None);
        Assert.Equal("alb-2", created.ForeignAlbumId);
    }

    [Fact]
    public async Task Create_WaitsForLidarrsRefresh_ThenMonitorsAndSearchesInOrder()
    {
        using var env = new TestEnv();
        env.Lidarr.OnGet = (id, n) => new LidarrAlbum { Id = id, Duration = n < 3 ? 0 : 240000, Statistics = new LidarrStats { TrackCount = n < 3 ? 0 : 12 } };

        await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);

        Assert.Equal(new[] { "get", "get", "get", "monitor", "search" }, env.Lidarr.Calls);
    }

    [Fact]
    public async Task Create_WhenLidarrNeverFinishesRefreshing_StillMonitorsAndSearches()
    {
        using var env = new TestEnv();
        env.Lidarr.OnGet = (id, _) => new LidarrAlbum { Id = id, Duration = 0 };

        await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);

        Assert.Equal(new[] { "monitor", "search" }, env.Lidarr.Calls.Where(c => c != "get"));
        Assert.True(env.Lidarr.Calls.Count(c => c == "get") > 1);
        Assert.Equal(new[] { 101 }, env.Lidarr.Searched);
    }

    [Fact]
    public async Task Reconcile_AnUnmonitoredLiveRequest_IsMonitoredAndSearchedAgain_AtMostEverySixHours()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Lidarr.Albums[101] = new LidarrAlbum { Id = 101, Monitored = false, Duration = 1000 };
        env.Lidarr.Calls.Clear();
        env.Lidarr.Searched.Clear();

        // too soon after the first search
        env.Now = env.Now.AddMinutes(5);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Empty(env.Lidarr.Searched);

        env.Now = env.Now.AddMinutes(6);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(new[] { 101 }, env.Lidarr.Searched);
        Assert.True(env.Lidarr.Monitored[101]);
        Assert.NotNull(env.Store.Get(request.Id)!.LastReSearchAt);

        // throttled for six hours
        env.Now = env.Now.AddHours(5);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Single(env.Lidarr.Searched);

        env.Now = env.Now.AddHours(1.1);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, env.Lidarr.Searched.Count);
    }

    [Fact]
    public async Task Reconcile_AMonitoredAlbumWithAGrabInTheQueue_IsNotSearchedAgain()
    {
        var (env, _) = await Requested();
        using var _ = env;
        env.Lidarr.Albums[101] = new LidarrAlbum { Id = 101, Monitored = false };
        env.Lidarr.Queue = new List<LidarrQueueRecord> { new() { AlbumId = 101 } };
        env.Lidarr.Searched.Clear();
        env.Now = env.Now.AddMinutes(30);

        await env.Service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(env.Lidarr.Searched);
    }

    [Fact]
    public async Task Create_ForAnAlbumLidarrAlreadyHolds_DoesNotAddItAgain()
    {
        using var env = new TestEnv();
        env.Lidarr.Existing["alb-1"] = new LidarrAlbum { Id = 7, ForeignAlbumId = "alb-1" };

        var request = await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);

        Assert.Empty(env.Lidarr.Added);
        Assert.Equal(7, request.LidarrAlbumId);
        Assert.True(env.Lidarr.Monitored[7]);
        Assert.Equal(new[] { 7 }, env.Lidarr.Searched);
        Assert.Equal(new[] { 7 }, env.Lidarr.AnyReleaseOk);
    }

    [Fact]
    public async Task Create_AddsTheAlbumAcceptingAnyRelease()
    {
        var (env, _) = await Requested();
        using var _ = env;

        Assert.True(env.Lidarr.Added[0].AnyReleaseOk);
    }

    private static LidarrQueueRecord FailedImport(int albumId, string message = "Album release not requested")
        => new()
        {
            AlbumId = albumId,
            TrackedDownloadState = "importFailed",
            OutputPath = "/downloads/The Wall",
            StatusMessages = new() { new LidarrStatusMessage { Title = "x", Messages = new() { message } } }
        };

    [Fact]
    public async Task Reconcile_ARejectedEdition_AcceptsAnyReleaseAndRescans_AtMostEveryThirtyMinutes()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Lidarr.AnyReleaseOk.Clear();
        env.Lidarr.Queue = new() { FailedImport(101) };

        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(new[] { 101 }, env.Lidarr.AnyReleaseOk);
        Assert.Equal(new[] { "/downloads/The Wall" }, env.Lidarr.Scans);
        Assert.NotNull(env.Store.Get(request.Id)!.LastImportRetryAt);

        env.Now = env.Now.AddMinutes(29);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Single(env.Lidarr.Scans);

        env.Now = env.Now.AddMinutes(2);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, env.Lidarr.Scans.Count);
    }

    [Fact]
    public async Task Reconcile_AnotherImportFailure_IsNotRetried()
    {
        var (env, _) = await Requested();
        using var _ = env;
        env.Lidarr.Queue = new() { FailedImport(101, "Has missing tracks") };

        await env.Service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(env.Lidarr.Scans);
    }

    [Fact]
    public async Task Create_WhenLidarrRefuses_KeepsAFailedRequestWithAGenericError_AndLeaksNothing()
    {
        using var env = new TestEnv();
        env.Lidarr.AddError = new LidarrException("SECRET-DETAIL /data/media/music");

        var ex = await Assert.ThrowsAsync<LidarrAddException>(() => env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None));

        Assert.Equal("ajout Lidarr refusé", ex.Message);
        var stored = Assert.Single(env.Store.List(null));
        Assert.Equal(RequestStatus.Failed, stored.Status);
        Assert.Equal("ajout Lidarr refusé", stored.Error);
        Assert.DoesNotContain("SECRET", stored.Error);
        // a failed request does not block asking again
        env.Lidarr.AddError = null;
        await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);
    }

    [Fact]
    public async Task Create_ADuplicateOfALiveRequest_IsRefused()
    {
        var (env, _) = await Requested();
        using var _ = env;

        await Assert.ThrowsAsync<ActiveRequestException>(() => env.Service.CreateAsync(TestEnv.Input("0000000000000000000000000000bbbb"), CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_RequestedToDownloadingToAvailable_FavouritesTheAlbumAndRecordsIt()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;

        env.Service.OnGrab(new[] { "alb-1" });
        Assert.Equal(RequestStatus.Downloading, env.Store.Get(request.Id)!.Status);

        var outcome = await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Contains("downloading -> available", Assert.Single(outcome));
        var done = env.Store.Get(request.Id)!;
        Assert.Equal(RequestStatus.Available, done.Status);
        Assert.Equal(env.Library.ItemId, done.JellyfinItemId);
        Assert.NotNull(done.LibraryAddedAt);
        Assert.NotNull(done.NotifiedAt);
        Assert.Equal("/data/music/Vald/XEU (2022)", Assert.Single(env.Library.Reported));
        Assert.Equal((TestEnv.User, env.Library.ItemId), Assert.Single(env.Library.Favorites));
        var sent = Assert.Single(env.Notifier.Sent);
        Assert.Equal("XEU de Vald est maintenant dans la bibliothèque", sent.Body);
        Assert.Equal(env.Library.ItemId, sent.Extra["album_id"]);
        Assert.Equal(request.Id, sent.Extra["request_id"]);
    }

    [Fact]
    public async Task Import_IsIdempotent_ASecondEventDoesNothing()
    {
        var (env, _) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();
        var second = await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal("alb-1: no active request", Assert.Single(second));
        Assert.Single(env.Notifier.Sent);
        Assert.Single(env.Library.Favorites);
    }

    [Fact]
    public async Task Import_PollsTheLibraryUntilTheAlbumAppears()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;
        env.Library.MissesBeforeFound = 3;

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(env.Library.ItemId, env.Store.Get(request.Id)!.JellyfinItemId);
        Assert.Single(env.Library.Favorites);
    }

    [Fact]
    public async Task Import_FindsAnAlbumTheLibraryRenamedAndHasNoMusicBrainzIdFor_ByItsFolder()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Lidarr.Files[101] = new List<LidarrTrackFile>
        {
            new() { Path = Folder + "/CD 01/01 a.flac" },
            new() { Path = Folder + "/CD 02/01 b.flac" }
        };
        env.Library.KnownFolders["/data/music/Vald/XEU (2022)"] = env.Library.ItemId;

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        var done = env.Store.Get(request.Id)!;
        Assert.Equal(env.Library.ItemId, done.JellyfinItemId);
        Assert.Equal(Folder, done.LidarrFolder);
        Assert.Single(env.Library.Favorites);
        Assert.Empty(env.Library.Refreshed);
    }

    [Fact]
    public async Task Import_PrefersTheMusicBrainzIdToTheFolder()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.KnownMbids.Add("alb-1");
        env.Library.KnownFolders["/data/music/Vald/XEU (2022)"] = "ffffffffffffffffffffffffffffffff";
        env.Lidarr.Files[101] = new List<LidarrTrackFile> { new() { Path = Folder + "/01 a.flac" } };

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(env.Library.ItemId, env.Store.Get(request.Id)!.JellyfinItemId);
    }

    [Fact]
    public async Task Import_WhenTheLibraryDoesNotListTheFolder_AsksJellyfinToScanIt_OncePerTenMinutes()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Lidarr.Files[101] = new List<LidarrTrackFile> { new() { Path = Folder + "/01 a.flac" } };

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal("/data/music/Vald/XEU (2022)", Assert.Single(env.Library.Refreshed));
        Assert.NotNull(env.Store.Get(request.Id)!.LastFolderRefreshAt);

        env.Now += TimeSpan.FromMinutes(11);
        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(2, env.Library.Refreshed.Count);
    }

    [Fact]
    public async Task Reconcile_QueuesTheCutoffUnmetSearch_OnceAWeek()
    {
        using var env = new TestEnv();

        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(1, env.Lidarr.CutoffSearches);

        env.Now += TimeSpan.FromDays(6);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(1, env.Lidarr.CutoffSearches);

        env.Now += TimeSpan.FromDays(2);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, env.Lidarr.CutoffSearches);
        Assert.Equal(env.Now, new RequestStore(env.DataDir, () => env.Now).LastCutoffSearchAt);
    }

    [Fact]
    public async Task Reconcile_InManualMode_DoesNotQueueTheCutoffUnmetSearch()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);

        await env.Service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, env.Lidarr.CutoffSearches);
    }

    [Fact]
    public async Task Import_WhenTheLibraryNeverShowsTheAlbum_StillNotifiesWithoutALink_AndNeverFavourites()
    {
        var (env, request) = await Requested();
        using var _ = env;

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        var done = env.Store.Get(request.Id)!;
        Assert.Equal(string.Empty, done.JellyfinItemId);
        Assert.Null(done.LibraryAddedAt);
        Assert.NotNull(done.NotifiedAt);
        Assert.Empty(env.Library.Favorites);
        Assert.False(Assert.Single(env.Notifier.Sent).Extra.ContainsKey("album_id"));
    }

    [Fact]
    public async Task Import_OfAPartialAlbum_IsAvailableWithTheCounts()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;
        env.Lidarr.Albums[101] = new LidarrAlbum
        {
            Id = 101,
            Statistics = new LidarrStats { TrackFileCount = 16, TrackCount = 17, PercentOfTracks = 94.1 }
        };

        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        var done = env.Store.Get(request.Id)!;
        Assert.Equal(RequestStatus.Available, done.Status);
        Assert.Equal((16, 17), (done.TracksImported, done.TracksTotal));
        Assert.Equal("XEU de Vald est maintenant dans la bibliothèque (16/17 titres)", Assert.Single(env.Notifier.Sent).Body);
    }

    [Fact]
    public async Task ImportFailure_WithoutFiles_RecordsAGenericError_AndKeepsTheStatus()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Service.OnGrab(new[] { "alb-1" });

        await env.Service.OnFailureAsync(new[] { "alb-1" }, "Import failed, missing tracks", CancellationToken.None);

        var stored = env.Store.Get(request.Id)!;
        Assert.Equal(RequestStatus.Downloading, stored.Status);
        Assert.Equal("import échoué", stored.Error);
        Assert.Empty(env.Notifier.Sent);
    }

    [Fact]
    public async Task ImportFailure_WithImportedFilesAndAnEmptyQueue_IsAvailable()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;
        env.Lidarr.Albums[101] = new LidarrAlbum
        {
            Id = 101,
            Statistics = new LidarrStats { TrackFileCount = 16, TrackCount = 17 },
            Artist = new LidarrArtist { Path = "/data/media/music/Vald" }
        };
        env.Lidarr.Files[101] = new List<LidarrTrackFile> { new() { Path = Folder + "/t.flac" } };

        var outcome = await env.Service.OnFailureAsync(new[] { "alb-1" }, "missing tracks", CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(RequestStatus.Available, env.Store.Get(request.Id)!.Status);
        Assert.Contains("available (16/17", Assert.Single(outcome));
        Assert.Equal("/data/music/Vald/XEU (2022)", Assert.Single(env.Library.Reported));
    }

    [Fact]
    public async Task ImportFailure_WhileTheDownloadIsStillQueued_StaysLiveWithTheError()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Lidarr.Albums[101] = new LidarrAlbum { Id = 101, Statistics = new LidarrStats { TrackFileCount = 16, TrackCount = 17 } };
        env.Lidarr.Queue = new List<LidarrQueueRecord> { new() { AlbumId = 101 } };

        await env.Service.OnFailureAsync(new[] { "alb-1" }, "missing tracks", CancellationToken.None);

        var stored = env.Store.Get(request.Id)!;
        Assert.Equal(RequestStatus.Requested, stored.Status);
        Assert.Equal("import échoué", stored.Error);
    }

    [Fact]
    public async Task Cancel_UnmonitorsInLidarr_AndFollowsTheOwnershipRules()
    {
        var (env, request) = await Requested();
        using var _ = env;

        await Assert.ThrowsAsync<RequestForbiddenException>(() => env.Service.CancelAsync(request.Id, "someone-else", false, CancellationToken.None));
        await Assert.ThrowsAsync<RequestNotFoundException>(() => env.Service.CancelAsync(99, TestEnv.User, false, CancellationToken.None));

        var cancelled = await env.Service.CancelAsync(request.Id, "an-admin", true, CancellationToken.None);

        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);
        Assert.False(env.Lidarr.Monitored[101]);
        await Assert.ThrowsAsync<RequestNotActiveException>(() => env.Service.CancelAsync(request.Id, TestEnv.User, false, CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_OfAnAvailableRequest_IsRefused()
    {
        var (env, request) = await Requested();
        using var _ = env;
        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();

        await Assert.ThrowsAsync<RequestNotActiveException>(() => env.Service.CancelAsync(request.Id, TestEnv.User, false, CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_WhenLidarrIsDown_KeepsTheRequestLive()
    {
        var (env, request) = await Requested();
        using var _ = env;
        var broken = new BrokenLidarr();
        var down = new RequestService(
            env.Store,
            broken,
            env.Library,
            env.Notifier,
            () => env.Config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RequestService>.Instance);

        await Assert.ThrowsAsync<LidarrUnavailableException>(() => down.CancelAsync(request.Id, TestEnv.User, false, CancellationToken.None));

        Assert.Equal(RequestStatus.Requested, env.Store.Get(request.Id)!.Status);
    }

    [Fact]
    public async Task Manual_RequestIsStoredWithoutCallingLidarr_AndTheAdminMarksItAvailable()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        env.Library.AlbumPresent = true;

        var request = await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);

        Assert.Equal(RequestStatus.Requested, request.Status);
        Assert.Null(request.LidarrAlbumId);
        Assert.Empty(env.Lidarr.Added);
        Assert.Empty(env.Lidarr.Searched);

        var available = env.Service.MarkAvailableByHand(request.Id);
        await env.Service.WaitAsync();

        Assert.Equal(RequestStatus.Available, available.Status);
        var done = env.Store.Get(request.Id)!;
        Assert.NotNull(done.LibraryAddedAt);
        Assert.Equal((TestEnv.User, env.Library.ItemId), Assert.Single(env.Library.Favorites));
        Assert.Empty(env.Library.Reported);
        Assert.Single(env.Notifier.Sent);
        Assert.Throws<RequestNotActiveException>(() => env.Service.MarkAvailableByHand(request.Id));
        Assert.Throws<RequestNotFoundException>(() => env.Service.MarkAvailableByHand(99));
    }

    [Fact]
    public async Task Manual_ReconcileNeverRetriesTheAdd_NorTimesTheRequestOut()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        var request = await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);
        env.Now = env.Now.AddDays(60);

        await env.Service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.Requested, env.Store.Get(request.Id)!.Status);
        Assert.Empty(env.Lidarr.Added);
    }

    [Fact]
    public async Task Reconcile_AFullyImportedAlbum_BecomesAvailable()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;
        env.Lidarr.Albums[101] = new LidarrAlbum
        {
            Id = 101,
            Statistics = new LidarrStats { PercentOfTracks = 100, TrackFileCount = 12, TrackCount = 12 },
            Artist = new LidarrArtist { Path = "/data/media/music/Vald" }
        };

        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(RequestStatus.Available, env.Store.Get(request.Id)!.Status);
        Assert.Equal("/data/music/Vald", Assert.Single(env.Library.Reported));
    }

    [Fact]
    public async Task Reconcile_PartialFilesAndAnEmptyQueue_IsAvailable_ButNotWhileQueued()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Lidarr.Albums[101] = new LidarrAlbum { Id = 101, Statistics = new LidarrStats { PercentOfTracks = 94, TrackFileCount = 16, TrackCount = 17 } };
        env.Lidarr.Queue = new List<LidarrQueueRecord> { new() { Album = new LidarrAlbum { Id = 101 } } };

        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Downloading, env.Store.Get(request.Id)!.Status);

        env.Lidarr.Queue = new List<LidarrQueueRecord>();
        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();

        var done = env.Store.Get(request.Id)!;
        Assert.Equal(RequestStatus.Available, done.Status);
        Assert.Equal((16, 17), (done.TracksImported, done.TracksTotal));
    }

    [Fact]
    public async Task Reconcile_PastTheTimeout_FailsTheRequest_AndTellsTheRequester()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Config.RequestTimeoutDays = 14;
        env.Now = env.Now.AddDays(15);

        await env.Service.ReconcileAsync(CancellationToken.None);

        var failed = env.Store.Get(request.Id)!;
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Equal("introuvable après 14 jours", failed.Error);
        Assert.Equal("XEU de Vald est introuvable", Assert.Single(env.Notifier.Sent).Body);
    }

    [Fact]
    public async Task Reconcile_BeforeTheTimeout_LeavesTheRequestAlone()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Now = env.Now.AddDays(13);

        await env.Service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.Requested, env.Store.Get(request.Id)!.Status);
    }

    [Fact]
    public async Task Reconcile_ARequestStillQueuedIsFailedOnlyAtTwiceTheTimeout()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Config.RequestTimeoutDays = 10;
        env.Lidarr.Queue = new List<LidarrQueueRecord> { new() { AlbumId = 101 } };

        env.Now = env.Now.AddDays(15);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Downloading, env.Store.Get(request.Id)!.Status);

        env.Now = env.Now.AddDays(6);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(RequestStatus.Failed, env.Store.Get(request.Id)!.Status);
    }

    [Fact]
    public async Task Reconcile_WhenTheQueueCannotBeRead_DoesNotTimeOutARequest()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Now = env.Now.AddDays(20);
        var service = new RequestService(
            env.Store,
            new BrokenLidarr(queueOnly: true, inner: env.Lidarr),
            env.Library,
            env.Notifier,
            () => env.Config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RequestService>.Instance,
            now: () => env.Now);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(RequestStatus.Requested, env.Store.Get(request.Id)!.Status);
    }

    [Fact]
    public async Task Reconcile_RetriesAnAddThatNeverHappened_AfterTenMinutes()
    {
        using var env = new TestEnv();
        var orphan = env.Store.Create(new RequestRecord { UserId = TestEnv.User, ForeignAlbumId = "alb-9", ForeignArtistId = "artist-1", Title = "T", Artist = "A" });

        env.Now = env.Now.AddMinutes(9);
        await env.Service.ReconcileAsync(CancellationToken.None);
        Assert.Empty(env.Lidarr.Added);

        env.Now = env.Now.AddMinutes(2);
        await env.Service.ReconcileAsync(CancellationToken.None);

        Assert.Single(env.Lidarr.Added);
        Assert.Equal(101, env.Store.Get(orphan.Id)!.LidarrAlbumId);
        Assert.Equal(RequestStatus.Requested, env.Store.Get(orphan.Id)!.Status);
    }

    [Fact]
    public async Task Reconcile_ASecondFailedAdd_EndsTheRequest()
    {
        using var env = new TestEnv();
        var orphan = env.Store.Create(new RequestRecord { UserId = TestEnv.User, ForeignAlbumId = "alb-9", ForeignArtistId = "artist-1" });
        env.Lidarr.AddError = new LidarrException("boom");
        env.Now = env.Now.AddMinutes(11);

        await env.Service.ReconcileAsync(CancellationToken.None);

        var stored = env.Store.Get(orphan.Id)!;
        Assert.Equal(RequestStatus.Failed, stored.Status);
        Assert.Equal("ajout Lidarr refusé", stored.Error);
    }

    [Fact]
    public async Task Reconcile_CatchesUpAnAvailableRequestThatNeverReachedTheLibrary()
    {
        var (env, request) = await Requested();
        using var _ = env;
        // the plugin stopped right after the import: available, but nothing finalised
        env.Store.Transition(request.Id, RequestStatus.Live, RequestStatus.Available, string.Empty);
        env.Library.AlbumPresent = true;

        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();

        var done = env.Store.Get(request.Id)!;
        Assert.NotNull(done.LibraryAddedAt);
        Assert.NotNull(done.NotifiedAt);
        Assert.Equal((TestEnv.User, env.Library.ItemId), Assert.Single(env.Library.Favorites));

        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();
        Assert.Single(env.Library.Favorites);
        Assert.Single(env.Notifier.Sent);
    }

    [Fact]
    public async Task Reconcile_GivesUpOnAvailableRequestsOlderThanTheResumeWindow()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Store.Transition(request.Id, RequestStatus.Live, RequestStatus.Available, string.Empty);
        env.Library.AlbumPresent = true;
        env.Now = env.Now.AddDays(2);

        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Empty(env.Library.Favorites);
    }

    [Fact]
    public async Task ANotifierThatReportsATransientFailure_IsRetriedByTheNextReconcile()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Library.AlbumPresent = true;
        env.Notifier.Result = false;
        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);
        await env.Service.WaitAsync();
        Assert.Null(env.Store.Get(request.Id)!.NotifiedAt);

        env.Notifier.Result = true;
        await env.Service.ReconcileAsync(CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.NotNull(env.Store.Get(request.Id)!.NotifiedAt);
        Assert.Equal(2, env.Notifier.Sent.Count);
        Assert.Single(env.Library.Favorites);
    }

    [Fact]
    public async Task Grab_ForAnUnknownAlbum_IsIgnored()
    {
        var (env, request) = await Requested();
        using var _ = env;

        var outcome = env.Service.OnGrab(new[] { "nobody-asked" });

        Assert.Equal("nobody-asked: no active request", Assert.Single(outcome));
        Assert.Equal(RequestStatus.Requested, env.Store.Get(request.Id)!.Status);
    }

    [Fact]
    public async Task Create_RecordsWhenLidarrWasAskedToSearch_ManualNeverDoes()
    {
        var (env, request) = await Requested();
        using var _ = env;
        using var manual = new TestEnv(FulfillmentMode.Manual);

        Assert.Equal(env.Now, env.Store.Get(request.Id)!.LastSearchAt);
        var manualRequest = await manual.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);
        Assert.Null(manualRequest.LastSearchAt);
    }

    [Fact]
    public async Task Retry_OfAFailedRequest_SearchesAgain_AndGoesBackToRequested()
    {
        using var env = new TestEnv();
        env.Lidarr.AddError = new LidarrException("down");
        await Assert.ThrowsAsync<LidarrAddException>(() => env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None));
        env.Lidarr.AddError = null;
        env.Now = env.Now.AddMinutes(5);

        var retried = await env.Service.RetryAsync(1, TestEnv.User, false, CancellationToken.None);

        Assert.Equal(RequestStatus.Requested, retried.Status);
        Assert.Equal(string.Empty, retried.Error);
        Assert.Equal(101, retried.LidarrAlbumId);
        Assert.Equal(env.Now, retried.LastSearchAt);
        Assert.Equal(env.Now, retried.UpdatedAt);
        Assert.Equal(new[] { 101 }, env.Lidarr.Searched);
    }

    [Fact]
    public async Task Retry_OfALiveRequest_SearchesAgain_AndKeepsItsStatus_ReAddingAVanishedAlbum()
    {
        var (env, request) = await Requested();
        using var _ = env;
        env.Service.OnGrab(new[] { "alb-1" });
        env.Lidarr.Searched.Clear();
        env.Now = env.Now.AddMinutes(5);

        var retried = await env.Service.RetryAsync(request.Id, "an-admin", true, CancellationToken.None);

        Assert.Equal(RequestStatus.Downloading, retried.Status);
        Assert.Equal(env.Now, retried.LastSearchAt);
        Assert.Equal(2, env.Lidarr.Added.Count);
        Assert.Single(env.Lidarr.Searched);
    }

    [Fact]
    public async Task Retry_FollowsTheOwnershipAndStateRules()
    {
        var (env, request) = await Requested();
        using var _ = env;

        await Assert.ThrowsAsync<RequestForbiddenException>(() => env.Service.RetryAsync(request.Id, "someone-else", false, CancellationToken.None));
        await Assert.ThrowsAsync<RequestNotFoundException>(() => env.Service.RetryAsync(99, TestEnv.User, false, CancellationToken.None));
        await env.Service.CancelAsync(request.Id, TestEnv.User, false, CancellationToken.None);
        await Assert.ThrowsAsync<RequestNotRetryableException>(() => env.Service.RetryAsync(request.Id, TestEnv.User, false, CancellationToken.None));
    }

    [Fact]
    public async Task Retry_OfAnAvailableRequest_IsRefused()
    {
        var (env, request) = await Requested();
        using var _ = env;
        await env.Service.OnImportAsync(new[] { "alb-1" }, new[] { Folder }, CancellationToken.None);

        await Assert.ThrowsAsync<RequestNotRetryableException>(() => env.Service.RetryAsync(request.Id, TestEnv.User, false, CancellationToken.None));
    }

    [Fact]
    public async Task Retry_InManualMode_IsRefusedInFrench()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        var request = await env.Service.CreateAsync(TestEnv.Input(), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<RequestNotRetryableException>(() => env.Service.RetryAsync(request.Id, TestEnv.User, false, CancellationToken.None));

        Assert.Contains("mode manuel", ex.Message);
    }

    [Fact]
    public async Task Retry_WhenLidarrIsDown_KeepsTheRequestAsItWas()
    {
        var (env, request) = await Requested();
        using var _ = env;
        var down = new RequestService(
            env.Store,
            new BrokenLidarr(),
            env.Library,
            env.Notifier,
            () => env.Config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RequestService>.Instance);

        await Assert.ThrowsAsync<LidarrUnavailableException>(() => down.RetryAsync(request.Id, TestEnv.User, false, CancellationToken.None));

        Assert.Equal(request.UpdatedAt, env.Store.Get(request.Id)!.UpdatedAt);
    }

    private sealed class BrokenLidarr : ILidarrProvider
    {
        private readonly bool _queueOnly;
        private readonly ILidarrApi? _inner;

        public BrokenLidarr(bool queueOnly = false, ILidarrApi? inner = null)
        {
            _queueOnly = queueOnly;
            _inner = inner;
        }

        public ILidarrApi Create() => new Api(_queueOnly, _inner);

        private sealed class Api(bool queueOnly, ILidarrApi? inner) : ILidarrApi
        {
            private static LidarrException Down() => new("Lidarr injoignable");

            public Task<IReadOnlyList<LidarrSearchItem>> SearchAsync(string term, CancellationToken ct = default)
                => queueOnly ? inner!.SearchAsync(term, ct) : throw Down();

            public Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct = default)
                => queueOnly ? inner!.LookupAlbumsAsync(term, ct) : throw Down();

            public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct = default)
                => queueOnly ? inner!.GetAlbumsByArtistAsync(artistId, ct) : throw Down();

            public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, CancellationToken ct = default)
                => queueOnly ? inner!.GetAlbumsByForeignIdAsync(foreignAlbumId, ct) : throw Down();

            public Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum album, CancellationToken ct = default)
                => queueOnly ? inner!.AddAlbumAsync(album, ct) : throw Down();

            public Task MonitorAlbumsAsync(IReadOnlyList<int> ids, bool monitored, CancellationToken ct = default)
                => queueOnly ? inner!.MonitorAlbumsAsync(ids, monitored, ct) : throw Down();

            public Task AlbumSearchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
                => queueOnly ? inner!.AlbumSearchAsync(ids, ct) : throw Down();

            public Task<LidarrAlbum> GetAlbumAsync(int id, CancellationToken ct = default)
                => queueOnly ? inner!.GetAlbumAsync(id, ct) : throw Down();

            public Task<IReadOnlyList<LidarrQueueRecord>> GetQueueAsync(CancellationToken ct = default) => throw Down();

            public Task<IReadOnlyList<LidarrTrackFile>> GetTrackFilesAsync(int albumId, CancellationToken ct = default)
                => queueOnly ? inner!.GetTrackFilesAsync(albumId, ct) : throw Down();

            public Task<string?> GetAlbumFolderAsync(int albumId, CancellationToken ct = default)
                => queueOnly ? inner!.GetAlbumFolderAsync(albumId, ct) : throw Down();

            public Task CutoffUnmetSearchAsync(CancellationToken ct = default)
                => queueOnly ? inner!.CutoffUnmetSearchAsync(ct) : throw Down();

            public Task SetAnyReleaseOkAsync(int albumId, CancellationToken ct = default)
                => queueOnly ? inner!.SetAnyReleaseOkAsync(albumId, ct) : throw Down();

            public Task DownloadedAlbumsScanAsync(string path, CancellationToken ct = default)
                => queueOnly ? inner!.DownloadedAlbumsScanAsync(path, ct) : throw Down();
        }
    }
}
