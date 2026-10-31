using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class DeezerHintsTests
{
    private const string PabloHoney = "11111111-0000-0000-0000-000000000001";
    private const string CrazySexyCool = "11111111-0000-0000-0000-000000000002";
    private const string Obscure = "11111111-0000-0000-0000-000000000003";
    private const string BestOf = "11111111-0000-0000-0000-000000000004";
    private const string Later = "11111111-0000-0000-0000-000000000005";
    private const string DishOfTheDay = "11111111-0000-0000-0000-000000000006";
    private const string CreepSingle = "11111111-0000-0000-0000-000000000007";

    // what api.deezer.com/search answered for "Creep", preview links shortened
    private static readonly string CreepJson = Answer(
        ("Creep", "Creep", "", 399, "Radiohead", "Pablo Honey"),
        ("Creep (Acoustic)", "Creep", "(Acoustic)", 399, "Radiohead", "Creep EP"),
        ("Creep", "Creep", "", 1321, "TLC", "CrazySexyCool"),
        ("Creep", "Creep", "", 5271261, "Daniela Andrade", "Covers"),
        ("Creep", "Creep", "", 1405, "Stone Temple Pilots", "Core"));

    private static string Answer(params (string Title, string Short, string Version, int ArtistId, string Artist, string Album)[] tracks)
        => JsonSerializer.Serialize(new
        {
            data = tracks.Select((t, i) => new Dictionary<string, object>
            {
                ["id"] = 138547415 + i,
                ["readable"] = true,
                ["title"] = t.Title,
                ["title_short"] = t.Short,
                ["title_version"] = t.Version,
                ["isrc"] = "GBAYE9200070",
                ["link"] = "https://www.deezer.com/track/" + (138547415 + i),
                ["duration"] = 238,
                ["rank"] = 978547 - i,
                ["explicit_lyrics"] = false,
                ["explicit_content_lyrics"] = 0,
                ["explicit_content_cover"] = 0,
                ["preview"] = "https://cdnt-preview.dzcdn.net/api/1/1/x.mp3",
                ["md5_image"] = "1dd56fd8824492e1a5106c99a00a85ec",
                ["artist"] = new Dictionary<string, object>
                {
                    ["id"] = t.ArtistId,
                    ["name"] = t.Artist,
                    ["link"] = "https://www.deezer.com/artist/" + t.ArtistId,
                    ["picture"] = "https://api.deezer.com/artist/" + t.ArtistId + "/image",
                    ["tracklist"] = "https://api.deezer.com/artist/" + t.ArtistId + "/top?limit=50",
                    ["type"] = "artist",
                },
                ["album"] = new Dictionary<string, object>
                {
                    ["id"] = 14880711,
                    ["title"] = t.Album,
                    ["cover"] = "https://api.deezer.com/album/14880711/image",
                    ["md5_image"] = "1dd56fd8824492e1a5106c99a00a85ec",
                    ["tracklist"] = "https://api.deezer.com/album/14880711/tracks",
                    ["type"] = "album",
                },
                ["type"] = "track",
            }),
            total = 232,
            next = "https://api.deezer.com/search?q=Creep&limit=5&index=5",
        });

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class CountingLogger : ILogger
    {
        public int Entries { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Entries++;
            }
        }
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Stub Answers(string body) => new((_, _) => Task.FromResult(Ok(body)));

    private static DeezerHints Client(Stub stub, Clock? clock = null, ILogger? logger = null)
    {
        var at = clock ?? new Clock();
        return new DeezerHints(new HttpClient(stub), "1.2.3.4", logger ?? NullLogger.Instance, () => at.Now, "https://deezer.test");
    }

    // ---- the provider

    [Fact]
    public async Task Provider_KeepsExactTitles_DistinctArtists_InDeezersOrder_AtMostThree()
    {
        var artists = await Client(Answers(CreepJson)).ArtistsForTitleAsync("creep", CancellationToken.None);

        Assert.Equal(new[] { "Radiohead", "TLC", "Daniela Andrade" }, artists);
    }

    [Fact]
    public async Task Provider_ComparesTitlesFolded_AndArtistsWithoutPunctuation()
    {
        var body = Answer(
            ("Déjà-Vu", "Déjà-Vu", "", 1, "Fool's Garden", "A"),
            ("deja vu", "deja vu", "", 2, "Fools Garden", "B"),
            ("Deja Vu (Live)", "Deja Vu", "(Live)", 3, "Live Band", "C"));

        Assert.Equal(new[] { "Fool's Garden" }, await Client(Answers(body)).ArtistsForTitleAsync("Deja Vu", CancellationToken.None));
    }

    [Fact]
    public async Task Provider_SendsTheTitleAndNothingAboutTheServerOrTheUser()
    {
        var stub = Answers(CreepJson);

        await Client(stub).ArtistsForTitleAsync(" Lemon Tree ", CancellationToken.None);

        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://deezer.test/search?q=Lemon%20Tree&limit=5", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Sillon-Plugin/1.2.3.4", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Headers.Authorization);
        Assert.Null(request.Headers.Referrer);
    }

    [Fact]
    public void Provider_NeverKeepsTheCookiesDeezerSets()
    {
        using var handler = DeezerHints.CreateHandler();

        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Provider_KeepsAnAnswerForDays_ThenAsksAgain()
    {
        var stub = Answers(CreepJson);
        var clock = new Clock();
        var client = Client(stub, clock);

        await client.ArtistsForTitleAsync("Creep", CancellationToken.None);
        clock.Now += TimeSpan.FromDays(6);
        await client.ArtistsForTitleAsync("CREEP!", CancellationToken.None);
        Assert.Single(stub.Requests);

        clock.Now += TimeSpan.FromDays(2);
        await client.ArtistsForTitleAsync("Creep", CancellationToken.None);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Provider_KeepsAnEmptyAnswerToo_AndItsCacheIsBounded()
    {
        var stub = Answers("{\"data\":[],\"total\":0}");
        var client = Client(stub);

        Assert.Empty(await client.ArtistsForTitleAsync("nothing", CancellationToken.None));
        Assert.Empty(await client.ArtistsForTitleAsync("nothing", CancellationToken.None));
        Assert.Single(stub.Requests);

        for (var i = 0; i < 1000; i++)
        {
            await client.ArtistsForTitleAsync("title " + i, CancellationToken.None);
        }

        // the cache filled up and started over: the first title is gone
        await client.ArtistsForTitleAsync("nothing", CancellationToken.None);
        Assert.Equal(1002, stub.Requests.Count);
    }

    public static TheoryData<string> Failures => new()
    {
        "status",
        "quota",
        "not-json",
        "network",
        "timeout",
    };

    private static Task<HttpResponseMessage> Fail(string how, CancellationToken ct) => how switch
    {
        "status" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
        "quota" => Task.FromResult(Ok("{\"error\":{\"type\":\"Exception\",\"message\":\"Quota limit exceeded\",\"code\":4}}")),
        "not-json" => Task.FromResult(Ok("<html>")),
        "network" => throw new HttpRequestException("down"),
        _ => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => Ok(CreepJson), ct, TaskContinuationOptions.None, TaskScheduler.Default),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Provider_AnyFailureGivesNoHint_LogsOnce_AndPausesTheCalls(string how)
    {
        var healthy = false;
        var stub = new Stub((_, ct) => healthy ? Task.FromResult(Ok(CreepJson)) : Fail(how, ct));
        var clock = new Clock();
        var logger = new CountingLogger();
        var client = Client(stub, clock, logger);

        Assert.Empty(await client.ArtistsForTitleAsync("Creep", CancellationToken.None));
        Assert.Empty(await client.ArtistsForTitleAsync("Lemon Tree", CancellationToken.None));
        Assert.Single(stub.Requests);
        Assert.Equal(1, logger.Entries);

        // the outage goes on: still one line
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Empty(await client.ArtistsForTitleAsync("Creep", CancellationToken.None));
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(1, logger.Entries);

        // a failure is not kept as an answer
        healthy = true;
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.NotEmpty(await client.ArtistsForTitleAsync("Creep", CancellationToken.None));
    }

    [Fact]
    public async Task Provider_TheCallersCancellationStillCancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var client = Client(new Stub((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ArtistsForTitleAsync("Creep", cancelled.Token));
    }

    // ---- the catalogue

    private sealed class FakeMusicBrainz(Func<TrackQuery, IReadOnlyList<MbRecording>> answer) : IMusicBrainzApi
    {
        public Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
            => Task.FromResult(new MbReleaseGroupPage(Array.Empty<MbReleaseGroup>(), 0));

        public List<TrackQuery> Queries { get; } = new();

        public Func<TrackQuery, CancellationToken, Task>? Before { get; init; }

        public async Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            if (Before is not null)
            {
                await Before(query, ct);
            }

            return answer(query);
        }

        public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRelease>?>(null);

        public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct) => Task.FromResult<MbTracklist?>(null);
    }

    private static MbReleaseGroup Group(string id, string type = "Album", params string[] secondary) => new(id, "T", type, secondary, 1993);

    private static MbRecording Rec(string artist, params MbReleaseGroup[] groups) => new("Creep", artist, 100, groups, "rec-" + artist);

    // MusicBrainz's text ranking of "Creep": 3,542 recordings, the first ones by nobody famous
    private static IReadOnlyList<MbRecording> CreepOnMusicBrainz(TrackQuery query) => query.Artist switch
    {
        null => new[] { Rec("Some Cover Band", Group(Obscure)) },
        "Radiohead" => new[] { Rec("Radiohead", Group(PabloHoney), Group(CreepSingle, "Single"), Group(BestOf, "Album", "Compilation"), Group(Later, "Album", "Live")) },
        "TLC" => new[] { Rec("TLC", Group(CrazySexyCool), Group(Obscure)) },
        _ => Array.Empty<MbRecording>(),
    };

    private static (TestEnv Env, CatalogService Catalog) Make(IMusicBrainzApi mb, ITrackHints hints, bool on = true, TimeSpan? budget = null, Action<TestEnv>? arrange = null)
    {
        var env = new TestEnv();
        env.Config.DeezerPopularityHints = on;
        env.Lidarr.OnLookup = term => term.StartsWith("lidarr:", StringComparison.Ordinal)
            ? new[] { new LidarrAlbum { ForeignAlbumId = term["lidarr:".Length..], Title = "Album " + term[^1], AlbumType = "Album", Artist = new LidarrArtist { ArtistName = "A" + term[^1], ForeignArtistId = "artist-" + term[^1] } } }
            : Array.Empty<LidarrAlbum>();
        arrange?.Invoke(env);
        return (env, new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, mb, () => env.Now, hints, budget));
    }

    private static async Task<IReadOnlyList<string>> Ids(CatalogService catalog, string query)
        => (await catalog.SearchAsync(query, CancellationToken.None)).Albums.Select(a => a.Mbid).ToList();

    [Fact]
    public async Task Search_Creep_OffersPabloHoneyFirst_ThenThePlainResults_WithoutDuplicates()
    {
        var mb = new FakeMusicBrainz(CreepOnMusicBrainz);
        var (env, catalog) = Make(mb, Client(Answers(CreepJson)));
        using var _ = env;

        var ids = await Ids(catalog, "Creep");

        // each artist offers its best two; the album TLC and the plain search share appears once
        Assert.Equal(new[] { PabloHoney, BestOf, CrazySexyCool, Obscure }, ids);
        Assert.Equal(new[] { new TrackQuery("Creep", "Radiohead", true), new TrackQuery("Creep", "TLC", true), new TrackQuery("Creep", "Daniela Andrade", true), new TrackQuery("Creep", null) }, mb.Queries);
    }

    [Fact]
    public async Task Search_HintedAlbumsComeBeforeLidarrsOwnResults_WhichAreKept()
    {
        var (env, catalog) = Make(new FakeMusicBrainz(CreepOnMusicBrainz), Client(Answers(CreepJson)), arrange: e =>
        {
            e.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Album = new LidarrAlbum { ForeignAlbumId = "lidarr-own", Title = "Creep", AlbumType = "Album" } } };
        });
        using var _ = env;

        var ids = await Ids(catalog, "Creep");

        // Obscure is also TLC's second album here, so it comes with the hints
        Assert.Equal(new[] { PabloHoney, BestOf, CrazySexyCool, Obscure, "lidarr-own" }, ids);
    }

    [Fact]
    public async Task Search_ARecordingFoundThroughAHint_KeepsItsMatchedTrack()
    {
        var (env, catalog) = Make(new FakeMusicBrainz(CreepOnMusicBrainz), Client(Answers(CreepJson)));
        using var _ = env;

        var album = (await catalog.SearchAsync("Creep", CancellationToken.None)).Albums[0];

        Assert.Equal(("Creep", "rec-Radiohead"), (album.MatchedTrack!.Title, album.MatchedTrack.RecordingMbid));
    }

    [Fact]
    public async Task Search_SettingOff_MakesNoCallToDeezer_AndGivesTodaysResults()
    {
        var stub = Answers(CreepJson);
        var mb = new FakeMusicBrainz(CreepOnMusicBrainz);
        var (env, catalog) = Make(mb, Client(stub), on: false);
        using var _ = env;

        var ids = await Ids(catalog, "Creep");

        Assert.Empty(stub.Requests);
        Assert.Equal(new[] { Obscure }, ids);
        Assert.Equal(new TrackQuery("Creep", null), Assert.Single(mb.Queries));
    }

    [Theory]
    [InlineData("Radiohead - Creep")]
    [InlineData("Creep by Radiohead")]
    [InlineData("ab")]
    public async Task Search_OnlyABareTitleAsksDeezer(string query)
    {
        var stub = Answers(CreepJson);
        var (env, catalog) = Make(new FakeMusicBrainz(CreepOnMusicBrainz), Client(stub));
        using var _ = env;

        await catalog.SearchAsync(query, CancellationToken.None);

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Search_WithSongSearchOff_MakesNoCallToDeezer()
    {
        var stub = Answers(CreepJson);
        var (env, catalog) = Make(new FakeMusicBrainz(CreepOnMusicBrainz), Client(stub), arrange: e => e.Config.SearchTracks = false);
        using var _ = env;

        await catalog.SearchAsync("Creep", CancellationToken.None);

        Assert.Empty(stub.Requests);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Search_WithDeezerDown_GivesTodaysResults(string how)
    {
        var mb = new FakeMusicBrainz(CreepOnMusicBrainz);
        var (env, catalog) = Make(mb, Client(new Stub((_, ct) => Fail(how, ct))));
        using var _ = env;

        Assert.Equal(new[] { Obscure }, await Ids(catalog, "Creep"));
        Assert.Equal(new TrackQuery("Creep", null), Assert.Single(mb.Queries));
    }

    [Fact]
    public async Task Search_AProviderThatThrows_GivesTodaysResults()
    {
        var (env, catalog) = Make(new FakeMusicBrainz(CreepOnMusicBrainz), new ThrowingHints());
        using var _ = env;

        Assert.Equal(new[] { Obscure }, await Ids(catalog, "Creep"));
    }

    private sealed class ThrowingHints : ITrackHints
    {
        public Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct) => throw new InvalidOperationException("broken");
    }

    [Theory]
    [InlineData("Fools Garden", "Fool's Garden")]
    [InlineData("Fool's Garden", "Fools Garden")]
    [InlineData("Fool’s Garden", "Fool's Garden")]
    [InlineData("Beyonce", "Beyoncé")]
    [InlineData("Sigur Rós", "Sigur Ros")]
    [InlineData("AC/DC", "AC-DC")]
    public async Task Search_ArtistSpellingsThatDifferOnlyByPunctuationOrAccents_StillMatch(string onDeezer, string onMusicBrainz)
    {
        var hints = Client(Answers(Answer(("Lemon Tree", "Lemon Tree", "", 1, onDeezer, "Dish of the Day"))));
        var mb = new FakeMusicBrainz(q => q.Artist is null
            ? Array.Empty<MbRecording>()
            : new[] { new MbRecording("Lemon Tree", onMusicBrainz, 100, new[] { Group(DishOfTheDay) }) });
        var (env, catalog) = Make(mb, hints);
        using var _ = env;

        Assert.Equal(new[] { DishOfTheDay }, await Ids(catalog, "Lemon Tree"));
    }

    [Fact]
    public async Task Search_ARecordingFoundUnderTheOtherSpelling_CountsDespiteItsLowScore()
    {
        var hints = Client(Answers(Answer(("Lemon Tree", "Lemon Tree", "", 1, "Fools Garden", "Dish of the Day"))));
        var mb = new FakeMusicBrainz(q => q.Artist is null
            ? Array.Empty<MbRecording>()
            : new[]
            {
                new MbRecording("Lemon Tree", "Fools Garden", 100, new[] { Group(Obscure, "Album", "Compilation") }),
                new MbRecording("Lemon Tree", "Fool's Garden", 45, new[] { Group(DishOfTheDay) }),
            });
        var (env, catalog) = Make(mb, hints);
        using var _ = env;

        Assert.Equal(new[] { DishOfTheDay, Obscure }, await Ids(catalog, "Lemon Tree"));
    }

    [Fact]
    public async Task Search_AnArtistDeezerNamesButMusicBrainzDoesNotKnow_IsSkipped()
    {
        // Daniela Andrade has no recording in the MusicBrainz stub
        var hints = Client(Answers(Answer(("Creep", "Creep", "", 5271261, "Daniela Andrade", "Covers"), ("Creep", "Creep", "", 1321, "TLC", "CrazySexyCool"))));
        var (env, catalog) = Make(new FakeMusicBrainz(CreepOnMusicBrainz), hints);
        using var _ = env;

        Assert.Equal(new[] { CrazySexyCool, Obscure }, await Ids(catalog, "Creep"));
    }

    [Fact]
    public async Task Search_AnotherArtistsRecording_DoesNotPassForTheHintedOne()
    {
        var hints = Client(Answers(Answer(("Creep", "Creep", "", 1, "Radiohead", "Pablo Honey"))));
        var mb = new FakeMusicBrainz(q => q.Artist is null ? Array.Empty<MbRecording>() : new[] { Rec("Some Tribute Band", Group(Obscure)) });
        var (env, catalog) = Make(mb, hints);
        using var _ = env;

        Assert.Empty(await Ids(catalog, "Creep"));
    }

    [Fact]
    public async Task Search_SinglesStayHidden_WhenAHintedArtistOnlyHasASingle()
    {
        var hints = Client(Answers(Answer(("Creep", "Creep", "", 1, "Radiohead", "Creep"))));
        var mb = new FakeMusicBrainz(q => q.Artist is null ? Array.Empty<MbRecording>() : new[] { Rec("Radiohead", Group(CreepSingle, "Single")) });
        var (env, catalog) = Make(mb, hints);
        using var _ = env;

        Assert.Empty(await Ids(catalog, "Creep"));
        Assert.DoesNotContain("lidarr:" + CreepSingle, env.Lidarr.LookupTerms);
    }

    [Fact]
    public async Task Search_AHintedEp_FollowsTheHideEpsSetting()
    {
        var hints = Client(Answers(Answer(("Creep", "Creep", "", 1, "Radiohead", "Creep EP"))));
        var mb = new FakeMusicBrainz(q => q.Artist is null ? Array.Empty<MbRecording>() : new[] { Rec("Radiohead", Group(Later, "EP")) });
        var (env, catalog) = Make(mb, hints, arrange: e => e.Config.HideEps = true);
        using var _ = env;

        Assert.Empty(await Ids(catalog, "Creep"));
    }

    [Fact]
    public async Task Search_OutOfTime_KeepsTheHintsFoundSoFar_AndThePlainResults()
    {
        var mb = new FakeMusicBrainz(CreepOnMusicBrainz)
        {
            Before = (q, ct) => q.Artist == "TLC" ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask,
        };
        var (env, catalog) = Make(mb, Client(Answers(CreepJson)), budget: TimeSpan.FromMilliseconds(100));
        using var _ = env;

        Assert.Equal(new[] { PabloHoney, BestOf, Obscure }, await Ids(catalog, "Creep"));
    }

    [Fact]
    public async Task Search_RespectsMusicBrainzsPaceAndCache()
    {
        var mbHttp = new Stub((request, _) => Task.FromResult(Ok("{\"recordings\":[]}")));
        var clock = new Clock();
        var starts = new List<DateTimeOffset>();
        var client = new MusicBrainzClient(
            new HttpClient(new Recording(mbHttp, () => starts.Add(clock.Now))),
            "1.0.0.0",
            NullLogger.Instance,
            () => clock.Now,
            (span, _) =>
            {
                clock.Now += span;
                return Task.CompletedTask;
            },
            "https://mb.test");
        var (env, catalog) = Make(client, Client(Answers(CreepJson)));
        using var _ = env;

        await catalog.SearchAsync("Creep", CancellationToken.None);
        Assert.Equal(4, mbHttp.Requests.Count);
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1)));

        // the same search again: nothing reaches MusicBrainz
        await catalog.SearchAsync("creep", CancellationToken.None);
        Assert.Equal(4, mbHttp.Requests.Count);
    }

    [Fact]
    public async Task Search_AHintedQuery_LooksForEverySpellingOfTheArtist_DeeperThanAPlainOne()
    {
        var mbHttp = new Stub((request, _) => Task.FromResult(Ok("{\"recordings\":[]}")));
        var client = new MusicBrainzClient(new HttpClient(mbHttp), "1.0.0.0", NullLogger.Instance, delay: (_, _) => Task.CompletedTask, baseUrl: "https://mb.test");
        var (env, catalog) = Make(client, Client(Answers(Answer(("Lemon Tree", "Lemon Tree", "", 1, "Fools Garden", "Dish of the Day")))));
        using var _ = env;

        await catalog.SearchAsync("Lemon Tree", CancellationToken.None);

        var urls = mbHttp.Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.Query)).ToList();
        Assert.Equal("?fmt=json&limit=25&query=recording:\"Lemon Tree\"", urls[1]);
        Assert.Equal("?fmt=json&limit=100&query=recording:\"Lemon Tree\" AND (artist:\"Fools Garden\" OR artistname:\"Fools Garden\")", urls[0]);
    }

    [Fact]
    public async Task Search_AnArtistTheUserNamed_KeepsTheExactForm()
    {
        var mbHttp = new Stub((request, _) => Task.FromResult(Ok("{\"recordings\":[]}")));
        var client = new MusicBrainzClient(new HttpClient(mbHttp), "1.0.0.0", NullLogger.Instance, delay: (_, _) => Task.CompletedTask, baseUrl: "https://mb.test");
        var (env, catalog) = Make(client, Client(Answers(CreepJson)));
        using var _ = env;

        await catalog.SearchAsync("Radiohead - Creep", CancellationToken.None);

        Assert.Equal("?fmt=json&limit=25&query=recording:\"Creep\" AND artist:\"Radiohead\"", Uri.UnescapeDataString(Assert.Single(mbHttp.Requests).RequestUri!.Query));
    }

    private sealed class Recording(HttpMessageHandler inner, Action onSend) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return base.SendAsync(request, cancellationToken);
        }
    }

    // ---- the setting

    [Fact]
    public void Setting_IsOffByDefault()
        => Assert.False(new PluginConfiguration().DeezerPopularityHints);

    [Fact]
    public void Setting_StaysOffWhenUpgradingFromAConfigurationWithoutIt()
    {
        const string Previous = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration><FulfillmentMode>Lidarr</FulfillmentMode><SearchTracks>true</SearchTracks><HideSingles>true</HideSingles></PluginConfiguration>
            """;
        using var reader = new StringReader(Previous);

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        Assert.False(config.DeezerPopularityHints);
        Assert.Equal(FulfillmentMode.Lidarr, config.FulfillmentMode);
    }

    [Fact]
    public void ConfigurationPage_OffersTheSetting_AndStatesWhatLeavesTheServer()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Sillon.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);

        var page = reader.ReadToEnd();

        Assert.Contains("id=\"DeezerPopularityHints\"", page, StringComparison.Ordinal);
        Assert.Contains("c.DeezerPopularityHints === true", page, StringComparison.Ordinal);
        Assert.Contains("config.DeezerPopularityHints = el('DeezerPopularityHints').checked", page, StringComparison.Ordinal);
        Assert.Contains("the search text and this server\\'s IP address go to Deezer", page, StringComparison.Ordinal);
        Assert.Contains("non-commercial private or family use", page, StringComparison.Ordinal);
        Assert.Contains("https://developers.deezer.com/termsofuse", page, StringComparison.Ordinal);
    }
}
