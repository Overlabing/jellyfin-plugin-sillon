using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>A whole discography from one request to Lidarr's metadata service. Fixtures are trimmed answers of the real service.</summary>
public class ArtistMetadataTests
{
    private const string Beatles = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";
    private const string BeatlesOldId = "f8b6c151-03ff-4fd7-8df0-ff0980d6ae4c";
    private const string BrokeForFree = "069a1c1f-14eb-4d36-b0a0-77dffbd67713";
    private const string Directionless = "ce0eb31a-142a-4ac8-9cb9-6a3adb3ad19e";
    private const string AbbeyRoad = "9162580e-5df4-32de-80cc-f45a8d8a9b1d";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string BeatlesAnswer => Read("metadata_artist_beatles.json");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (LidarrMetadataClient Client, Handler Http) Client(Func<HttpRequestMessage, HttpResponseMessage> respond, Func<DateTimeOffset>? now = null)
    {
        var http = new Handler((request, _) => Task.FromResult(respond(request)));
        return (new LidarrMetadataClient(new HttpClient(http), "1.2.3.4", NullLogger.Instance, now, "https://metadata.test"), http);
    }

    private sealed class FakeMetadata(Func<string, MetadataArtist?> answer) : IArtistMetadata
    {
        public List<string> Asked { get; } = new();

        public Task<MetadataArtist?> GetArtistAsync(string artistId, CancellationToken ct)
        {
            Asked.Add(artistId);
            return Task.FromResult(answer(artistId));
        }
    }

    private sealed class FakeBrowse : IMusicBrainzApi
    {
        public int Browses { get; private set; }

        public IReadOnlyList<MbReleaseGroup> Groups { get; init; } = Array.Empty<MbReleaseGroup>();

        public Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
        {
            Browses++;
            return Task.FromResult(new MbReleaseGroupPage(Groups, Groups.Count));
        }

        public Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRecording>>(Array.Empty<MbRecording>());

        public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRelease>?>(null);

