using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Lidarr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>The one ranking of a search and its <c>top</c>. Fixtures are trimmed answers of a real Lidarr and a real MusicBrainz, for the searches of a phone in production.</summary>
public class SearchTopTests
{
    private const string DjSnake = "31445b1e-96f3-4c7f-84ec-dd02e647e690";
    private const string DjSnakeEye = "aa114151-4a17-42b0-8e20-37b434627479";
    private const string Radiohead = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
    private const string Beatles = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";
    private const string Directionless = "ce0eb31a-142a-4ac8-9cb9-6a3adb3ad19e";
    private const string Bootleg = "3e19d5e9-4ac5-33a9-a1f8-5c8796c80363";
    private const string WesleyWillis = "89bbac22-8be9-40ca-a85f-bbf81d4a131d";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static T Fixture<T>(string name)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), Json)!;

    private sealed class Hints(params string[] artists) : ITrackHints
    {
        public Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artists);
    }

    private sealed class Handler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });
    }

    private static MusicBrainzClient MusicBrainz(Func<string> body)
        => new(new HttpClient(new Handler(body)), "1.0.0.0", NullLogger.Instance, delay: (_, _) => Task.CompletedTask, baseUrl: "https://mb.test");

    private static string CreepRecordings() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "musicbrainz_search_recordings_creep.json"));

    private static CatalogService Catalog(TestEnv env, IMusicBrainzApi? musicBrainz = null, ITrackHints? hints = null, IArtistMetadata? metadata = null)
        => new(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, musicBrainz, hints: hints, metadata: metadata);

    private sealed class Pictures : IArtistMetadata
    {
        public Task<MetadataArtist?> GetArtistAsync(string artistId, CancellationToken ct)
            => Task.FromResult<MetadataArtist?>(new MetadataArtist(artistId, "Radiohead", string.Empty, "Group", Array.Empty<string>(), new[] { new MetadataImage("poster", "https://images.example/radiohead.jpg") }, Array.Empty<MetadataAlbum>()));
    }

    private static TestEnv EnvFor(string fixture)
    {
        var env = new TestEnv();
        env.Lidarr.OnSearch = _ => Fixture<List<LidarrSearchItem>>(fixture);
        return env;
    }

    private static LidarrAlbum Studio(string id, string title, int editions, int year)
        => new()
        {
            ForeignAlbumId = id,
            Title = title,
            AlbumType = "Album",
            ReleaseDate = $"{year}-01-01T00:00:00Z",
            Releases = Enumerable.Range(0, editions).Select(i => new LidarrRelease { ForeignReleaseId = $"{id}-{i}", Status = "Official" }).ToList(),
            Artist = new LidarrArtist { Id = 13, ForeignArtistId = Radiohead, ArtistName = "Radiohead" },
        };

    // Lidarr holds Radiohead: its own albums, a few more editions for the ones people know
    private static TestEnv RadioheadEnv(string fixture)
    {
        var env = EnvFor(fixture);
        env.Lidarr.Albums[1] = Studio("pablo", "Pablo Honey", 2, 1993);
        env.Lidarr.Albums[2] = Studio("ok", "OK Computer", 6, 1997);
        env.Lidarr.Albums[3] = Studio("kid", "Kid A", 5, 2000);
        env.Lidarr.Albums[4] = Studio("rainbows", "In Rainbows", 4, 2007);
        env.Lidarr.Albums[5] = Studio("bends", "The Bends", 3, 1994);
        return env;
    }

    private static string Describe(CatalogSearchResult result)
        => string.Join(" | ", result.Top!.Select(t => $"{t.Kind}:{(t.Kind == "artist" ? result.Artists.First(a => a.Mbid == t.Mbid).Name : result.Albums.First(a => a.Mbid == t.Mbid).Title)}:{t.Match}"));

    // ---- the artist

    [Fact]
    public async Task Top_AnExactArtistWithVotesAndPictures_IsFirst_AndItsNearNamesakesAreNotThere()
    {
        using var env = EnvFor("lidarr_search_dj_snake.json");

        var result = await Catalog(env).SearchAsync("Dj snake", CancellationToken.None);

        Assert.Equal(new CatalogTop("artist", DjSnake, "exact"), result.Top![0]);
        Assert.DoesNotContain(result.Top, t => t.Mbid == DjSnakeEye);
        Assert.DoesNotContain(result.Top, t => result.Artists.Any(a => a.Mbid == t.Mbid && a.Mbid != DjSnake && t.Kind == "artist"));
        Assert.Equal(DjSnake, result.Artists[0].Mbid);
        Assert.Contains(result.Artists, a => a.Mbid == DjSnakeEye);
    }

    [Fact]
    public async Task Top_ANamesakeWithAFewVotesAgainstManyIsNotThere_ButStaysInTheArtists()
    {
        using var env = EnvFor("lidarr_search_the_beatles.json");

        var result = await Catalog(env).SearchAsync("The Beatles", CancellationToken.None);

        Assert.Equal(new[] { Beatles }, result.Top!.Where(t => t.Kind == "artist").Select(t => t.Mbid));
        Assert.Equal(5, result.Artists.Count);
        Assert.Equal(Beatles, result.Artists[0].Mbid);
    }

    [Fact]
    public async Task Top_ASongTitlesNamesake_WithAVoteAndNoPicture_IsNotTheArtist()
    {
        using var env = EnvFor("lidarr_search_creep.json");

        var result = await Catalog(env).SearchAsync("Creep", CancellationToken.None);

        Assert.Null(result.Top);
        Assert.Equal(6, result.Artists.Count);
    }

    [Fact]
    public async Task Top_ATypoOfAnArtistWithManyVotes_IsTheArtist_AndItsStudioAlbumsFollow_NothingWithTheTextInItsName()
    {
        using var env = RadioheadEnv("lidarr_search_radiohed.json");

        var result = await Catalog(env).SearchAsync("radiohed", CancellationToken.None);

        Assert.Equal(new CatalogTop("artist", Radiohead, "partial"), result.Top![0]);
        Assert.Equal(new[] { "OK Computer", "Kid A", "In Rainbows" }, result.Top.Skip(1).Select(t => result.Albums.First(a => a.Mbid == t.Mbid).Title));
        Assert.Equal(Radiohead, result.Artists[0].Mbid);
        Assert.DoesNotContain(result.Albums.Where(a => a.Title == "Radiohedonistycznie"), a => result.Top.Any(t => t.Mbid == a.Mbid));
        Assert.DoesNotContain(result.Top, t => t.Kind == "artist" && t.Mbid != Radiohead);
    }

    [Fact]
    public async Task Top_AnArtistLidarrHolds_HasItsStudioAlbumsWithTheMostEditions_AndAnExactMatch()
    {
        using var env = RadioheadEnv("lidarr_search_radiohead.json");

        var result = await Catalog(env).SearchAsync("Radiohead", CancellationToken.None);

        Assert.Equal("artist:Radiohead:exact | album:OK Computer:partial | album:Kid A:partial | album:In Rainbows:partial", Describe(result));
        Assert.DoesNotContain(result.Top!, t => t.Mbid == WesleyWillis);
    }

    // ---- what is never offered

    [Fact]
    public async Task Search_AlbumsKnownFromBootlegsAndPromotionalCopiesAlone_AreNotOffered_AndAYearThatIsNoYearIsNotShown()
    {
        using var env = RadioheadEnv("lidarr_search_radiohead.json");

        var result = await Catalog(env).SearchAsync("Radiohead", CancellationToken.None);

        Assert.DoesNotContain(result.Albums, a => a.Mbid is Bootleg or "3ab9fbe7-b809-3db8-96cf-4de6e56bb180" or "af44dc42-1bf4-4cdc-ad98-cdfdc2312b09" or "d5eb0376-6f0f-4b28-b5d1-4584b9be8d75");
        Assert.All(result.Albums, a => Assert.True(a.Year == 0 || a.Year >= 1000));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", 0)]
    [InlineData("0000-00-00", 0)]
    [InlineData("1993-02-22T00:00:00Z", 1993)]
    [InlineData(null, 0)]
    public void YearOf_ADateMusicBrainzDoesNotHave_IsNoYear(string? date, int year) => Assert.Equal(year, CatalogService.YearOf(date));

    [Fact]
    public async Task Search_SongsOnBootlegsAndPromotionalCopiesAlone_AreNotOffered_AUnionOfOfficialAndNotIsKept()
    {
        using var env = EnvFor("lidarr_search_creep.json");

        var result = await Catalog(env, MusicBrainz(CreepRecordings)).SearchAsync("Creep", CancellationToken.None);

        var titles = result.Albums.Select(a => a.Title).ToList();
        Assert.DoesNotContain("Acoustic Type Thing", titles);
        Assert.DoesNotContain("Rolling Rock 2.0", titles);
        Assert.DoesNotContain("Rockhard Presents: Feel It to Believe", titles);
        Assert.Contains(titles, t => t.StartsWith("Linger Ficken", StringComparison.Ordinal));
        Assert.Contains("Reborn", titles);
    }

    [Fact]
    public async Task Search_VariousArtistsCompilations_FollowTheRealArtistsAlbums_AndAreNeverOnTop()
    {
        using var env = EnvFor("lidarr_search_creep.json");

        var result = await Catalog(env, MusicBrainz(CreepRecordings)).SearchAsync("Creep", CancellationToken.None);

        var songs = result.Albums.Where(a => a.MatchedTrack is not null).ToList();
        var firstVarious = songs.FindIndex(a => a.Artist == "Various Artists");
        Assert.True(firstVarious < 0 || songs.Take(firstVarious).All(a => a.Artist != "Various Artists"));
        Assert.True(firstVarious < 0 || songs.Skip(firstVarious).All(a => a.Artist == "Various Artists"));
        Assert.DoesNotContain(result.Top ?? Array.Empty<CatalogTop>(), t => result.Albums.Any(a => a.Mbid == t.Mbid && a.Artist == "Various Artists"));
    }

    [Fact]
    public async Task Search_WithHints_ACompilationOfVariousArtists_NeverComesBeforeTheHintedArtistsOwnAlbum()
    {
        const string Compilation = "99999999-9999-4999-8999-999999999999";
        using var env = EnvFor("lidarr_search_creep.json");
        env.Config.DeezerPopularityHints = true;
        var recordings = RadioheadCreep().Replace("\"releases\":[", $$$"""
            "releases":[{"id":"v","status":"Official","title":"Now Live","date":"1993","release-group":{"id":"{{{Compilation}}}","title":"Now Live","primary-type":"Album"},"artist-credit":[{"name":"Various Artists","artist":{"id":"89ad4ac3-39f7-470e-963a-56509c546377","name":"Various Artists"}}]},
            """, StringComparison.Ordinal);

        var result = await Catalog(env, MusicBrainz(() => recordings), new Hints("Radiohead")).SearchAsync("Creep", CancellationToken.None);

        var songs = result.Albums.Where(a => a.MatchedTrack is not null).Select(a => a.Title).ToList();
        Assert.Contains("Now Live", songs);
        Assert.True(songs.IndexOf("Pablo Honey") < songs.IndexOf("Now Live"), string.Join(" | ", songs));
        Assert.DoesNotContain(result.Top ?? Array.Empty<CatalogTop>(), t => t.Mbid == Compilation);
    }

    [Fact]
    public async Task Top_AmongManyArtistsWithTheSong_NoneIsOnTop_BecauseNothingSaysWhichIsMeant()
    {
        using var env = EnvFor("lidarr_search_creep.json");

        var result = await Catalog(env, MusicBrainz(CreepRecordings)).SearchAsync("Creep", CancellationToken.None);

        Assert.Null(result.Top);
    }

    // ---- songs

    private const string Pablo = "cd76f76b-ff15-3784-a71d-4da3078a6851";
    private const string Edition = "11111111-2222-4333-8444-555555555555";

    private static string RadioheadCreep() => $$$"""
        {"recordings":[{"id":"r","score":100,"title":"Creep","artist-credit":[{"name":"Radiohead","artist":{"id":"{{{Radiohead}}}","name":"Radiohead"}}],
          "releases":[
            {"id":"a","status":"Official","title":"Pablo Honey","date":"1993","release-group":{"id":"{{{Pablo}}}","title":"Pablo Honey","primary-type":"Album"}},
            {"id":"b","status":"Official","title":"Pablo Honey (Collector's Edition)","date":"2009","release-group":{"id":"{{{Edition}}}","title":"Pablo Honey (Collector's Edition)","primary-type":"Album"}},
            {"id":"c","status":"Bootleg","title":"Unofficial Live","date":"2001","release-group":{"id":"22222222-2222-4333-8444-555555555555","title":"Unofficial Live","primary-type":"Album","secondary-types":["Live"]}}]}]}
        """;

    [Fact]
    public async Task Top_WhenTheServerHoldsTheAlbumWithTheSong_NoOtherAlbumOfThatArtistsSongIsThere()
    {
        using var env = EnvFor("lidarr_search_creep.json");
        env.Config.DeezerPopularityHints = true;
        env.Library.KnownTitles.Add("Pablo Honey");

        var result = await Catalog(env, MusicBrainz(RadioheadCreep), new Hints("Radiohead")).SearchAsync("Creep", CancellationToken.None);

        Assert.Equal("album:Pablo Honey:exact", Describe(result));
        Assert.Contains(result.Albums, a => a.Mbid == Edition);
        Assert.DoesNotContain(result.Albums, a => a.Title == "Unofficial Live");
    }

    [Fact]
    public async Task Top_WhenTheServerDoesNotHoldIt_TheHintedAlbumsAreOnTopAndExact()
    {
        using var env = EnvFor("lidarr_search_creep.json");
        env.Config.DeezerPopularityHints = true;

        var result = await Catalog(env, MusicBrainz(RadioheadCreep), new Hints("Radiohead")).SearchAsync("Creep", CancellationToken.None);

        Assert.Equal("album:Pablo Honey:exact | album:Pablo Honey (Collector's Edition):exact", Describe(result));
    }

    [Fact]
    public async Task Top_ASongOnlyOneArtistHas_IsOnTop()
    {
        using var env = EnvFor("lidarr_search_creep.json");

        var result = await Catalog(env, MusicBrainz(RadioheadCreep)).SearchAsync("Creep", CancellationToken.None);

        Assert.Equal("album:Pablo Honey:exact | album:Pablo Honey (Collector's Edition):exact", Describe(result));
    }

    // ---- the albums

    [Fact]
    public async Task Search_TheStudioAlbumsOfTheArtistMeant_ComeFirst_ThenTheAlbumsOfTheTitle_ThenLidarrsOrder()
    {
        using var env = RadioheadEnv("lidarr_search_radiohead.json");

        var result = await Catalog(env).SearchAsync("Radiohead", CancellationToken.None);

        var titles = result.Albums.Select(a => a.Title).ToList();
        Assert.Equal(new[] { "Pablo Honey", "OK Computer", "Kid A", "In Rainbows", "The Bends" }.Order(), titles.Take(5).Order());
        Assert.Equal(WesleyWillis, result.Albums[5].Mbid);
    }

    // ---- the answer

    [Fact]
    public async Task Json_WithoutTop_IsTheOldAnswer_WithTop_ItHoldsKindMbidAndMatch()
    {
        using var env = EnvFor("lidarr_search_creep.json");
        var plain = await Catalog(env).SearchAsync("Creep", CancellationToken.None);
        using var withTop = RadioheadEnv("lidarr_search_radiohead.json");
        var ranked = await Catalog(withTop).SearchAsync("Radiohead", CancellationToken.None);

        var old = JsonDocument.Parse(ApiJson.Result(plain).Content!).RootElement;
        var top = JsonDocument.Parse(ApiJson.Result(ranked).Content!).RootElement;

        Assert.False(old.TryGetProperty("top", out _));
        Assert.Equal(new[] { "artists", "albums" }, old.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Array, old.GetProperty("artists").ValueKind);
        var first = top.GetProperty("top")[0];
        Assert.Equal(new[] { "kind", "mbid", "match" }, first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("artist", Radiohead, "exact"), (first.GetProperty("kind").GetString(), first.GetProperty("mbid").GetString(), first.GetProperty("match").GetString()));
        Assert.True(top.GetProperty("albums")[0].TryGetProperty("title", out _));
    }

    public static IEnumerable<object[]> Scenarios()
    {
        yield return new object[] { "dj snake" };
        yield return new object[] { "beatles" };
        yield return new object[] { "creep" };
        yield return new object[] { "creep held" };
        yield return new object[] { "radiohead" };
        yield return new object[] { "radiohed" };
        yield return new object[] { "directionless" };
        yield return new object[] { "broke for free" };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Top_RefersOnlyToItemsOfTheSameAnswer_AtMostSix_EachOnce(string scenario)
    {
        using var env = scenario switch
        {
            "dj snake" => EnvFor("lidarr_search_dj_snake.json"),
            "beatles" => EnvFor("lidarr_search_the_beatles.json"),
            "radiohead" => RadioheadEnv("lidarr_search_radiohead.json"),
            "radiohed" => RadioheadEnv("lidarr_search_radiohed.json"),
            "directionless" or "broke for free" => DirectionlessEnv(),
            _ => EnvFor("lidarr_search_creep.json"),
        };
        if (scenario == "creep held")
        {
            env.Config.DeezerPopularityHints = true;
            env.Library.KnownTitles.Add("Pablo Honey");
        }

        var query = scenario == "creep held" ? "creep" : scenario;
        var result = await Catalog(env, MusicBrainz(scenario.StartsWith("creep", StringComparison.Ordinal) ? RadioheadCreep : CreepRecordings), scenario == "creep held" ? new Hints("Radiohead") : null).SearchAsync(query, CancellationToken.None);

        var top = result.Top ?? Array.Empty<CatalogTop>();
        Assert.InRange(top.Count, 0, 6);
        Assert.Equal(top.Count, top.Select(t => (t.Kind, t.Mbid)).Distinct().Count());
        Assert.All(top, t =>
        {
            Assert.Contains(t.Match, new[] { "exact", "partial" });
            Assert.True(t.Kind == "artist" ? result.Artists.Any(a => a.Mbid == t.Mbid) : result.Albums.Any(a => a.Mbid == t.Mbid));
        });
    }

    private static TestEnv DirectionlessEnv()
    {
        var env = new TestEnv();
        env.Config.CatalogAllowList = Directionless;
        env.Lidarr.OnSearch = _ => Fixture<List<LidarrSearchItem>>("lidarr_search_directionless.json");
        env.Lidarr.OnLookup = term => Fixture<List<LidarrAlbum>>("lidarr_lookup_directionless.json")
            .Where(a => !term.StartsWith("lidarr:", StringComparison.Ordinal) || a.ForeignAlbumId == term["lidarr:".Length..])
            .ToList();
        return env;
    }

    [Theory]
    [InlineData("Directionless")]
    [InlineData("Broke For Free")]
    public async Task AllowList_TopHoldsOnlyAllowedItems_AndTheAnswerStaysTheSame(string query)
    {
        using var env = DirectionlessEnv();

        var result = await Catalog(env, MusicBrainz(CreepRecordings)).SearchAsync(query, CancellationToken.None);

        Assert.Equal(Directionless, Assert.Single(result.Albums).Mbid);
        Assert.All(result.Top ?? Array.Empty<CatalogTop>(), t => Assert.True(t.Kind == "artist" || t.Mbid == Directionless));
        if (query == "Broke For Free")
        {
            Assert.Equal(new CatalogTop("artist", result.Artists[0].Mbid, "exact"), result.Top![0]);
        }
    }

    [Fact]
    public async Task Search_AnArtistLidarrHolds_ShowsThePicturesOfTheMetadataService_NeverThePathsOfItsOwnFolder()
    {
        using var env = RadioheadEnv("lidarr_search_radiohead.json");

        var without = await Catalog(env).SearchAsync("Radiohead", CancellationToken.None);
        var with = await Catalog(env, metadata: new Pictures()).SearchAsync("Radiohead", CancellationToken.None);

        Assert.Empty(without.Artists[0].Images);
        Assert.Equal("https://images.example/radiohead.jpg", Assert.Single(with.Artists[0].Images).Url);
    }

    [Fact]
    public void ImageUrl_APathOnLidarrsOwnDisk_IsNoPictureTheAppCouldFetch()
    {
        var local = new LidarrImage { CoverType = "poster", Url = "/MediaCover/13/poster.jpg?lastWrite=1", RemoteUrl = "/config/MediaCover/13/poster.jpg" };
        var remote = new LidarrImage { CoverType = "poster", Url = "/MediaCoverProxy/2f3a/p.jpg", RemoteUrl = "https://images.example/p.jpg" };

        Assert.Equal(string.Empty, CatalogService.ImageUrl(local));
        Assert.Equal("https://images.example/p.jpg", CatalogService.ImageUrl(remote));
    }
}
