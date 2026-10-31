using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Lidarr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>An artist Lidarr does not hold still shows its albums. Fixtures are trimmed answers of the real MusicBrainz and a real Lidarr, for an artist neither title nor name search can find.</summary>
public class ArtistDiscographyTests
{
    private const string DjSnake = "31445b1e-96f3-4c7f-84ec-dd02e647e690";
    private const string Namesake = "c6634121-d420-4a77-b6de-751211f78526";
    private const string Encore = "5c631dec-2278-4dbb-80c0-a8649aff0d8c";
    private const string CarteBlanche = "8cd49547-ec41-47e9-84f5-e707a604dc75";
    private const string Nomad = "46f1c4fe-924a-4a24-8c1a-899c6f677fb2";
    private const string Presents = "582d8cdb-306b-41ea-bbd0-4fad0964663c";
    private const string ShangChi = "512af5da-2aef-41df-95ee-3e856bcacbc5";
    private const string LeanOnEp = "c169317f-03f3-4082-a909-75d4fad3c0aa";
    private const string BrokeForFree = "069a1c1f-14eb-4d36-b0a0-77dffbd67713";
    private const string Directionless = "ce0eb31a-142a-4ac8-9cb9-6a3adb3ad19e";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static T Fixture<T>(string name) => JsonSerializer.Deserialize<T>(Read(name), Json)!;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Urls)
            {
                Urls.Add(request.RequestUri!.PathAndQuery);
            }

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The real client over a canned transport: pace and cache are those of production, without the waiting.</summary>
    private static (MusicBrainzClient Client, Handler Http) RealMusicBrainz(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var http = new Handler(respond ?? (_ => Ok(Read("musicbrainz_browse_dj_snake.json"))));
        return (new MusicBrainzClient(new HttpClient(http), "1.0.0.0", NullLogger.Instance, delay: (_, _) => Task.CompletedTask, baseUrl: "https://mb.test"), http);
    }

    private sealed class FakeBrowse : IMusicBrainzApi
    {
        public Func<int, CancellationToken, Task<MbReleaseGroupPage>> Answer { get; init; } = (_, _) => Task.FromResult(new MbReleaseGroupPage(Array.Empty<MbReleaseGroup>(), 0));

        public List<(string Artist, IReadOnlyList<string> Types, int Offset)> Calls { get; } = new();

        public int RecordingSearches { get; private set; }

        public Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((artistId, types, offset));
            }

            return Answer(offset, ct);
        }

        public Func<TrackQuery, IReadOnlyList<MbRecording>> Recordings { get; init; } = _ => Array.Empty<MbRecording>();

        public Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct)
        {
            RecordingSearches++;
            return Task.FromResult(Recordings(query));
        }

        public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRelease>?>(null);

        public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct) => Task.FromResult<MbTracklist?>(null);
    }

    private sealed class Hints(params string[] artists) : ITrackHints
    {
        public Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artists);
    }

    /// <summary>A Lidarr that answers as the real one does for an artist it does not hold: the artist alone for its id, the titles for its name.</summary>
    private static TestEnv Production()
    {
        var env = new TestEnv();
        var known = Fixture<List<LidarrAlbum>>("lidarr_lookup_dj_snake_albums.json");
        env.Lidarr.OnSearch = term => term.StartsWith("lidarr:", StringComparison.Ordinal)
            ? Fixture<List<LidarrSearchItem>>("lidarr_search_dj_snake.json").Take(1).ToList()
            : Fixture<List<LidarrSearchItem>>("lidarr_search_dj_snake.json");
        env.Lidarr.OnLookup = term => term.StartsWith("lidarr:", StringComparison.Ordinal)
            ? known.Where(a => a.ForeignAlbumId == term["lidarr:".Length..]).ToList()
            : Array.Empty<LidarrAlbum>();
        return env;
    }

    private static CatalogService Catalog(TestEnv env, IMusicBrainzApi? musicBrainz, TimeSpan? budget = null, ITrackHints? hints = null)
        => new(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, musicBrainz, hints: hints, discographyBudget: budget);

    private static MbReleaseGroup Group(string id, int year = 2000, string type = "Album", params string[] secondary)
        => new(id, "title " + id, type, secondary, year);

    private static LidarrAlbum Album(string id, string type = "Album", string status = "Official", int releases = 1, string artist = DjSnake, string[]? secondary = null, string date = "2000-01-01T00:00:00Z")
        => new()
        {
            ForeignAlbumId = id,
            Title = "title " + id,
            AlbumType = type,
            ReleaseDate = date,
            SecondaryTypes = secondary?.ToList(),
            Releases = Enumerable.Range(0, releases).Select(_ => new LidarrRelease { Status = status, MediumCount = 1, TrackCount = 10 }).ToList(),
            Artist = new LidarrArtist { ForeignArtistId = artist, ArtistName = "DJ Snake" },
        };

    private static FakeBrowse Browsing(params MbReleaseGroup[] groups)
        => new() { Answer = (_, _) => Task.FromResult(new MbReleaseGroupPage(groups, groups.Length)) };

    // ---- MusicBrainz's browse

    [Fact]
    public async Task Client_AsksPolitely_AndReadsTheRealAnswer()
    {
        var (client, http) = RealMusicBrainz();

        var page = await client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album", "ep" }, 0, CancellationToken.None);
        await client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album", "ep" }, 0, CancellationToken.None);

        Assert.Equal($"/ws/2/release-group?fmt=json&artist={DjSnake}&type=album%7Cep&limit=100&offset=0", Assert.Single(http.Urls));
        Assert.Equal(12, page.Total);
        var encore = Assert.Single(page.Groups, g => g.Id == Encore);
        Assert.Equal(("Encore", "Album", 2016), (encore.Title, encore.PrimaryType, encore.Year));
        Assert.Empty(encore.SecondaryTypes);
        Assert.Equal(new[] { "Compilation", "Soundtrack" }, Assert.Single(page.Groups, g => g.Id == ShangChi).SecondaryTypes);
    }

    [Fact]
    public async Task Client_ADifferentPageOrFilter_IsAnotherQuestion()
    {
        var (client, http) = RealMusicBrainz();

        await client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album", "ep" }, 0, CancellationToken.None);
        await client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album", "ep" }, 100, CancellationToken.None);
        await client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album" }, 0, CancellationToken.None);

        Assert.Equal(3, http.Urls.Count);
    }

    [Fact]
    public async Task Client_GarbageIsAFailure()
    {
        var (client, _) = RealMusicBrainz(_ => Ok("<html>nope</html>"));

        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album" }, 0, CancellationToken.None));
    }

    // ---- the artist page

    [Fact]
    public async Task Page_DjSnake_ShowsTheStudioAlbums_NewestFirst_WithoutSearchingByName()
    {
        using var env = Production();
        var (mb, http) = RealMusicBrainz();

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { Nomad, CarteBlanche, Encore }, result.Albums.Take(3).Select(a => a.Mbid));
        Assert.Equal("DJ Snake", result.Artist!.Name);
        Assert.All(result.Albums.Take(3), a => Assert.Equal(("DJ Snake", "absent"), (a.Artist, a.State)));
        Assert.Single(http.Urls);
        Assert.DoesNotContain("DJ Snake", env.Lidarr.SearchTerms);
    }

    [Fact]
    public async Task Page_CompilationsAndSoundtracks_KeepTheSecondaryTypesTheAppGroupsThemBy_AndTheirOwnArtist()
    {
        using var env = Production();

        var result = await Catalog(env, RealMusicBrainz().Client).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "Compilation" }, Assert.Single(result.Albums, a => a.Mbid == Presents).SecondaryTypes);
        var shared = Assert.Single(result.Albums, a => a.Mbid == ShangChi);
        Assert.Equal(new[] { "Compilation", "Soundtrack" }, shared.SecondaryTypes);
        Assert.Equal("88rising", shared.Artist);
    }

    [Fact]
    public async Task Page_SinglesAreNeverAskedFor_AndAHiddenTypeIsNotShownEvenIfLidarrCallsItSo()
    {
        using var env = Production();
        var mb = Browsing(Group("a"), Group("b"));
        env.Lidarr.OnLookup = term => term == "lidarr:a" ? new[] { Album("a") } : new[] { Album("b", type: "Single") };

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "album", "ep" }, mb.Calls[0].Types);
        Assert.Equal("a", Assert.Single(result.Albums).Mbid);
    }

    [Fact]
    public async Task Page_HideEps_DoesNotAskForThem()
    {
        using var env = Production();
        env.Config.HideEps = true;
        var (mb, http) = RealMusicBrainz();

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Contains("type=album&", Assert.Single(http.Urls), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Albums, a => a.Mbid == LeanOnEp);
    }

    [Fact]
    public async Task Page_SinglesShown_AreAskedFor_AfterTheAlbums()
    {
        using var env = Production();
        env.Config.HideSingles = false;
        var mb = Browsing(Group("s", 2020, "Single"), Group("a", 2000));
        env.Lidarr.OnLookup = term => new[] { term == "lidarr:s" ? Album("s", type: "Single") : Album("a") };

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { "album", "ep", "single" }, mb.Calls[0].Types);
        Assert.Equal(new[] { "a", "s" }, result.Albums.Select(a => a.Mbid));
    }

    [Fact]
    public async Task Page_BrowseFails_ShowsWhatTheSearchByNameFinds()
    {
        using var env = Production();
        var (mb, _) = RealMusicBrainz(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        // today's answer, the third-party singles hidden
        Assert.Equal(Presents, Assert.Single(result.Albums).Mbid);
        Assert.Equal("DJ Snake", result.Artist!.Name);
    }

    [Fact]
    public async Task Page_ALookupFails_ShowsTheOthers_AndWhatTheSearchByNameAdds_AndAsksAgainNextTime()
    {
        using var env = Production();
        var mb = RealMusicBrainz().Client;
        var failing = true;
        env.Lidarr.BeforeLookup = (term, _) => failing && term == "lidarr:" + Nomad ? throw new LidarrException("down") : Task.CompletedTask;
        var catalog = Catalog(env, mb);

        var first = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.DoesNotContain(first.Albums, a => a.Mbid == Nomad);
        Assert.Contains(first.Albums, a => a.Mbid == Encore);
        Assert.Contains("DJ Snake", env.Lidarr.SearchTerms);

        failing = false;
        var second = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(Nomad, second.Albums[0].Mbid);
    }

    [Fact]
    public async Task Page_BudgetExceeded_KeepsWhatResolved_InOrder()
    {
        using var env = Production();
        env.Lidarr.BeforeLookup = async (term, ct) =>
        {
            if (term == "lidarr:" + CarteBlanche)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };

        var result = await Catalog(env, RealMusicBrainz().Client, TimeSpan.FromMilliseconds(150)).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Contains(result.Albums, a => a.Mbid == Nomad);
        Assert.Contains(result.Albums, a => a.Mbid == Encore);
        Assert.DoesNotContain(result.Albums, a => a.Mbid == CarteBlanche);
    }

    [Fact]
    public async Task Page_BudgetCutShort_GoesFurtherTheNextTime_FromWhatItKept()
    {
        using var env = Production();
        var slow = true;
        env.Lidarr.BeforeLookup = async (term, ct) =>
        {
            if (slow && term == "lidarr:" + CarteBlanche)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var catalog = Catalog(env, RealMusicBrainz().Client, TimeSpan.FromMilliseconds(150));

        await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);
        slow = false;
        var before = env.Lidarr.LookupTerms.Count(t => t.StartsWith("lidarr:", StringComparison.Ordinal));
        var second = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(1, env.Lidarr.LookupTerms.Count(t => t.StartsWith("lidarr:", StringComparison.Ordinal)) - before);
        Assert.Contains(second.Albums, a => a.Mbid == CarteBlanche);
    }

    [Fact]
    public async Task Page_BrowseStuck_DoesNotHoldThePageBeyondTheBudget()
    {
        using var env = Production();
        var mb = new FakeBrowse
        {
            Answer = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException();
            },
        };

        var result = await Catalog(env, mb, TimeSpan.FromMilliseconds(100)).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(Presents, Assert.Single(result.Albums).Mbid);
    }

    [Fact]
    public async Task Page_TheCallerGivingUp_IsNotSwallowed()
    {
        using var env = Production();
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var mb = new FakeBrowse { Answer = (_, ct) => Task.FromCanceled<MbReleaseGroupPage>(ct) };
        env.Lidarr.OnSearch = _ => Fixture<List<LidarrSearchItem>>("lidarr_search_dj_snake.json").Take(1).ToList();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, gone.Token));
    }

    [Fact]
    public async Task Page_Pages_AndAFailedOne_KeepsTheFirst()
    {
        using var env = Production();
        var mb = new FakeBrowse
        {
            Answer = (offset, _) => offset switch
            {
                0 => Task.FromResult(new MbReleaseGroupPage(new[] { Group("first") }, 250)),
                100 => Task.FromResult(new MbReleaseGroupPage(new[] { Group("second") }, 250)),
                _ => throw new MusicBrainzUnavailableException("down"),
            },
        };
        env.Lidarr.OnLookup = term => term.StartsWith("lidarr:", StringComparison.Ordinal) ? new[] { Album(term["lidarr:".Length..]) } : Array.Empty<LidarrAlbum>();

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(new[] { 0, 100, 200 }, mb.Calls.Select(c => c.Offset));
        Assert.Contains(result.Albums, a => a.Mbid == "first");
        Assert.Contains(result.Albums, a => a.Mbid == "second");
    }

    [Fact]
    public async Task Page_Answers_AreKept()
    {
        using var env = Production();
        var (mb, http) = RealMusicBrainz();
        var catalog = Catalog(env, mb);

        await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);
        var lookups = env.Lidarr.LookupTerms.Count;
        var again = await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal(lookups, env.Lidarr.LookupTerms.Count);
        Assert.Single(http.Urls);
        Assert.Equal(Nomad, again.Albums[0].Mbid);
    }

    [Fact]
    public async Task Page_TheStateIsToday_NotThatOfTheKeptAnswer()
    {
        using var env = Production();
        var catalog = Catalog(env, RealMusicBrainz().Client);

        Assert.Equal("absent", (await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None)).Albums[0].State);
        env.Library.KnownTitles.Add("Nomad");

        Assert.Equal("library", (await catalog.ArtistAlbumsAsync(DjSnake, null, CancellationToken.None)).Albums[0].State);
    }

    [Fact]
    public async Task Page_AnAlbumKnownFromBootlegsAlone_IsLeftOut()
    {
        using var env = Production();
        var mb = Browsing(Group("real"), Group("mixed"), Group("bootleg"));
        env.Lidarr.OnLookup = term => term switch
        {
            "lidarr:real" => new[] { Album("real") },
            "lidarr:mixed" => new[] { Album("mixed", status: "Promotion") },
            "lidarr:bootleg" => new[] { Album("bootleg", status: "Bootleg") },
            _ => Array.Empty<LidarrAlbum>(),
        };

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.DoesNotContain(result.Albums, a => a.Mbid == "bootleg");
        Assert.Equal(2, result.Albums.Count);
    }

    [Fact]
    public async Task Page_ALargeDiscography_LooksUpTheStudioAlbums_BeforeAnythingElse_TheOldestFirst()
    {
        using var env = Production();
        var groups = Enumerable.Range(0, 300).Select(i => Group("live" + i, 2020, "Album", "Live"))
            .Append(Group("late", 2020))
            .Append(Group("early", 1963))
            .ToArray();
        var mb = new FakeBrowse { Answer = (offset, _) => Task.FromResult(new MbReleaseGroupPage(groups.Skip(offset).Take(100).ToList(), groups.Length)) };
        env.Lidarr.OnLookup = term => term.StartsWith("lidarr:", StringComparison.Ordinal) ? new[] { Album(term["lidarr:".Length..]) } : Array.Empty<LidarrAlbum>();

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        var ids = env.Lidarr.LookupTerms.Where(t => t.StartsWith("lidarr:", StringComparison.Ordinal)).ToList();
        Assert.Equal("lidarr:early", ids[0]);
        Assert.Equal("lidarr:late", ids[1]);
        Assert.Equal(150, ids.Count);
        Assert.Equal(new[] { "late", "early" }, result.Albums.Take(2).Select(a => a.Mbid));
    }

    [Fact]
    public async Task Page_AnArtistLidarrHolds_DoesNotReachMusicBrainz()
    {
        using var env = new TestEnv();
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Artist = new LidarrArtist { Id = 7, ForeignArtistId = DjSnake, ArtistName = "DJ Snake" } } };
        env.Lidarr.Albums[1] = new LidarrAlbum { Id = 1, ForeignAlbumId = "held", Title = "Held", AlbumType = "Album", Artist = new LidarrArtist { Id = 7, ForeignArtistId = DjSnake, ArtistName = "DJ Snake" } };
        var mb = Browsing(Group("a"));

        var result = await Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal("held", Assert.Single(result.Albums).Mbid);
        Assert.Empty(mb.Calls);
    }

    [Fact]
    public async Task Page_ArtistLidarrDoesNotKnow_IsFoundThroughTheAlbums()
    {
        using var env = Production();
        env.Lidarr.OnSearch = _ => Array.Empty<LidarrSearchItem>();

        var result = await Catalog(env, RealMusicBrainz().Client).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None);

        Assert.Equal("DJ Snake", result.Artist!.Name);
        Assert.Equal(Nomad, result.Albums[0].Mbid);
    }

    [Fact]
    public async Task Page_UnknownEverywhere_IsStillNotFound()
    {
        using var env = new TestEnv();
        var mb = new FakeBrowse { Answer = (_, _) => throw new MusicBrainzUnavailableException("MusicBrainz answered 404") };

        var ex = await Assert.ThrowsAsync<CatalogException>(() => Catalog(env, mb).ArtistAlbumsAsync(DjSnake, null, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }

    // ---- the search

    private static IReadOnlyList<string> Ids(CatalogSearchResult result) => result.Albums.Select(a => a.Mbid).ToList();

    [Fact]
    public async Task Search_ByArtistName_PutsTheStudioAlbumsOnTop_OfThatArtistOnly()
    {
        using var env = Production();
        var mb = new FakeBrowse { Answer = (_, _) => RealPageAsync() };

        var result = await Catalog(env, mb).SearchAsync("dj snake", CancellationToken.None);

        Assert.Equal(new[] { Nomad, CarteBlanche, Encore, Presents }, Ids(result).Take(4));
        Assert.Equal(DjSnake, Assert.Single(mb.Calls).Artist);
    }

    private static Task<MbReleaseGroupPage> RealPageAsync()
        => RealMusicBrainz().Client.BrowseReleaseGroupsAsync(DjSnake, new[] { "album", "ep" }, 0, CancellationToken.None);

    [Fact]
    public async Task Search_ByArtistName_LeavesOutCompilationsAndEpsFromTheTop_AndDoesNotLookThemUp()
    {
        using var env = Production();

        await Catalog(env, RealMusicBrainz().Client).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.DoesNotContain("lidarr:" + ShangChi, env.Lidarr.LookupTerms);
        Assert.DoesNotContain("lidarr:" + LeanOnEp, env.Lidarr.LookupTerms);
        Assert.Contains("lidarr:" + Encore, env.Lidarr.LookupTerms);
    }

    [Theory]
    [InlineData("DJ SNAKE")]
    [InlineData("  dj   snake ")]
    [InlineData("Dj Snake!")]
    public async Task Search_TheNameIsComparedAsTitlesAre(string query)
    {
        using var env = Production();
        var mb = Browsing(Group(Encore));

        await Catalog(env, mb).SearchAsync(query, CancellationToken.None);

        Assert.Single(mb.Calls);
    }

    [Theory]
    [InlineData("Snake")]
    [InlineData("DJ Snake Presents")]
    [InlineData("Lean On")]
    public async Task Search_QueryThatIsNotTheTopArtistsName_AddsNoLoad(string query)
    {
        using var env = Production();
        var mb = Browsing(Group(Encore));

        await Catalog(env, mb).SearchAsync(query, CancellationToken.None);

        Assert.Empty(mb.Calls);
    }

    [Fact]
    public async Task Search_ANamesakeBelowTheTopArtist_DoesNotCount()
    {
        using var env = new TestEnv();
        env.Lidarr.OnSearch = _ => new[]
        {
            new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = "other", ArtistName = "Someone Else" } },
            new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = DjSnake, ArtistName = "Creep" } },
        };
        var mb = Browsing(Group(Encore));

        await Catalog(env, mb).SearchAsync("Creep", CancellationToken.None);

        Assert.Empty(mb.Calls);
    }

    [Fact]
    public async Task Search_AmongNamesakes_TheMostVoted_IsTheOneAskedFor_EvenListedSecond()
    {
        using var env = new TestEnv();
        env.Lidarr.OnSearch = _ => new[]
        {
            new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = Namesake, ArtistName = "DJ Snake", Ratings = new LidarrRatings { Votes = 0 } } },
            new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = DjSnake, ArtistName = "DJ Snake", Ratings = new LidarrRatings { Votes = 5 } } },
        };
        var mb = Browsing();

        await Catalog(env, mb).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Equal(DjSnake, Assert.Single(mb.Calls).Artist);
    }

    [Fact]
    public async Task Search_OnEqualVotes_TheNamesakeLidarrHolds_Wins_FromLidarrAlone()
    {
        using var env = new TestEnv();
        env.Lidarr.OnSearch = _ => new[]
        {
            new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = Namesake, ArtistName = "DJ Snake" } },
            new LidarrSearchItem { Artist = new LidarrArtist { Id = 7, ForeignArtistId = DjSnake, ArtistName = "DJ Snake" } },
        };
        env.Lidarr.Albums[1] = new LidarrAlbum { Id = 1, ForeignAlbumId = "held", Title = "Held", AlbumType = "Album", Artist = new LidarrArtist { Id = 7, ForeignArtistId = DjSnake, ArtistName = "DJ Snake" } };
        var mb = Browsing(Group(Encore));

        var result = await Catalog(env, mb).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Empty(mb.Calls);
        Assert.Equal("held", Ids(result)[0]);
    }

    [Fact]
    public async Task Search_ManyStudioAlbums_AreCapped_ToThoseWithTheMostEditions_NewestFirst()
    {
        using var env = Production();
        var groups = Enumerable.Range(0, 30).Select(i => Group("g" + i, 1960 + i)).ToArray();
        var mb = Browsing(groups);
        env.Lidarr.OnLookup = term =>
        {
            if (!term.StartsWith("lidarr:", StringComparison.Ordinal))
            {
                return Array.Empty<LidarrAlbum>();
            }

            var i = int.Parse(term["lidarr:g".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return new[] { Album("g" + i, releases: i < 12 ? 10 : 1, date: $"{1960 + i}-01-01T00:00:00Z") };
        };

        var result = await Catalog(env, mb).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 12).Reverse().Select(i => "g" + i), Ids(result).Take(12));
        Assert.DoesNotContain("g20", Ids(result));
    }

    [Fact]
    public async Task Search_HintedSongAlbums_StayFirst_TheArtistsAlbumsNext_ThenTheRest()
    {
        using var env = Production();
        env.Config.DeezerPopularityHints = true;
        var mb = new FakeBrowse
        {
            Answer = (_, _) => Task.FromResult(new MbReleaseGroupPage(new[] { Group(Encore, 2016) }, 1)),
            Recordings = q => new[]
            {
                new MbRecording("DJ Snake", q.Artist ?? "Someone", 100, new[] { Group(q.Artist is null ? "plain-hit" : "hinted-hit") }),
            },
        };
        var known = Fixture<List<LidarrAlbum>>("lidarr_lookup_dj_snake_albums.json");
        env.Lidarr.OnLookup = term => term switch
        {
            "lidarr:hinted-hit" => new[] { Album("hinted-hit", artist: "famous") },
            "lidarr:plain-hit" => new[] { Album("plain-hit", artist: "else") },
            _ when term.StartsWith("lidarr:", StringComparison.Ordinal) => known.Where(a => a.ForeignAlbumId == term["lidarr:".Length..]).ToList(),
            _ => new[] { Album("title-match", artist: "another") },
        };

        var result = await Catalog(env, mb, hints: new Hints("Famous")).SearchAsync("DJ Snake", CancellationToken.None);

        var ids = Ids(result).ToList();
        Assert.Equal("hinted-hit", ids[0]);
        Assert.Equal(Encore, ids[1]);
        Assert.True(ids.IndexOf(Presents) < ids.IndexOf("title-match"));
        Assert.Equal("plain-hit", ids[^1]);
    }

    [Fact]
    public async Task Search_TheArtistsAlbums_DoNotTakeTheRoomOfTheOtherResults()
    {
        using var env = Production();
        var mb = Browsing(Group(Encore, 2016), Group(Nomad, 2025), Group(CarteBlanche, 2019));
        var known = Fixture<List<LidarrAlbum>>("lidarr_lookup_dj_snake_albums.json");
        env.Lidarr.OnLookup = term => term.StartsWith("lidarr:", StringComparison.Ordinal)
            ? known.Where(a => a.ForeignAlbumId == term["lidarr:".Length..]).ToList()
            : Enumerable.Range(0, 60).Select(i => Album("other" + i, artist: "other" + i)).ToArray();

        var result = await Catalog(env, mb).SearchAsync("DJ Snake", CancellationToken.None);

        // the DJ Snake Presents of the search comes among the 40, and three on top of them
        Assert.Equal(43, result.Albums.Count);
    }

    [Fact]
    public async Task Search_BrowseFails_StillAnswersAsBefore()
    {
        using var env = Production();
        var mb = new FakeBrowse { Answer = (_, _) => throw new MusicBrainzUnavailableException("down", busy: true) };

        var result = await Catalog(env, mb).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Equal(Presents, Assert.Single(result.Albums).Mbid);
    }

    [Fact]
    public async Task Search_NoMusicBrainz_StillAnswersAsBefore()
    {
        using var env = Production();

        var result = await Catalog(env, null).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Equal(Presents, Assert.Single(result.Albums).Mbid);
    }

    [Fact]
    public async Task Search_BudgetExceeded_KeepsWhatResolved()
    {
        using var env = Production();
        var mb = Browsing(Group(Encore, 2016), Group(Nomad, 2025));
        env.Lidarr.BeforeLookup = async (term, ct) =>
        {
            if (term == "lidarr:" + Nomad)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };

        var result = await Catalog(env, mb, TimeSpan.FromMilliseconds(100)).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Equal(Encore, Ids(result)[0]);
        Assert.DoesNotContain(Nomad, Ids(result));
    }

    // ---- the allow list stays absolute

    private static TestEnv AllowListed()
    {
        var env = new TestEnv();
        env.Config.CatalogAllowList = Directionless;
        var directionless = Fixture<List<LidarrAlbum>>("lidarr_lookup_directionless.json");
        env.Lidarr.OnSearch = term => term == "lidarr:" + BrokeForFree
            ? new[] { new LidarrSearchItem { Artist = directionless.First(a => a.ForeignAlbumId == Directionless).Artist } }
            : Fixture<List<LidarrSearchItem>>("lidarr_search_directionless.json");
        env.Lidarr.OnLookup = term => directionless.Where(a => !term.StartsWith("lidarr:", StringComparison.Ordinal) || a.ForeignAlbumId == term["lidarr:".Length..]).ToList();
        return env;
    }

    [Fact]
    public async Task AllowList_Page_BrokeForFree_IsDirectionlessOnly_WithoutAskingMusicBrainz()
    {
        using var env = AllowListed();
        var mb = Browsing(Group(Directionless), Group("615e8bed-bc67-49ef-a4d0-7d9c27721f5b"));

        var result = await Catalog(env, mb).ArtistAlbumsAsync(BrokeForFree, null, CancellationToken.None);

        Assert.Equal(Directionless, Assert.Single(result.Albums).Mbid);
        Assert.Empty(mb.Calls);
    }

    [Fact]
    public async Task AllowList_Page_AnArtistWithAlbumsOutsideIt_IsUnknown_WithoutAskingMusicBrainz()
    {
        using var env = AllowListed();
        var mb = Browsing(Group(Encore));
        env.Lidarr.OnSearch = term => term == "lidarr:" + DjSnake
            ? new[] { new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = DjSnake, ArtistName = "DJ Snake" } } }
            : Array.Empty<LidarrSearchItem>();

        var ex = await Assert.ThrowsAsync<CatalogException>(() => Catalog(env, mb).ArtistAlbumsAsync(DjSnake, "DJ Snake", CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Empty(mb.Calls);
        Assert.DoesNotContain("lidarr:" + Encore, env.Lidarr.LookupTerms);
    }

    [Fact]
    public async Task AllowList_Search_ByArtistName_IsDirectionlessOnly_WithoutAskingMusicBrainz()
    {
        using var env = AllowListed();
        var mb = Browsing(Group(Encore), Group(Directionless));

        var result = await Catalog(env, mb).SearchAsync("Broke For Free", CancellationToken.None);

        Assert.Equal(Directionless, Assert.Single(result.Albums).Mbid);
        Assert.Empty(mb.Calls);
    }

    [Fact]
    public async Task AllowList_Search_AnArtistOutsideIt_GivesNothing_WithoutAskingMusicBrainz()
    {
        using var env = AllowListed();
        env.Lidarr.OnSearch = _ => Fixture<List<LidarrSearchItem>>("lidarr_search_dj_snake.json");
        env.Lidarr.OnLookup = term => term == "lidarr:" + Directionless ? Fixture<List<LidarrAlbum>>("lidarr_lookup_directionless.json").Where(a => a.ForeignAlbumId == Directionless).ToList() : Array.Empty<LidarrAlbum>();
        var mb = Browsing(Group(Encore));

        var result = await Catalog(env, mb).SearchAsync("DJ Snake", CancellationToken.None);

        Assert.Empty(result.Albums);
        Assert.Empty(result.Artists);
        Assert.Empty(mb.Calls);
    }
}