        public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct) => Task.FromResult<MbTracklist?>(null);
    }

    private static MetadataArtist Parsed(string json, string id = Beatles)
    {
        var (client, _) = Client(_ => Answer(json));
        return client.GetArtistAsync(id, CancellationToken.None).GetAwaiter().GetResult()!;
    }

    /// <summary>A Lidarr that answers as the real one does for an artist it does not hold: the artist alone for its id and for its name.</summary>
    private static TestEnv Env(string id = Beatles, string name = "The Beatles")
    {
        var env = new TestEnv();
        var artist = new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = id, ArtistName = name, Ratings = new LidarrRatings { Votes = 86 } } };
        env.Lidarr.OnSearch = _ => new[] { artist };
        return env;
    }

    private static CatalogService Catalog(TestEnv env, IMusicBrainzApi musicBrainz, IArtistMetadata metadata)
        => new(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, musicBrainz, metadata: metadata);

    private static string[] Titles(IEnumerable<CatalogAlbum> albums) => albums.Select(a => a.Title).ToArray();

    // ---- the client

    [Fact]
    public async Task Client_ReadsARealAnswer_NamesOnlyThePlugin_AndKeepsIt()
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var (client, http) = Client(_ => Answer(BeatlesAnswer), () => now);

        var artist = await client.GetArtistAsync(Beatles, CancellationToken.None);

        Assert.Equal("The Beatles", artist!.Name);
        Assert.Equal("Group", artist.ArtistType);
        Assert.Equal(26, artist.Albums.Count);
        var abbey = Assert.Single(artist.Albums, a => a.Title == "Abbey Road");
        Assert.Equal("Album", abbey.Type);
        Assert.Equal("1969-09-26", abbey.ReleaseDate);
        Assert.Contains("Official", abbey.ReleaseStatuses);
        Assert.Contains(artist.Images, i => i.CoverType == "poster" && i.Url.StartsWith("https://", StringComparison.Ordinal));

        var request = Assert.Single(http.Requests);
        Assert.Equal($"https://metadata.test/api/v0.4/artist/{Beatles}", request.RequestUri!.ToString());
        Assert.Equal("Sillon-Plugin/1.2.3.4", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Authorization"));

        // hours later still the kept answer, and a new one once that is over
        now = now.AddHours(5);
        Assert.Same(artist, await client.GetArtistAsync(Beatles.ToUpperInvariant(), CancellationToken.None));
        Assert.Single(http.Requests);
        now = now.AddHours(2);
        await client.GetArtistAsync(Beatles, CancellationToken.None);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Client_AcceptsTheRedirectFromAMergedArtistsOldId_ButNoOtherArtist()
    {
        var (client, _) = Client(_ => Answer(BeatlesAnswer));
        Assert.Equal(Beatles, (await client.GetArtistAsync(BeatlesOldId, CancellationToken.None))!.Id);

        var (other, _) = Client(_ => Answer(BeatlesAnswer));
        Assert.Null(await other.GetArtistAsync(BrokeForFree, CancellationToken.None));
    }

    public static TheoryData<string> UnusableAnswers() => new()
    {
        "<html>Bad gateway</html>",
        "{\"error\":\"Internal server error\"}",
        "[]",
        "null",
        // the real answer with a field gone or changed
        BeatlesAnswer.Replace("\"Albums\"", "\"albums_v2\"", StringComparison.Ordinal),
        BeatlesAnswer.Replace("\"Type\"", "\"Kind\"", StringComparison.Ordinal),
        BeatlesAnswer.Replace("\"ReleaseStatuses\"", "\"Statuses\"", StringComparison.Ordinal),
        BeatlesAnswer.Replace("\"SecondaryTypes\": []", "\"SecondaryTypes\": null", StringComparison.Ordinal),
        BeatlesAnswer.Replace("\"ReleaseDate\": \"1969-09-26\"", "\"ReleaseDate\": 1969", StringComparison.Ordinal),
    };

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task Client_AnUnexpectedShape_IsNoAnswer_NotAnEmptyArtist_AndIsNotKept(string body)
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var healthy = false;
        var (client, http) = Client(_ => Answer(healthy ? BeatlesAnswer : body), () => now);

        Assert.Null(await client.GetArtistAsync(Beatles, CancellationToken.None));
        Assert.Single(http.Requests);

        // the service is back: the next answer is the real one, once the short pause is over
        healthy = true;
        now = now.AddMinutes(2);
        Assert.Equal(26, (await client.GetArtistAsync(Beatles, CancellationToken.None))!.Albums.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Client_WhenDown_AsksOnce_ThenLeavesItAloneForAMinute(HttpStatusCode status)
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var (client, http) = Client(_ => Answer("{}", status), () => now);

        Assert.Null(await client.GetArtistAsync(Beatles, CancellationToken.None));
        Assert.Null(await client.GetArtistAsync(BrokeForFree, CancellationToken.None));
        Assert.Null(await client.GetArtistAsync(Beatles, CancellationToken.None));
        Assert.Single(http.Requests);

        now = now.AddSeconds(61);
        Assert.Null(await client.GetArtistAsync(Beatles, CancellationToken.None));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Client_AnUnknownArtist_IsNoAnswer_WithoutPausing_AndAnArtistWithoutAlbumsIsNotKept()
    {
        var withoutAlbums = JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = BrokeForFree, ["artistname"] = "Broke For Free", ["Albums"] = Array.Empty<object>() });
        var (client, http) = Client(request => request.RequestUri!.AbsolutePath.EndsWith(Beatles, StringComparison.Ordinal)
            ? Answer("{\"error\":\"Resource not found\"}", HttpStatusCode.NotFound)
            : Answer(withoutAlbums));

        Assert.Null(await client.GetArtistAsync(Beatles, CancellationToken.None));
        Assert.Null(await client.GetArtistAsync(BrokeForFree, CancellationToken.None));
        Assert.Null(await client.GetArtistAsync(BrokeForFree, CancellationToken.None));
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task Client_NoAnswerInTime_IsNoAnswer_WhenTheCallerGivesUp_ItGivesUpToo()
    {
        var http = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Answer(BeatlesAnswer);
        });
        var client = new LidarrMetadataClient(new HttpClient(http), "1", NullLogger.Instance, baseUrl: "https://metadata.test", timeout: TimeSpan.FromMilliseconds(30));
        Assert.Null(await client.GetArtistAsync(Beatles, CancellationToken.None));

        var other = new LidarrMetadataClient(new HttpClient(http), "1", NullLogger.Instance, baseUrl: "https://metadata.test", timeout: TimeSpan.FromMinutes(1));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.GetArtistAsync(Beatles, caller.Token));
    }

    // ---- the artist page

    [Fact]
    public async Task Page_IsTheWholeOfferedList_StudioAlbumsFirst_FromOneRequest_WithoutAPerAlbumLookup()
    {
        using var env = Env();
        env.Config.HideEps = false;
        var metadata = new FakeMetadata(_ => Parsed(BeatlesAnswer));
        var mb = new FakeBrowse();

        var result = await Catalog(env, mb, metadata).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "Let It Be", "Abbey Road", "Revolver", "Please Please Me",
                "Norwegian Wood", "Help!", "The Beatles’ Hits",
                "1964 US Albums in Mono", "Let It Be… Naked", "1", "Yellow Submarine Songtrack", "Anthology 1", "Live at the BBC", "The Beatles at the Hollywood Bowl", "Help!",
                "The Alternate Magical Mystery Tour",
            },
            Titles(result.Albums));
        Assert.Equal(new[] { Beatles }, metadata.Asked);
        Assert.Equal(1, mb.Browses);
        Assert.Empty(env.Lidarr.LookupTerms);
        Assert.Equal(new[] { "lidarr:" + Beatles }, env.Lidarr.SearchTerms);
    }

    [Theory]
    [InlineData("Hot as Sun", "Album")] // a bootleg
    [InlineData("Hot as Sun", "Other")] // promotional, and of a hidden type
    [InlineData("Kinfauns Demos", "Album")] // a bootleg
    [InlineData("The Capitol Albums, Volume 1 Sampler", "Album")] // promotional
    [InlineData("Norwegian Wood", "Album")] // no known status and no date
    [InlineData("The Beatles’ Story", "Album")] // an interview
    [InlineData("Interview Disc", "Other")]
    [InlineData("Posters, Incense and Strobe Candles", "Broadcast")]
    [InlineData("The Beatles’ Movie Medley", "Single")] // hidden by default
    public async Task Page_LeavesOut(string title, string type)
    {
        using var env = Env();
        var answer = Parsed(BeatlesAnswer);
        Assert.Contains(answer.Albums, a => a.Title == title && a.Type == type);

        var result = await Catalog(env, new FakeBrowse(), new FakeMetadata(_ => answer)).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.DoesNotContain(result.Albums, a => a.Title == title && a.AlbumType == type);
    }

    [Fact]
    public async Task Page_FollowsTheSettings_ForSinglesAndEps()
    {
        using var env = Env();
        env.Config.HideEps = true;
        var catalog = Catalog(env, new FakeBrowse(), new FakeMetadata(_ => Parsed(BeatlesAnswer)));

        var noEps = await catalog.ArtistAlbumsAsync(Beatles, null, CancellationToken.None);
        Assert.DoesNotContain(noEps.Albums, a => a.AlbumType == "EP");

        env.Config.HideEps = false;
        env.Config.HideSingles = false;
        var all = await catalog.ArtistAlbumsAsync(Beatles, null, CancellationToken.None);
        Assert.Contains(all.Albums, a => a.AlbumType == "EP");
        Assert.Contains(all.Albums, a => a.AlbumType == "Single" && a.Title == "Let It Be");
    }

    [Fact]
    public async Task Page_AnAlbumWithoutAnyKnownStatus_StaysWhenItHasADate()
    {
        using var env = Env(BrokeForFree, "Broke For Free");
        var result = await Catalog(env, new FakeBrowse(), new FakeMetadata(_ => Parsed(Read("metadata_artist_broke_for_free.json"), BrokeForFree)))
            .ArtistAlbumsAsync(BrokeForFree, null, CancellationToken.None);

        // Lidarr and MusicBrainz list these real albums too, with no status in the service's answer
        Assert.Contains("Slam Funk", Titles(result.Albums));
        Assert.Contains("Directionless", Titles(result.Albums));
    }

    [Fact]
    public async Task Page_AHugeArtist_IsCompleteOnTheFirstAnswer()
    {
        // the size of the real list of The Beatles, in the shape of the real one
        var albums = Enumerable.Range(0, 1042).Select(i => new Dictionary<string, object?>
        {
            ["Id"] = $"00000000-0000-0000-0000-{i:D12}",
            ["OldIds"] = Array.Empty<string>(),
            ["Title"] = $"Album {i:D4}",
            ["Type"] = "Album",
            ["SecondaryTypes"] = i % 2 == 0 ? Array.Empty<string>() : new[] { "Compilation" },
            ["ReleaseStatuses"] = new[] { "Official" },
            ["ReleaseDate"] = "1970-01-01",
            ["Rating"] = null,
        });
        var body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = Beatles, ["artistname"] = "The Beatles", ["Albums"] = albums, ["oldids"] = Array.Empty<string>() });
        using var env = Env();
        var (client, http) = Client(_ => Answer(body));
        var mb = new FakeBrowse();

        var result = await Catalog(env, mb, client).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        // no cap on the list, and nothing kept for a second visit to fill in
        Assert.Equal(1042, result.Albums.Count);
        Assert.Single(http.Requests);
        Assert.Empty(env.Lidarr.LookupTerms);
        Assert.Equal("Album 0000", result.Albums[0].Title);
        Assert.Empty(result.Albums[0].SecondaryTypes);
        Assert.Contains("Compilation", result.Albums[^1].SecondaryTypes);
    }

    [Fact]
    public async Task Page_FillsEveryFieldTheAppReads_AndWhatItCannotKnowStaysOutOfSight()
    {
        using var env = Env();
        var result = await Catalog(env, new FakeBrowse(), new FakeMetadata(_ => Parsed(BeatlesAnswer))).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        var abbey = Assert.Single(result.Albums, a => a.Title == "Abbey Road");
        Assert.Equal(AbbeyRoad, abbey.Mbid);
        Assert.Equal(Beatles, abbey.ArtistMbid);
        Assert.Equal("The Beatles", abbey.Artist);
        Assert.Equal(1969, abbey.Year);
        Assert.Equal("Album", abbey.Type);
        Assert.Equal("Album", abbey.AlbumType);
        Assert.Empty(abbey.SecondaryTypes);
        Assert.Equal("absent", abbey.State);
        Assert.Null(abbey.RequestId);
        Assert.Equal("1969-09-26T00:00:00Z", abbey.ReleaseDate);
        Assert.Equal($"https://coverartarchive.org/release-group/{AbbeyRoad}/front-500", abbey.CoverUrl);

        // the app shows a track count above 0, a disc count above 1, a label and a disambiguation when not empty, and takes a year of 0 for none
        Assert.Equal(0, abbey.TrackCount);
        Assert.Equal(1, abbey.DiscCount);
        Assert.Null(abbey.Label);
        Assert.Equal(string.Empty, abbey.Disambiguation);
        Assert.Null(abbey.Country);

        var undated = Assert.Single(result.Albums, a => a.Title == "The Alternate Magical Mystery Tour");
        Assert.Equal(0, undated.Year);
        Assert.Null(undated.ReleaseDate);

        // the album groups of the app: from the secondary types alone, disc counts being unknown here
        var anthology = Assert.Single(result.Albums, a => a.Title == "Anthology 1");
        Assert.Contains("Compilation", anthology.SecondaryTypes);
        Assert.Equal(1, anthology.DiscCount);
        Assert.Contains("Live", Assert.Single(result.Albums, a => a.Title == "Live at the BBC").SecondaryTypes);
        Assert.Contains("Soundtrack", Assert.Single(result.Albums, a => a.Title == "Help!" && a.AlbumType == "Album").SecondaryTypes);

        // the artist the page shows comes from Lidarr's lookup, as for any other artist
        Assert.Equal("The Beatles", result.Artist!.Name);
    }

    [Fact]
    public async Task Page_WhenLidarrDoesNotKnowTheArtist_TheServiceNamesIt()
    {
        using var env = new TestEnv();
        var result = await Catalog(env, new FakeBrowse(), new FakeMetadata(_ => Parsed(BeatlesAnswer))).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.Equal("The Beatles", result.Artist!.Name);
        Assert.Equal("Group", result.Artist.ArtistType);
        Assert.Contains(result.Artist.Images, i => i.CoverType == "poster");
    }

    [Fact]
    public async Task Page_TheStateOfEachAlbum_ComesFromTheRequestsAndTheLibrary_InOneQuestion()
    {
        using var env = Env();
        var library = new CountingLibrary(env.Library);
        env.Library.KnownTitles.Add("Revolver");
        await env.Service.CreateAsync(new Requests.NewRequest(TestEnv.User, "name", AbbeyRoad, Beatles, "Abbey Road", "The Beatles", 1969, string.Empty), CancellationToken.None);
        var catalog = new CatalogService(env.Lidarr, env.Store, library, () => env.Config, NullLogger<CatalogService>.Instance, new FakeBrowse(), metadata: new FakeMetadata(_ => Parsed(BeatlesAnswer)));

        var result = await catalog.ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.Equal("library", Assert.Single(result.Albums, a => a.Title == "Revolver").State);
        var requested = Assert.Single(result.Albums, a => a.Title == "Abbey Road");
        Assert.NotEqual("absent", requested.State);
        Assert.NotNull(requested.RequestId);
        Assert.Equal("absent", Assert.Single(result.Albums, a => a.Title == "Let It Be").State);
        Assert.Equal(1, library.Questions);
        Assert.DoesNotContain(library.Asked, a => a.ForeignAlbumId == AbbeyRoad);
    }

    [Fact]
    public async Task Page_WhenTheLibraryFails_TheAlbumsAreStillListed()
    {
        using var env = Env();
        var catalog = new CatalogService(env.Lidarr, env.Store, new CountingLibrary(env.Library) { Fails = true }, () => env.Config, NullLogger<CatalogService>.Instance, new FakeBrowse(), metadata: new FakeMetadata(_ => Parsed(BeatlesAnswer)));

        var result = await catalog.ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.NotEmpty(result.Albums);
        Assert.All(result.Albums, a => Assert.Equal("absent", a.State));
    }

    [Fact]
    public async Task Page_AnArtistLidarrHolds_DoesNotAskTheService()
    {
        using var env = new TestEnv();
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Artist = new LidarrArtist { Id = 7, ForeignArtistId = Beatles, ArtistName = "The Beatles" } } };
        var metadata = new FakeMetadata(_ => Parsed(BeatlesAnswer));

        await Catalog(env, new FakeBrowse(), metadata).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.Empty(metadata.Asked);
    }

    // ---- the joint albums the service does not list

    private const string DjSnake = "31445b1e-96f3-4c7f-84ec-dd02e647e690";
    private const string MajorLazer = "75be165a-ad83-4d12-bd28-f589a15c479f";
    private const string LeanOnEp = "c169317f-03f3-4082-a909-75d4fad3c0aa";
    private const string MyBonnie = "b1800708-68c4-3dd8-9698-77ba1a0645f5";

    private static MbReleaseGroup LeanOn => new(LeanOnEp, "Lean On EP (10th Anniversary)", "EP", Array.Empty<string>(), 2025, ArtistId: MajorLazer, ArtistName: "Major Lazer", Date: "2025-12-25");

    private static MetadataArtist DjSnakeListed => new(
        DjSnake, "DJ Snake", string.Empty, "Person", Array.Empty<string>(), Array.Empty<MetadataImage>(),
        new[] { new MetadataAlbum("nomad", "Nomad", "Album", Array.Empty<string>(), new[] { "Official" }, "2025-11-07", Array.Empty<string>()) });

    [Fact]
    public async Task Page_AddsTheJointAlbumsTheServiceDoesNotList_InTheNameOfTheArtistCreditedFirst_WithoutALookup_AndARequestNamesThatArtist()
    {
        using var env = Env(DjSnake, "DJ Snake");
        var mb = new FakeBrowse { Groups = new[] { LeanOn, new MbReleaseGroup("nomad", "Nomad", "Album", Array.Empty<string>(), 2025, ArtistId: DjSnake, ArtistName: "DJ Snake") } };

        var page = await Catalog(env, mb, new FakeMetadata(_ => DjSnakeListed)).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "Nomad", "Lean On EP (10th Anniversary)" }, Titles(page.Albums));
        Assert.Empty(env.Lidarr.LookupTerms);
        Assert.Equal("DJ Snake", page.Artist!.Name);
        var album = page.Albums[1];
        Assert.Equal(MajorLazer, album.ArtistMbid);
        Assert.Equal("Major Lazer", album.Artist);
        Assert.Equal(2025, album.Year);
        Assert.Equal("2025-12-25T00:00:00Z", album.ReleaseDate);
        Assert.Equal($"https://coverartarchive.org/release-group/{LeanOnEp}/front-500", album.CoverUrl);

        await env.Service.CreateAsync(new Requests.NewRequest(TestEnv.User, "name", album.Mbid, album.ArtistMbid, album.Title, album.Artist, album.Year, album.CoverUrl), CancellationToken.None);
        var added = Assert.Single(env.Lidarr.Added);
        Assert.Equal(LeanOnEp, added.ForeignAlbumId);
        Assert.Equal(MajorLazer, added.Artist.ForeignArtistId);
    }

    [Fact]
    public async Task Page_AJointAlbum_FollowsTheSettings_AndAnInterviewStaysOut_AndAMergedIdIsNoNewAlbum()
    {
        using var env = Env(DjSnake, "DJ Snake");
        env.Config.HideEps = true;
        var listed = DjSnakeListed with { Albums = new[] { new MetadataAlbum("nomad", "Nomad", "Album", Array.Empty<string>(), new[] { "Official" }, "2025-11-07", new[] { "old-nomad" }) } };
        var mb = new FakeBrowse
        {
            Groups = new[]
            {
                new MbReleaseGroup("talk", "The Celebration", "Album", new[] { "Interview" }, 1999, ArtistId: MajorLazer, ArtistName: "Major Lazer"),
                new MbReleaseGroup("old-nomad", "Nomad", "Album", Array.Empty<string>(), 2025, ArtistId: DjSnake, ArtistName: "DJ Snake"),
            },
        };

        var page = await Catalog(env, mb, new FakeMetadata(_ => listed)).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "Nomad" }, Titles(page.Albums));
    }

    [Fact]
    public async Task Search_AddsAStudioAlbumMadeJointly_ButNoOtherKindOfJointAlbum()
    {
        using var env = Env(DjSnake, "DJ Snake");
        var mb = new FakeBrowse { Groups = new[] { LeanOn, new MbReleaseGroup("joint", "Joint Album", "Album", Array.Empty<string>(), 2020, ArtistId: MajorLazer, ArtistName: "Major Lazer") } };

        var result = await Catalog(env, mb, new FakeMetadata(_ => DjSnakeListed)).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Equal(new[] { "Nomad", "Joint Album" }, Titles(result.Albums));
    }

    [Theory]
    [InlineData("Lean On EP", LeanOnEp, "Major Lazer", MajorLazer)]
    [InlineData("My Bonnie", MyBonnie, "Tony Sheridan", "7f9a3245-df19-4681-8314-4a4c1281dc74")]
    public async Task Search_ByTitle_StillFindsAJointAlbumTheServiceDoesNotList(string query, string album, string artist, string artistId)
    {
        using var env = new TestEnv();
        var found = new LidarrAlbum { ForeignAlbumId = album, Title = query, AlbumType = "EP", Artist = new LidarrArtist { ForeignArtistId = artistId, ArtistName = artist } };
        env.Lidarr.OnSearch = term => term == query ? new[] { new LidarrSearchItem { Album = found } } : Array.Empty<LidarrSearchItem>();
        var metadata = new FakeMetadata(_ => null);

        var result = await Catalog(env, new FakeBrowse(), metadata).SearchAsync(query, CancellationToken.None);

        var hit = Assert.Single(result.Albums);
        Assert.Equal(album, hit.Mbid);
        Assert.Equal(artistId, hit.ArtistMbid);
        Assert.Empty(metadata.Asked);
    }

    private sealed class SlowBrowse : IMusicBrainzApi
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Browses;

        public Exception? Failure { get; set; }

        public async Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
        {
            Interlocked.Increment(ref Browses);
            await Gate.Task.WaitAsync(ct);
            return Failure is null ? new MbReleaseGroupPage(new[] { LeanOn }, 1) : throw Failure;
        }

        public Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRecording>>(Array.Empty<MbRecording>());

        public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRelease>?>(null);

        public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct) => Task.FromResult<MbTracklist?>(null);
    }

    private static CatalogService Waiting(TestEnv env, IMusicBrainzApi mb, TimeSpan wait)
        => new(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, mb, metadata: new FakeMetadata(_ => DjSnakeListed), browseWait: wait);

    [Fact]
    public async Task Page_WhenMusicBrainzTakesLongerThanTheWait_AnswersWithTheList_AndOneBrowseGoesOn_ForTheNextVisit()
    {
        using var env = Env(DjSnake, "DJ Snake");
        var mb = new SlowBrowse();
        var catalog = Waiting(env, mb, TimeSpan.FromMilliseconds(30));

        var first = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);
        var second = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "Nomad" }, Titles(first.Albums));
        Assert.Equal(new[] { "Nomad" }, Titles(second.Albums));
        Assert.Equal(1, Volatile.Read(ref mb.Browses));

        mb.Gate.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<CatalogAlbum> later;
        do
        {
            later = (await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None)).Albums;
        }
        while (later.Count < 2 && DateTime.UtcNow < deadline);

        Assert.Equal(new[] { "Nomad", "Lean On EP (10th Anniversary)" }, Titles(later));
    }

    [Fact]
    public async Task Page_WhenTheBrowseFails_AnswersWithTheList_WithoutTheNameSearch_AndTheNextVisitBrowsesAgain()
    {
        using var env = Env(DjSnake, "DJ Snake");
        var mb = new SlowBrowse { Failure = new MusicBrainzUnavailableException("MusicBrainz answered 503", busy: true) };
        mb.Gate.SetResult();
        var catalog = Waiting(env, mb, TimeSpan.FromSeconds(5));

        var first = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "Nomad" }, Titles(first.Albums));
        Assert.Equal(new[] { "lidarr:" + DjSnake }, env.Lidarr.SearchTerms);

        mb.Failure = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<CatalogAlbum> later;
        do
        {
            later = (await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None)).Albums;
        }
        while (later.Count < 2 && DateTime.UtcNow < deadline);

        Assert.Equal(2, later.Count);
    }

    [Fact]
    public async Task MusicBrainzClient_AsksForTheFilteredBrowse_AndReadsWhoIsCreditedFirst()
    {
        const string body = """
            {"release-group-count":1,"release-groups":[{"id":"c169317f-03f3-4082-a909-75d4fad3c0aa","title":"Lean On EP (10th Anniversary)","primary-type":"EP","secondary-types":[],"first-release-date":"2025-12-25",
              "artist-credit":[{"name":"Major Lazer","joinphrase":" & ","artist":{"id":"75be165a-ad83-4d12-bd28-f589a15c479f","name":"Major Lazer"}},{"name":"DJ Snake","artist":{"id":"31445b1e-96f3-4c7f-84ec-dd02e647e690","name":"DJ Snake"}}]}]}
            """;
        var http = new Handler((_, _) => Task.FromResult(Answer(body)));
        var client = new MusicBrainzClient(new HttpClient(http), "1", NullLogger.Instance, delay: (_, _) => Task.CompletedTask, baseUrl: "https://mb.test");

        var page = await client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album", "ep" }, 0, CancellationToken.None);

        var query = Assert.Single(http.Requests).RequestUri!.Query;
        Assert.Contains("release-group-status=website-default", query, StringComparison.Ordinal);
        Assert.Contains("inc=artist-credits", query, StringComparison.Ordinal);
        var group = Assert.Single(page.Groups);
        Assert.Equal(MajorLazer, group.ArtistId);
        Assert.Equal("Major Lazer", group.ArtistName);
        Assert.Equal("2025-12-25", group.Date);
    }

    // ---- when the service gives no answer

    [Fact]
    public async Task Page_WhenTheServiceGivesNoAnswer_FallsBackOnMusicBrainz_WithALookupPerAlbum()
    {
        using var env = Env();
        env.Lidarr.OnLookup = term => term == "lidarr:" + AbbeyRoad
            ? new[] { new LidarrAlbum { ForeignAlbumId = AbbeyRoad, Title = "Abbey Road", AlbumType = "Album", Artist = new LidarrArtist { ForeignArtistId = Beatles, ArtistName = "The Beatles" }, Releases = new List<LidarrRelease> { new() { Status = "Official", MediumCount = 1, TrackCount = 17 } } } }
            : Array.Empty<LidarrAlbum>();
        var mb = new FakeBrowse { Groups = new[] { new MbReleaseGroup(AbbeyRoad, "Abbey Road", "Album", Array.Empty<string>(), 1969) } };
        var (down, http) = Client(_ => Answer("{}", HttpStatusCode.BadGateway));

        var result = await Catalog(env, mb, down).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);

        Assert.Equal(17, Assert.Single(result.Albums).TrackCount);
        Assert.Equal(1, mb.Browses);
        Assert.Equal(new[] { "lidarr:" + AbbeyRoad }, env.Lidarr.LookupTerms);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Search_WhenTheServiceGivesNoAnswer_FallsBackOnMusicBrainz()
    {
        using var env = Env();
        env.Lidarr.OnLookup = term => term == "lidarr:" + AbbeyRoad
            ? new[] { new LidarrAlbum { ForeignAlbumId = AbbeyRoad, Title = "Abbey Road", AlbumType = "Album", Artist = new LidarrArtist { ForeignArtistId = Beatles, ArtistName = "The Beatles" }, Releases = new List<LidarrRelease> { new() { Status = "Official" } } } }
            : Array.Empty<LidarrAlbum>();
        var mb = new FakeBrowse { Groups = new[] { new MbReleaseGroup(AbbeyRoad, "Abbey Road", "Album", Array.Empty<string>(), 1969) } };

        var result = await Catalog(env, mb, new FakeMetadata(_ => null)).SearchAsync("The Beatles", CancellationToken.None);

        Assert.Equal("Abbey Road", Assert.Single(result.Albums).Title);
        Assert.Equal(1, mb.Browses);
    }

    // ---- the search

    [Fact]
    public async Task Search_PutsAllTheStudioAlbumsOfTheArtistNamedFirst_NewestFirst_WithoutALookup()
    {
        using var env = Env();
        var metadata = new FakeMetadata(_ => Parsed(BeatlesAnswer));
        var mb = new FakeBrowse();

        var result = await Catalog(env, mb, metadata).SearchAsync("the beatles", CancellationToken.None);

        Assert.Equal(new[] { "Let It Be", "Abbey Road", "Revolver", "Please Please Me" }, Titles(result.Albums));
        Assert.DoesNotContain(env.Lidarr.LookupTerms, t => t.StartsWith("lidarr:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_ThatNamesNoArtist_DoesNotAskTheService()
    {
        using var env = Env();
        var metadata = new FakeMetadata(_ => Parsed(BeatlesAnswer));

        await Catalog(env, new FakeBrowse(), metadata).SearchAsync("abbey road", CancellationToken.None);

        Assert.Empty(metadata.Asked);
    }

    // ---- a request

    [Fact]
    public async Task Request_OfAListedAlbum_OfAnArtistLidarrDoesNotHold_TakesTheUsualPathThroughLidarr()
    {
        using var env = Env();
        var page = await Catalog(env, new FakeBrowse(), new FakeMetadata(_ => Parsed(BeatlesAnswer))).ArtistAlbumsAsync(Beatles, null, CancellationToken.None);
        var album = Assert.Single(page.Albums, a => a.Title == "Abbey Road");

        // what the app posts: the fields of the album as the page gave it
        var input = new Requests.NewRequest(TestEnv.User, "name-" + TestEnv.User, album.Mbid, album.ArtistMbid, album.Title, album.Artist, album.Year, album.CoverUrl);
        var request = await env.Service.CreateAsync(input, CancellationToken.None);

        var added = Assert.Single(env.Lidarr.Added);
        Assert.Equal(AbbeyRoad, added.ForeignAlbumId);
        Assert.True(added.Monitored);
        Assert.True(added.AnyReleaseOk);
        Assert.False(added.AddOptions.SearchForNewAlbum);
        Assert.Equal(Beatles, added.Artist.ForeignArtistId);
        Assert.Equal(2, added.Artist.QualityProfileId);
        Assert.Equal(1, added.Artist.MetadataProfileId);
        Assert.Equal("/data/media/music", added.Artist.RootFolderPath);
        Assert.Equal("none", added.Artist.MonitorNewItems);
        Assert.Equal("none", added.Artist.AddOptions.Monitor);
        Assert.False(added.Artist.AddOptions.SearchForMissingAlbums);
        Assert.Equal(new[] { 101 }, env.Lidarr.Searched);
        Assert.Equal(album.CoverUrl, request.CoverUrl);
    }

    // ---- the allow list

    [Fact]
    public async Task AllowList_NeverReachesTheService_AndBrokeForFreeGivesDirectionlessOnly()
    {
        using var env = new TestEnv();
        env.Config.CatalogAllowList = Directionless;
        env.Lidarr.OnSearch = _ => JsonSerializer.Deserialize<List<LidarrSearchItem>>(Read("lidarr_search_directionless.json"), Web)!;
        env.Lidarr.OnLookup = term => JsonSerializer.Deserialize<List<LidarrAlbum>>(Read("lidarr_lookup_directionless.json"), Web)!
            .Where(a => !term.StartsWith("lidarr:", StringComparison.Ordinal) || a.ForeignAlbumId == term["lidarr:".Length..])
            .ToList();
        var metadata = new FakeMetadata(_ => Parsed(Read("metadata_artist_broke_for_free.json"), BrokeForFree));
        var mb = new FakeBrowse();
        var catalog = Catalog(env, mb, metadata);

        var page = await catalog.ArtistAlbumsAsync(BrokeForFree, "Broke For Free", CancellationToken.None);
        var search = await catalog.SearchAsync("Broke For Free", CancellationToken.None);

        Assert.Equal(Directionless, Assert.Single(page.Albums).Mbid);
        Assert.Equal(Directionless, Assert.Single(search.Albums).Mbid);
        Assert.Empty(metadata.Asked);

        // no MusicBrainz browse either: the allow list answers from its own albums
        Assert.Equal(0, mb.Browses);
    }

    // ---- the library

    [Fact]
    public void Library_TellsWhichAlbumsItHolds_OnTheTermsOfASingleLookup()
    {
        var library = new[]
        {
            new LibraryAlbum(new[] { AbbeyRoad.ToUpperInvariant() }, "Abbey Road (2019 mix)", "Another Name"),
            new LibraryAlbum(Array.Empty<string>(), "revolver", "the beatles"),
            new LibraryAlbum(Array.Empty<string>(), "Rubber Soul", "A Namesake"),
        };
        var wanted = new[]
        {
            new AlbumKey(AbbeyRoad, "Abbey Road", "The Beatles"),
            new AlbumKey("revolver-id", "Revolver", "The Beatles"),
            new AlbumKey("rubber-id", "Rubber Soul", "The Beatles"),
            new AlbumKey("let-it-be-id", "Let It Be", "The Beatles"),
            new AlbumKey("blank-title-id", string.Empty, string.Empty),
        };

        var held = LibraryAlbum.Held(library, wanted);

        Assert.Equal(2, held.Count);
        Assert.Contains(AbbeyRoad, held);
        Assert.Contains("REVOLVER-ID", held);
    }

    private sealed class CountingLibrary(IJellyfinLibrary inner) : IJellyfinLibrary
    {
        public int Questions { get; private set; }

        public List<AlbumKey> Asked { get; } = new();

        public bool Fails { get; init; }

        public IReadOnlySet<string> FindAlbums(IReadOnlyList<AlbumKey> albums)
        {
            if (Fails)
            {
                throw new InvalidOperationException("the database is locked");
            }

            Questions++;
            Asked.AddRange(albums);
            return inner.FindAlbums(albums);
        }

        public string? FindAlbum(string foreignAlbumId, string title, string artist) => throw new InvalidOperationException("one question per album is what this avoids");

        public string? FindAlbumByPath(string jellyfinFolder) => inner.FindAlbumByPath(jellyfinFolder);

        public void RefreshPath(string jellyfinFolder) => inner.RefreshPath(jellyfinFolder);

        public void ReportPathChanged(string path) => inner.ReportPathChanged(path);

        public bool MarkFavorite(string userId, string itemId) => inner.MarkFavorite(userId, itemId);

        public JellyfinUserInfo? GetUser(string userId) => inner.GetUser(userId);
    }
}
