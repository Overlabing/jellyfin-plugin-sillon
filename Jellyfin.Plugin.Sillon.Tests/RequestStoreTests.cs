using Jellyfin.Plugin.Sillon.Storage;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class RequestStoreTests
{
    [Fact]
    public void Create_AssignsIncrementingIds_AndPersistsAcrossReloads()
    {
        using var env = new TestEnv();
        var first = env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = "a" });
        var second = env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = "b" });

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(RequestStatus.Requested, first.Status);
        Assert.Equal(env.Now, first.CreatedAt);

        var reloaded = new RequestStore(env.DataDir);
        Assert.Equal(new long[] { 2, 1 }, reloaded.List(null).Select(r => r.Id));
        Assert.Equal("b", reloaded.Get(2)!.ForeignAlbumId);
        Assert.Equal(3, reloaded.Create(new RequestRecord { ForeignAlbumId = "c" }).Id);
    }

    [Fact]
    public void Writes_AreAtomic_NoTemporaryFileIsLeftBehind()
    {
        using var env = new TestEnv();
        env.Store.Create(new RequestRecord { ForeignAlbumId = "a" });

        Assert.True(File.Exists(Path.Combine(env.DataDir, "requests.json")));
        Assert.False(File.Exists(Path.Combine(env.DataDir, "requests.json.tmp")));
    }

    [Fact]
    public void Create_RejectsALiveOrAvailableDuplicate_ButNotAFailedOrCancelledOne()
    {
        using var env = new TestEnv();
        var first = env.Store.Create(new RequestRecord { ForeignAlbumId = "a" });
        Assert.Throws<ActiveRequestException>(() => env.Store.Create(new RequestRecord { ForeignAlbumId = "a" }));

        env.Store.Transition(first.Id, RequestStatus.Live, RequestStatus.Available, string.Empty);
        Assert.Throws<ActiveRequestException>(() => env.Store.Create(new RequestRecord { ForeignAlbumId = "a" }));

        var other = env.Store.Create(new RequestRecord { ForeignAlbumId = "b" });
        env.Store.Transition(other.Id, RequestStatus.Live, RequestStatus.Failed, "x");
        env.Store.Create(new RequestRecord { ForeignAlbumId = "b" });
        var third = env.Store.Create(new RequestRecord { ForeignAlbumId = "c" });
        env.Store.Transition(third.Id, RequestStatus.Live, RequestStatus.Cancelled, string.Empty);
        env.Store.Create(new RequestRecord { ForeignAlbumId = "c" });
    }

    [Fact]
    public void Transition_OnlyWinsFromAnAllowedState()
    {
        using var env = new TestEnv();
        var r = env.Store.Create(new RequestRecord { ForeignAlbumId = "a" });

        Assert.True(env.Store.Transition(r.Id, new[] { RequestStatus.Requested }, RequestStatus.Downloading, string.Empty));
        Assert.False(env.Store.Transition(r.Id, new[] { RequestStatus.Requested }, RequestStatus.Downloading, string.Empty));
        Assert.True(env.Store.Transition(r.Id, RequestStatus.Live, RequestStatus.Available, string.Empty));
        Assert.False(env.Store.Transition(r.Id, RequestStatus.Live, RequestStatus.Cancelled, string.Empty));
        Assert.False(env.Store.Transition(99, RequestStatus.Live, RequestStatus.Cancelled, string.Empty));
        Assert.Equal(RequestStatus.Available, env.Store.Get(r.Id)!.Status);
    }

    [Fact]
    public void Newest_ACancelledRowHidesTheAlbum_AFailedOneShows()
    {
        using var env = new TestEnv();
        var a = env.Store.Create(new RequestRecord { ForeignAlbumId = "a" });
        env.Store.Transition(a.Id, RequestStatus.Live, RequestStatus.Failed, "x");
        var b = env.Store.Create(new RequestRecord { ForeignAlbumId = "b" });
        env.Store.Transition(b.Id, RequestStatus.Live, RequestStatus.Cancelled, string.Empty);

        var newest = env.Store.Newest(new[] { "a", "b", "c" });

        Assert.Equal(RequestStatus.Failed, newest["a"].Status);
        Assert.False(newest.ContainsKey("b"));
        Assert.False(newest.ContainsKey("c"));
    }

    [Fact]
    public void List_FiltersByUser_NewestFirst()
    {
        using var env = new TestEnv();
        env.Store.Create(new RequestRecord { UserId = "u1", ForeignAlbumId = "a" });
        env.Store.Create(new RequestRecord { UserId = "u2", ForeignAlbumId = "b" });
        env.Store.Create(new RequestRecord { UserId = "u1", ForeignAlbumId = "c" });

        Assert.Equal(new long[] { 3, 1 }, env.Store.List("u1").Select(r => r.Id));
        Assert.Equal(3, env.Store.List(null).Count);
    }

    [Fact]
    public void ListUnsettled_SelectsAvailableRequestsNotYetNotifiedOrFavourited()
    {
        using var env = new TestEnv();
        var done = env.Store.Create(new RequestRecord { ForeignAlbumId = "a" });
        var half = env.Store.Create(new RequestRecord { ForeignAlbumId = "b" });
        var old = env.Store.Create(new RequestRecord { ForeignAlbumId = "c" });
        foreach (var r in new[] { done, half, old })
        {
            env.Store.Transition(r.Id, RequestStatus.Live, RequestStatus.Available, string.Empty);
        }

        env.Store.MarkLibraryAdded(done.Id);
        env.Store.MarkNotified(done.Id);
        env.Store.MarkNotified(half.Id);

        Assert.Equal(new long[] { 2, 3 }, env.Store.ListUnsettled(env.Now.AddHours(-1)).Select(r => r.Id));
        Assert.Empty(env.Store.ListUnsettled(env.Now.AddHours(1)));
    }

    [Fact]
    public void AnUnreadableFile_IsMovedAside_AndTheStoreStartsEmpty()
    {
        using var env = new TestEnv();
        File.WriteAllText(Path.Combine(env.DataDir, "requests.json"), "{not json");

        var store = new RequestStore(env.DataDir);

        Assert.Empty(store.List(null));
        Assert.True(File.Exists(Path.Combine(env.DataDir, "requests.json.corrupt")));
    }

    [Fact]
    public void Devices_UpsertMovesATokenToItsLatestUser_AndDeleteChecksTheOwner()
    {
        using var env = new TestEnv();
        env.Devices.Upsert("abc", "u1", "ios", "development");
        var created = env.Devices.For("u1").Single().CreatedAt;
        env.Now = env.Now.AddMinutes(1);
        env.Devices.Upsert("abc", "u2", "ios", "production");

        Assert.Empty(env.Devices.For("u1"));
        var device = env.Devices.For("u2").Single();
        Assert.Equal("production", device.Env);
        Assert.Equal(created, device.CreatedAt);

        env.Devices.Delete("abc", "u1");
        Assert.Single(env.Devices.For("u2"));
        env.Devices.Delete("abc", "u2");
        Assert.Empty(env.Devices.For("u2"));

        env.Devices.Upsert("def", "u1", "ios", string.Empty);
        Assert.Equal("def", new DeviceStore(env.DataDir).For("u1").Single().Token);
    }
}
