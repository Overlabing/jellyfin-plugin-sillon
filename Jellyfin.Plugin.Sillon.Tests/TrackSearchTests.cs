using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class TrackSearchTests
{
    private const string Radiohead = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
    private const string PabloHoney = "11111111-0000-0000-0000-000000000001";
    private const string CreepSingle = "11111111-0000-0000-0000-000000000002";
    private const string Anthology = "11111111-0000-0000-0000-000000000003";
    private const string LiveAlbum = "11111111-0000-0000-0000-000000000004";
    private const string Recording = "22222222-0000-0000-0000-000000000001";
    private const string CreepEp = "11111111-0000-0000-0000-000000000005";

    private static MbReleaseGroup Group(string id, string type, int year = 1993, params string[] secondary)
        => new(id, "T-" + id[^1], type, secondary, year);

    private static MbRecording Rec(string title, string artist, int score, params MbReleaseGroup[] groups)
        => new(title, artist, score, groups);

    // ---- query parsing

    [Theory]
    [InlineData("Creep", "Creep", null)]
    [InlineData("Radiohead - Creep", "Creep", "Radiohead")]
    [InlineData("Radiohead – Creep", "Creep", "Radiohead")]
    [InlineData("Creep by Radiohead", "Creep", "Radiohead")]
    [InlineData("Lemon Tree par Fools Garden", "Lemon Tree", "Fools Garden")]
    [InlineData("  Bohemian Rhapsody  ", "Bohemian Rhapsody", null)]
    public void Parse_ReadsTheArtistWhenTheTextNamesOne(string text, string title, string? artist)
        => Assert.Equal(new TrackQuery(title, artist), TrackQuery.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    public void Parse_IgnoresTextTooShortForATrack(string text) => Assert.Null(TrackQuery.Parse(text));

    [Fact]
    public void Parse_KeepsATrailingSeparatorAsPartOfTheTitle()
        => Assert.Equal(new TrackQuery("Creep by", null), TrackQuery.Parse("Creep by"));

    [Fact]
    public void ToLucene_QuotesAndEscapes()
    {
        Assert.Equal("recording:\"Creep\"", new TrackQuery("Creep", null).ToLucene());
        Assert.Equal("recording:\"Say \\\"Hi\\\"\" AND artist:\"AC/DC\"", new TrackQuery("Say \"Hi\"", "AC/DC").ToLucene());
    }

    // ---- single filtering and ranking

    [Fact]
    public void Albums_DropsSinglesAndOthers_AndRanksStudioBeforeCompilationLiveAndEp()
    {
        var recordings = new[]
        {
            Rec("Creep", "Radiohead", 100,
                Group(CreepSingle, "Single"),
                Group(Anthology, "Album", 2008, "Compilation"),
                Group(LiveAlbum, "Album", 2001, "Live"),
                Group(CreepEp, "EP"),
                Group(PabloHoney, "Album", 1993),
                Group("11111111-0000-0000-0000-000000000009", "Broadcast")),
        };

        var albums = TrackSearch.Albums(recordings, new TrackQuery("Creep", "Radiohead"), hideEps: false);

        Assert.Equal(new[] { PabloHoney, LiveAlbum, Anthology, CreepEp }, albums.Select(a => a.ReleaseGroupId));
        Assert.Equal(new[] { 0, 2, 2, 3 }, albums.Select(a => a.Rank));
    }

    [Fact]
    public void Albums_HidesEpsOnRequest()
    {
        var recordings = new[] { Rec("Creep", "Radiohead", 100, Group(CreepEp, "EP"), Group(PabloHoney, "Album")) };

        var albums = TrackSearch.Albums(recordings, new TrackQuery("Creep", null), hideEps: true);

        Assert.Equal(PabloHoney, Assert.Single(albums).ReleaseGroupId);
    }

    [Fact]
    public void Albums_MoreRecordingsOnTheSameRank_RankHigher_ThenTheEarlierYear()
    {
        var recordings = new[]
        {
            Rec("Creep", "Radiohead", 100, Group(PabloHoney, "Album", 1993)),
            Rec("Creep", "Radiohead", 95, Group(PabloHoney, "Album", 1993), Group(Anthology, "Album", 1998)),
            Rec("Creep", "Radiohead", 90, Group(Anthology, "Album", 1998), Group(LiveAlbum, "Album", 1990)),
        };

        var albums = TrackSearch.Albums(recordings, new TrackQuery("Creep", null), hideEps: false);

        // 2 hits each for Pablo Honey and Anthology (earlier year first); 1 hit for the live one
        Assert.Equal(new[] { PabloHoney, Anthology, LiveAlbum }, albums.Select(a => a.ReleaseGroupId));
        Assert.Equal(new[] { 2, 2, 1 }, albums.Select(a => a.Hits));
    }

    [Fact]
    public void Albums_IgnoresOtherTitles_OtherArtists_AndWeakScores()
    {
        var recordings = new[]
        {
            Rec("Creep (acoustic)", "Radiohead", 100, Group(LiveAlbum, "Album")),
            Rec("Creep", "TLC", 100, Group(Anthology, "Album")),
            Rec("Creep", "Radiohead", 40, Group(CreepEp, "Album")),
            Rec("creep!", "RADIOHEAD", 90, Group(PabloHoney, "Album")),
        };

        var albums = TrackSearch.Albums(recordings, new TrackQuery("Creep", "radiohead"), hideEps: false);

        Assert.Equal(PabloHoney, Assert.Single(albums).ReleaseGroupId);
    }

    // ---- MusicBrainz client: headers, rate limit, cache, failure

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private const string MbJson = """
        {"recordings":[{"id":"r1","score":100,"title":"Creep","artist-credit":[{"name":"Radiohead","joinphrase":"","artist":{"id":"a","name":"Radiohead"}}],
          "releases":[
            {"id":"x","title":"Pablo Honey","date":"1993-02-22","release-group":{"id":"11111111-0000-0000-0000-000000000001","title":"Pablo Honey","primary-type":"Album"}},
            {"id":"y","title":"Creep","date":"1992-09-21","release-group":{"id":"11111111-0000-0000-0000-000000000002","title":"Creep","primary-type":"Single"}},
            {"id":"z","title":"The Best Of","date":"2008","release-group":{"id":"11111111-0000-0000-0000-000000000003","title":"The Best Of","primary-type":"Album","secondary-types":["Compilation"]}}]}]}
        """;

    private static HttpResponseMessage Ok(string body = MbJson)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public List<TimeSpan> Waits { get; } = new();

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            Waits.Add(span);
            Now += span;
            return Task.CompletedTask;
        }
    }

    private static (MusicBrainzClient Client, FakeHandler Handler, Clock Clock) MakeClient(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new FakeHandler(respond ?? (_ => Ok()));
        var clock = new Clock();
        return (new MusicBrainzClient(new HttpClient(handler), "0.9.0.0", NullLogger.Instance, () => clock.Now, clock.Delay, "https://mb.test"), handler, clock);
    }

    [Fact]
    public async Task Client_SendsAnIdentifyingUserAgent_AndMapsTheRecordings()
    {
        var (client, handler, _) = MakeClient();

        var found = await client.SearchRecordingsAsync(new TrackQuery("Creep", "Radiohead"), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Sillon-Plugin/0.9.0.0 ( https://github.com/Overlabing/jellyfin-plugin-sillon )", request.Headers.UserAgent.ToString());
        Assert.Equal("/ws/2/recording", request.RequestUri!.AbsolutePath);
        Assert.Contains("fmt=json", request.RequestUri.Query);
        Assert.Contains(Uri.EscapeDataString("recording:\"Creep\" AND artist:\"Radiohead\""), request.RequestUri.Query);
        var recording = Assert.Single(found);
        Assert.Equal(("Creep", "Radiohead", 100), (recording.Title, recording.Artist, recording.Score));
        Assert.Equal(new[] { "Album", "Single", "Album" }, recording.ReleaseGroups.Select(g => g.PrimaryType));
        Assert.Equal(new[] { 1993, 1992, 2008 }, recording.ReleaseGroups.Select(g => g.Year));
        Assert.Equal("Compilation", Assert.Single(recording.ReleaseGroups[2].SecondaryTypes));
    }

    [Fact]
    public async Task Client_KeepsOneSecondBetweenRequests()
    {
        var (client, handler, clock) = MakeClient();

        await client.SearchRecordingsAsync(new TrackQuery("Creep", null), CancellationToken.None);
        await client.SearchRecordingsAsync(new TrackQuery("Karma Police", null), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var wait = Assert.Single(clock.Waits);
        Assert.True(wait >= TimeSpan.FromSeconds(1), wait.ToString());
    }

    [Fact]
    public async Task Client_AnswersARepeatedSearchFromTheCache_UntilItExpires()
    {
        var (client, handler, clock) = MakeClient();

        await client.SearchRecordingsAsync(new TrackQuery("Creep", "Radiohead"), CancellationToken.None);
        await client.SearchRecordingsAsync(new TrackQuery("creep", "RADIOHEAD"), CancellationToken.None);
        Assert.Single(handler.Requests);

        clock.Now += TimeSpan.FromHours(25);
        await client.SearchRecordingsAsync(new TrackQuery("Creep", "Radiohead"), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Client_ConcurrentSearches_NeverExceedTheRate()
    {
        var (client, handler, clock) = MakeClient();

        await Task.WhenAll(new[] { "One", "Two", "Three" }.Select(t => client.SearchRecordingsAsync(new TrackQuery(t, null), CancellationToken.None)));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, clock.Waits.Count);
        Assert.All(clock.Waits, w => Assert.True(w >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Client_GarbageJsonIsAFailure_NotAnEmptyAnswer()
    {
        var (client, _, _) = MakeClient(_ => Ok("<html>nope</html>"));

        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchRecordingsAsync(new TrackQuery("Creep", null), CancellationToken.None));
    }

    // ---- the catalogue: hiding singles, resolving the albums, hints, phase

    private sealed class FakeMusicBrainz(IReadOnlyList<MbRecording> recordings) : IMusicBrainzApi
    {
        public Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
            => Task.FromResult(new MbReleaseGroupPage(Array.Empty<MbReleaseGroup>(), 0));

        public List<TrackQuery> Queries { get; } = new();

        public bool Throws { get; init; }

        public IReadOnlyList<MbRelease>? Releases { get; set; } = Array.Empty<MbRelease>();

        public Dictionary<string, MbTracklist?> Tracklists { get; } = new();

        public List<string> ReleaseGroupCalls { get; } = new();

        public List<string> TracklistCalls { get; } = new();

        public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct)
        {
            ReleaseGroupCalls.Add(releaseGroupId);
            return Task.FromResult(Releases);
        }

        public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct)
        {
            TracklistCalls.Add(releaseId);
            return Task.FromResult(Tracklists.TryGetValue(releaseId, out var list) ? list : null);
        }

        public Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            return Throws ? throw new HttpRequestException("down") : Task.FromResult(recordings);
        }
    }

    private static LidarrAlbum Album(string id, string title, string type = "Album", string artist = "Radiohead", string[]? secondary = null)
        => new()
        {
            Id = 0,
            ForeignAlbumId = id,
            Title = title,
            AlbumType = type,
            SecondaryTypes = secondary?.ToList(),
            ReleaseDate = "1993-02-22T00:00:00Z",
            Artist = new LidarrArtist { ArtistName = artist, ForeignArtistId = Radiohead },
        };

    private static (TestEnv Env, CatalogService Catalog, FakeMusicBrainz Mb) MakeCatalog(Action<TestEnv>? arrange = null, bool mbThrows = false)
    {
        var env = new TestEnv();
        env.Lidarr.OnLookup = term => term switch
        {
            "lidarr:" + PabloHoney => new[] { Album(PabloHoney, "Pablo Honey") },
            "lidarr:" + Anthology => new[] { Album(Anthology, "The Best Of", secondary: new[] { "Compilation" }) },
            "creep" => new[] { Album(CreepSingle, "Creep", "Single") },
            _ => Array.Empty<LidarrAlbum>(),
        };
        arrange?.Invoke(env);
        var mb = new FakeMusicBrainz(new[]
        {
            Rec("Creep", "Radiohead", 100, Group(CreepSingle, "Single"), Group(Anthology, "Album", 2008, "Compilation"), Group(PabloHoney, "Album") with { Disc = 1, Position = 2 }) with { Id = Recording },
        })
        { Throws = mbThrows };
        return (env, new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, mb, () => env.Now), mb);
    }

    [Fact]
    public async Task Search_ATrackTitleOffersTheAlbumsThatHoldIt_WithoutTheSingle_AndWithTheHint()
    {
        var (env, catalog, mb) = MakeCatalog();
        using var _ = env;

        var result = await catalog.SearchAsync("creep", CancellationToken.None);

        Assert.Equal(new[] { PabloHoney, Anthology }, result.Albums.Select(a => a.Mbid));
        Assert.All(result.Albums, a => Assert.Equal("Creep", a.MatchedTrack!.Title));
        Assert.All(result.Albums, a => Assert.Equal(Recording, a.MatchedTrack!.RecordingMbid));
        var pablo = result.Albums.Single(a => a.Mbid == PabloHoney).MatchedTrack!;
        Assert.Equal((1, 2), (pablo.Disc, pablo.Position));
        Assert.Equal(new TrackQuery("creep", null), Assert.Single(mb.Queries));
        // resolved through Lidarr's id lookup, so a request takes the existing path
        Assert.Contains("lidarr:" + PabloHoney, env.Lidarr.LookupTerms);
        Assert.DoesNotContain("lidarr:" + CreepSingle, env.Lidarr.LookupTerms);
    }

    [Fact]
    public async Task Search_HidingSinglesIsASetting()
    {
        var (env, catalog, _) = MakeCatalog(e => e.Config.HideSingles = false);
        using var _ = env;

        var result = await catalog.SearchAsync("creep", CancellationToken.None);

        Assert.Contains(CreepSingle, result.Albums.Select(a => a.Mbid));
        Assert.Null(result.Albums.Single(a => a.Mbid == CreepSingle).MatchedTrack);
    }

    [Fact]
    public async Task Search_HidesEpsOnlyWhenAsked()
    {
        var (env, catalog, _) = MakeCatalog(e => e.Lidarr.OnLookup = _ => new[] { Album(CreepEp, "Creep EP", "EP") });
        using var _ = env;
        Assert.Contains(CreepEp, (await catalog.SearchAsync("creep ep", CancellationToken.None)).Albums.Select(a => a.Mbid));

        env.Config.HideEps = true;
        Assert.DoesNotContain(CreepEp, (await catalog.SearchAsync("creep ep", CancellationToken.None)).Albums.Select(a => a.Mbid));
    }

    [Fact]
    public async Task Search_TrackSearchOffOrMusicBrainzDown_StillAnswersFromLidarr()
    {
        var (env, catalog, mb) = MakeCatalog(e => e.Config.SearchTracks = false);
        using (env)
        {
            var result = await catalog.SearchAsync("creep", CancellationToken.None);
            Assert.Empty(result.Albums);
            Assert.Empty(mb.Queries);
        }

        var (env2, catalog2, _) = MakeCatalog(mbThrows: true);
        using var _ = env2;
        Assert.Empty((await catalog2.SearchAsync("creep", CancellationToken.None)).Albums);
    }

    [Fact]
    public async Task Search_AnAllowListIsNotSecondGuessed()
    {
        var (env, catalog, _) = MakeCatalog(e => e.Config.CatalogAllowList = PabloHoney);
        using var _ = env;

        var result = await catalog.SearchAsync("creep", CancellationToken.None);

        Assert.Equal(PabloHoney, Assert.Single(result.Albums).Mbid);
    }

    [Fact]
    public async Task Search_AFailedLidarrLookupSkipsThatAlbumOnly()
    {
        var (env, catalog, _) = MakeCatalog();
        using var _ = env;
        var inner = env.Lidarr.OnLookup;
        env.Lidarr.OnLookup = term => term == "lidarr:" + Anthology ? throw new LidarrException("boom") : inner(term);

        var result = await catalog.SearchAsync("creep", CancellationToken.None);

        Assert.Equal(PabloHoney, Assert.Single(result.Albums).Mbid);
    }

    // ---- request status mapping (wire value unchanged, optional phase)

    [Fact]
    public async Task Search_AWaitingRequestIsSearchingThenNotFoundYet_WhileTheStateStaysRequested()
    {
        var (env, catalog, _) = MakeCatalog();
        using var _ = env;
        env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = PabloHoney, CreatedAt = env.Now });

        var fresh = (await catalog.SearchAsync("creep", CancellationToken.None)).Albums.Single(a => a.Mbid == PabloHoney);
        Assert.Equal(("requested", "searching"), (fresh.State, fresh.Phase));

        env.Now += TimeSpan.FromDays(3);
        var stale = (await catalog.SearchAsync("creep", CancellationToken.None)).Albums.Single(a => a.Mbid == PabloHoney);
        Assert.Equal(("requested", "notFoundYet"), (stale.State, stale.Phase));
    }

    [Fact]
    public async Task Search_AGrabbedRequestHasNoPhase()
    {
        var (env, catalog, _) = MakeCatalog();
        using var _ = env;
        var request = env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = PabloHoney });
        env.Store.Transition(request.Id, RequestStatus.Live, RequestStatus.Downloading, string.Empty);

        var album = (await catalog.SearchAsync("creep", CancellationToken.None)).Albums.Single(a => a.Mbid == PabloHoney);

        Assert.Equal(("downloading", null), (album.State, album.Phase));
    }

    [Theory]
    [InlineData(RequestStatus.Requested, 0, "searching")]
    [InlineData(RequestStatus.Requested, 2, "searching")]
    [InlineData(RequestStatus.Requested, 3, "notFoundYet")]
    [InlineData(RequestStatus.Requested, 10, "notFoundYet")]
    [InlineData(RequestStatus.Downloading, 5, null)]
    [InlineData(RequestStatus.Available, 5, null)]
    [InlineData(RequestStatus.Failed, 20, null)]
    [InlineData(RequestStatus.Cancelled, 5, null)]
    public void PhaseAt_OnlyAWaitingRequestHasAPhase(string status, int ageDays, string? expected)
    {
        var created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new RequestRecord { Status = status, CreatedAt = created };

        Assert.Equal(expected, request.PhaseAt(created.AddDays(ageDays), TimeSpan.FromDays(3)));
    }

    [Fact]
    public void Wire_NewFieldsAreOptional_AndTheStateValuesAreTheOnesTheAppKnows()
    {
        var album = new CatalogAlbum("m", "a", "T", "A", 2000, "Album", string.Empty, "requested", 1, "Album", new List<string>(), 1, 10, null, null, string.Empty);

        var json = JsonSerializer.Serialize(album);

        Assert.DoesNotContain("matchedTrack", json);
        Assert.DoesNotContain("phase", json);
        Assert.Contains("\"state\":\"requested\"", json);
    }

    [Fact]
    public async Task Search_TheMatchedTrackIsOnTheWire()
    {
        var (env, catalog, _) = MakeCatalog();
        using var _ = env;

        var json = JsonDocument.Parse(Jellyfin.Plugin.Sillon.Api.ApiJson.Result(await catalog.SearchAsync("creep", CancellationToken.None)).Content!).RootElement;

        var pablo = json.GetProperty("albums").EnumerateArray().Single(a => a.GetProperty("mbid").GetString() == PabloHoney);
        Assert.Equal(
            "{\"title\":\"Creep\",\"recordingMbid\":\"" + Recording + "\",\"disc\":1,\"position\":2}",
            pablo.GetProperty("matchedTrack").GetRawText());
        // the Anthology hit has no position: the optional members are absent, not null
        var anthology = json.GetProperty("albums").EnumerateArray().Single(a => a.GetProperty("mbid").GetString() == Anthology);
        Assert.Equal("{\"title\":\"Creep\",\"recordingMbid\":\"" + Recording + "\"}", anthology.GetProperty("matchedTrack").GetRawText());
    }

    // ---- the tracklist of a catalogue album

    private const string ReleaseA = "33333333-0000-0000-0000-00000000000a";
    private const string ReleaseB = "33333333-0000-0000-0000-00000000000b";

    private static MbTracklist Tracklist(string release, params MbTrack[] tracks) => new(release, "Pablo Honey", tracks);

    private static LidarrAlbum WithRelease(string id, string releaseId)
        => new()
        {
            ForeignAlbumId = id,
            Title = "Pablo Honey",
            AlbumType = "Album",
            Releases = new List<LidarrRelease> { new() { ForeignReleaseId = releaseId, TrackCount = 2, MediumCount = 1 } },
        };

    [Fact]
    public async Task Tracks_UsesTheReleaseLidarrShowsFirst_AndFlagsTheMatchedRecording()
    {
        var (env, catalog, mb) = MakeCatalog(e => e.Lidarr.OnLookup = _ => new[] { WithRelease(PabloHoney, ReleaseA) });
        using var _ = env;
        mb.Tracklists[ReleaseA] = Tracklist(
            ReleaseA,
            new MbTrack(1, 1, "1", "You", 208000, "22222222-0000-0000-0000-000000000009"),
            new MbTrack(1, 2, "2", "Creep", 238000, Recording),
            new MbTrack(2, 1, "1", "Creep (live)", null, "22222222-0000-0000-0000-000000000008"));

        var list = await catalog.AlbumTracksAsync(PabloHoney, "Karma Police", Recording.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal((PabloHoney, ReleaseA, 2), (list.AlbumMbid, list.ReleaseMbid, list.DiscCount));
        Assert.Equal(new[] { "You", "Creep", "Creep (live)" }, list.Tracks.Select(t => t.Title));
        Assert.Equal(new bool?[] { null, true, null }, list.Tracks.Select(t => t.Matched));
        Assert.Equal(238000, list.Tracks[1].DurationMs);
        Assert.Empty(mb.ReleaseGroupCalls);
    }

    [Fact]
    public async Task Tracks_WithoutARecordingIdTheTitleMatches_OnlyOnce()
    {
        var (env, catalog, mb) = MakeCatalog(e => e.Lidarr.OnLookup = _ => new[] { WithRelease(PabloHoney, ReleaseA) });
        using var _ = env;
        mb.Tracklists[ReleaseA] = Tracklist(ReleaseA, new MbTrack(1, 1, "1", "Creep", null, ""), new MbTrack(1, 2, "2", "CREEP!", null, ""));

        var list = await catalog.AlbumTracksAsync(PabloHoney, "creep", null, CancellationToken.None);

        Assert.Equal(new bool?[] { true, null }, list.Tracks.Select(t => t.Matched));
    }

    [Fact]
    public async Task Tracks_WhenLidarrNamesNoRelease_MusicBrainzPicksTheEarliestOfficialOne()
    {
        var (env, catalog, mb) = MakeCatalog(e => e.Lidarr.OnLookup = _ => new[] { WithRelease(PabloHoney, string.Empty) });
        using var _ = env;
        mb.Releases = new[] { new MbRelease("bootleg", "Bootleg", "1990"), new MbRelease(ReleaseB, "Official", "1994-01-01"), new MbRelease(ReleaseA, "Official", "1993-02-22") };
        mb.Tracklists[ReleaseA] = Tracklist(ReleaseA, new MbTrack(1, 1, "1", "You", null, "r"));

        var list = await catalog.AlbumTracksAsync(PabloHoney, null, null, CancellationToken.None);

        Assert.Equal(ReleaseA, list.ReleaseMbid);
        Assert.Equal(new[] { ReleaseA }, mb.TracklistCalls);
    }

    [Fact]
    public async Task Tracks_AnAlbumLidarrDoesNotKnow_Is404_AndTheAllowListDecides()
    {
        var (env, catalog, mb) = MakeCatalog(e => e.Lidarr.OnLookup = _ => Array.Empty<LidarrAlbum>());
        using var _ = env;

        var ex = await Assert.ThrowsAsync<CatalogException>(() => catalog.AlbumTracksAsync(PabloHoney, null, null, CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);

        env.Config.CatalogAllowList = Anthology;
        var notOffered = await Assert.ThrowsAsync<CatalogException>(() => catalog.AlbumTracksAsync(PabloHoney, null, null, CancellationToken.None));
        Assert.Equal(404, notOffered.StatusCode);
        Assert.Empty(mb.TracklistCalls);
    }

    [Fact]
    public async Task Tracks_MusicBrainzDown_Is502_AndManualModeIs503()
    {
        var (env, catalog, mb) = MakeCatalog(e => e.Lidarr.OnLookup = _ => new[] { WithRelease(PabloHoney, ReleaseA) });
        using var _ = env;
        mb.Tracklists[ReleaseA] = null;

        var down = await Assert.ThrowsAsync<CatalogException>(() => catalog.AlbumTracksAsync(PabloHoney, null, null, CancellationToken.None));
        Assert.Equal(502, down.StatusCode);

        env.Config.FulfillmentMode = Jellyfin.Plugin.Sillon.Configuration.FulfillmentMode.Manual;
        var manual = await Assert.ThrowsAsync<CatalogException>(() => catalog.AlbumTracksAsync(PabloHoney, null, null, CancellationToken.None));
        Assert.Equal(503, manual.StatusCode);
    }

    private const string ReleaseJson = """
        {"id":"33333333-0000-0000-0000-00000000000a","title":"Pablo Honey","status":"Official",
         "media":[
          {"position":1,"tracks":[
            {"id":"t1","position":1,"number":"1","title":"You","length":208000,"recording":{"id":"22222222-0000-0000-0000-000000000009","title":"You","length":208000}},
            {"id":"t2","position":2,"number":"2","title":"Creep","length":null,"recording":{"id":"22222222-0000-0000-0000-000000000001","title":"Creep","length":238000}}]},
          {"position":2,"tracks":[{"id":"t3","position":1,"number":"A1","title":"Bonus","recording":{"id":"r3","title":"Bonus"}}]}]}
        """;

    [Fact]
    public async Task Client_ReadsReleasesAndTracklists_AndCachesThem()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.Contains("/release-group/")
            ? Ok("""{"releases":[{"id":"a","status":"Official","date":"1993-02-22"},{"id":"b","status":"Bootleg"}]}""")
            : Ok(ReleaseJson));
        var clock = new Clock();
        var client = new MusicBrainzClient(new HttpClient(handler), "1.0.0.0", NullLogger.Instance, () => clock.Now, clock.Delay, "https://mb.test");

        var releases = await client.GetReleasesAsync("grp", CancellationToken.None);
        var list = await client.GetTracklistAsync(ReleaseA, CancellationToken.None);
        await client.GetTracklistAsync(ReleaseA, CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, releases!.Select(r => r.Id));
        Assert.Equal("Official", releases![0].Status);
        Assert.Equal(new[] { "inc=releases" }, handler.Requests[0].RequestUri!.Query.TrimStart('?').Split('&').Where(q => q.StartsWith("inc")));
        Assert.Equal(new[] { "inc=recordings" }, handler.Requests[1].RequestUri!.Query.TrimStart('?').Split('&').Where(q => q.StartsWith("inc")));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { (1, 1, "1", "You", (int?)208000), (1, 2, "2", "Creep", 238000), (2, 1, "A1", "Bonus", null) },
            list!.Tracks.Select(t => (t.Disc, t.Position, t.Number, t.Title, t.LengthMs)));
        Assert.Equal("22222222-0000-0000-0000-000000000001", list.Tracks[1].RecordingId);
    }

    [Fact]
    public async Task Client_ASearchHitCarriesTheRecordingIdAndTheTrackPosition()
    {
        var (client, _, _) = MakeClient(_ => Ok("""
            {"recordings":[{"id":"22222222-0000-0000-0000-000000000001","score":100,"title":"Creep","artist-credit":[{"name":"Radiohead"}],
              "releases":[{"title":"Pablo Honey","date":"1993","media":[{"position":2,"track":[{"number":"7","title":"Creep"}]}],
                "release-group":{"id":"g","primary-type":"Album"}}]}]}
            """));

        var found = Assert.Single(await client.SearchRecordingsAsync(new TrackQuery("Creep", null), CancellationToken.None));

        Assert.Equal("22222222-0000-0000-0000-000000000001", found.Id);
        Assert.Equal((2, 7), (found.ReleaseGroups[0].Disc, found.ReleaseGroups[0].Position));
    }

    [Fact]
    public async Task Client_AFailedTracklistIsNull_NotEmpty()
    {
        var (client, _, _) = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.GetTracklistAsync(ReleaseA, CancellationToken.None));
    }
}
