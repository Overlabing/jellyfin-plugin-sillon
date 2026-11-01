using System.Text.Json;
using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class CatalogServiceTests
{
    private const string Xeu = "5f0e7c3a-2b1d-4e8f-9a6b-7d3c1e5a9b22";
    private const string Vald = "9d2d1c4e-7a0b-4f5c-8d6a-0c6f6d2a1b11";

    // the canned answer of the former gateway's tests
    private const string SearchJson = """
        [
         {"foreignId":"9d2d1c4e-7a0b-4f5c-8d6a-0c6f6d2a1b11","artist":{"id":0,"artistName":"Vald","foreignArtistId":"9d2d1c4e-7a0b-4f5c-8d6a-0c6f6d2a1b11","disambiguation":"rap","images":[{"coverType":"poster","url":"https://img/vald.jpg"}]}},
         {"foreignId":"5f0e7c3a-2b1d-4e8f-9a6b-7d3c1e5a9b22","album":{"id":0,"title":"XEU","foreignAlbumId":"5f0e7c3a-2b1d-4e8f-9a6b-7d3c1e5a9b22","releaseDate":"2022-11-18T00:00:00Z","albumType":"Album",
           "images":[{"coverType":"cover","url":"/MediaCover/Albums/41/cover.jpg","remoteUrl":"https://img/xeu.jpg"}],"artist":{"artistName":"Vald","foreignArtistId":"9d2d1c4e-7a0b-4f5c-8d6a-0c6f6d2a1b11"}}},
         {"foreignId":"in-lib","album":{"id":9,"title":"Agartha","foreignAlbumId":"in-lib","releaseDate":"2018-01-01","albumType":"Album",
           "images":[{"coverType":"remoteCover","url":"/MediaCover/Albums/9/cover.jpg"},{"coverType":"cover","url":"https://img/agartha.jpg"}],"statistics":{"percentOfTracks":100,"trackFileCount":12},
           "artist":{"artistName":"Vald","foreignArtistId":"9d2d1c4e-7a0b-4f5c-8d6a-0c6f6d2a1b11"}}}
        ]
        """;

    private static IReadOnlyList<LidarrSearchItem> Items(string json)
        => JsonSerializer.Deserialize<List<LidarrSearchItem>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static (TestEnv Env, CatalogService Catalog) Make(FulfillmentMode mode = FulfillmentMode.Lidarr)
    {
        var env = new TestEnv(mode);
        env.Lidarr.OnSearch = term => term == "lidarr:" + Vald
            ? Items("[{\"foreignId\":\"" + Vald + "\",\"artist\":{\"id\":0,\"artistName\":\"Vald\",\"foreignArtistId\":\"" + Vald + "\"}}]")
            : Items(SearchJson);
        return (env, new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, now: () => env.Now));
    }

    [Fact]
    public async Task Search_MapsArtistsAndAlbums_WithTheGatewayStates()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = Xeu });

        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        var artist = Assert.Single(result.Artists);
        Assert.Equal((Vald, "Vald", "rap"), (artist.Mbid, artist.Name, artist.Disambiguation));
        Assert.Equal("https://img/vald.jpg", Assert.Single(artist.Images).Url);
        Assert.Equal(2, result.Albums.Count);
        var xeu = result.Albums[0];
        Assert.Equal(("requested", 1L, 2022, "https://img/xeu.jpg", "Vald", Vald, "Album"), (xeu.State, xeu.RequestId, xeu.Year, xeu.CoverUrl, xeu.Artist, xeu.ArtistMbid, xeu.Type));
        var agartha = result.Albums[1];
        Assert.Equal(("library", null, "https://img/agartha.jpg"), (agartha.State, agartha.RequestId, agartha.CoverUrl));
    }

    [Fact]
    public async Task Search_AnAlbumInJellyfinsLibrary_IsInTheLibrary_ByTitleAndArtistToo()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Library.KnownTitles.Add("XEU");

        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.Equal("library", result.Albums[0].State);
        Assert.Null(result.Albums[0].RequestId);
    }

    [Fact]
    public async Task Search_ARequestThatMovedOnIsReflected_ACancelledOneIsNot()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var request = env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = Xeu });
        env.Store.Transition(request.Id, new[] { RequestStatus.Requested }, RequestStatus.Downloading, string.Empty);
        Assert.Equal("downloading", (await catalog.SearchAsync("vald", CancellationToken.None)).Albums[0].State);

        env.Store.Transition(request.Id, RequestStatus.Live, RequestStatus.Cancelled, string.Empty);
        Assert.Equal("absent", (await catalog.SearchAsync("vald", CancellationToken.None)).Albums[0].State);
    }

    private const string RichAlbumJson = """
        {"id":0,"title":"Live at X","foreignAlbumId":"rich-1","releaseDate":"2019-05-03T00:00:00Z","albumType":"EP","secondaryTypes":["Live","Compilation"],"disambiguation":"deluxe",
         "releases":[{"mediumCount":1,"trackCount":9,"label":["Label A","Label B"],"disambiguation":"","status":"Official"},{"mediumCount":3,"trackCount":30,"label":[],"status":"Official"}],
         "artist":{"artistName":"Era","foreignArtistId":"era-1","artistType":"Group","genres":["ambient","new age"]}}
        """;

    private static LidarrAlbum Album(string json)
        => JsonSerializer.Deserialize<LidarrAlbum>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task Search_CarriesTheRichAlbumFields_AndTheArtistTypeAndGenres()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var rich = Album(RichAlbumJson);
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Album = rich }, new LidarrSearchItem { Artist = rich.Artist } };

        var result = await catalog.SearchAsync("era", CancellationToken.None);

        var album = Assert.Single(result.Albums);
        Assert.Equal(("EP", 1, 9, "Label A", "2019-05-03T00:00:00Z", "deluxe"), (album.AlbumType, album.DiscCount, album.TrackCount, album.Label, album.ReleaseDate, album.Disambiguation));
        Assert.Equal(new[] { "Live", "Compilation" }, album.SecondaryTypes);
        var artist = Assert.Single(result.Artists);
        Assert.Equal("Group", artist.ArtistType);
        Assert.Equal(new[] { "ambient", "new age" }, artist.Genres);
    }

    [Fact]
    public async Task Search_AnAlbumWithoutReleases_HasTheDefaults()
    {
        var (env, catalog) = Make();
        using var _ = env;

        var album = (await catalog.SearchAsync("vald", CancellationToken.None)).Albums[0];

        Assert.Equal(("Album", 1, 0, null, "", ""), (album.AlbumType, album.DiscCount, album.TrackCount, album.Label, album.Disambiguation, string.Join(',', album.SecondaryTypes)));
        Assert.Equal("2022-11-18T00:00:00Z", album.ReleaseDate);
        Assert.Empty((await catalog.SearchAsync("vald", CancellationToken.None)).Artists[0].Genres);
    }

    [Fact]
    public async Task Search_Json_HasTheContractKeys_AndOmitsAbsentOnes()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Album = Album(RichAlbumJson) }, new LidarrSearchItem { Album = new LidarrAlbum { ForeignAlbumId = "bare", Title = "Bare" } } };

        var json = JsonDocument.Parse(JsonSerializer.Serialize(await catalog.SearchAsync("era", CancellationToken.None), ApiJson.Options)).RootElement.GetProperty("albums");

        Assert.Equal(
            new[] { "mbid", "artistMbid", "title", "artist", "year", "type", "coverUrl", "state", "albumType", "secondaryTypes", "discCount", "trackCount", "label", "releaseDate", "disambiguation" },
            json[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            new[] { "mbid", "artistMbid", "title", "artist", "year", "type", "coverUrl", "state", "albumType", "secondaryTypes", "discCount", "trackCount", "disambiguation" },
            json[1].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Search_MergesTheAlbumLookup_DedupedByForeignAlbumId()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Artist = new LidarrArtist { ForeignArtistId = "era-1", ArtistName = "Era" } } };
        env.Lidarr.OnLookup = _ => new[]
        {
            Album(RichAlbumJson),
            new LidarrAlbum { ForeignAlbumId = "rich-1", Title = "dup" },
            new LidarrAlbum { ForeignAlbumId = string.Empty, Title = "no id" },
            new LidarrAlbum { ForeignAlbumId = "other", Title = "Other" }
        };

        var result = await catalog.SearchAsync("Era", CancellationToken.None);

        Assert.Single(result.Artists);
        Assert.Equal(new[] { "rich-1", "other" }, result.Albums.Select(a => a.Mbid));
        Assert.Equal("Live at X", result.Albums[0].Title);
        Assert.Equal(new[] { "Era" }, env.Lidarr.LookupTerms);
    }

    [Fact]
    public async Task Search_SearchAlbumsComeFirst_TheLookupFillsInMissingReleases_AndTheCapIs40()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Album = new LidarrAlbum { ForeignAlbumId = "rich-1", Title = "bare" } } };
        env.Lidarr.OnLookup = _ => new[] { Album(RichAlbumJson) }
            .Concat(Enumerable.Range(0, 60).Select(i => new LidarrAlbum { ForeignAlbumId = "x" + i, Title = "x" + i })).ToList();

        var result = await catalog.SearchAsync("era", CancellationToken.None);

        Assert.Equal(40, result.Albums.Count);
        Assert.Equal("rich-1", result.Albums[0].Mbid);
        Assert.Equal(1, result.Albums[0].DiscCount);
    }

    [Fact]
    public async Task Search_WhenOnlyTheAlbumLookupFails_StillAnswers()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnLookup = _ => throw new LidarrException("down");

        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.Equal(2, result.Albums.Count);
    }

    [Fact]
    public async Task Search_InManualMode_Is503WithAFrenchMessage()
    {
        var (env, catalog) = Make(FulfillmentMode.Manual);
        using var _ = env;

        var ex = await Assert.ThrowsAsync<CatalogException>(() => catalog.SearchAsync("vald", CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("mode manuel", ex.Message);
        Assert.Empty(env.Lidarr.SearchTerms);
    }

    [Fact]
    public async Task Search_WhenLidarrFails_Is502_WithoutTheDetail()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => throw new LidarrException("SECRET-DETAIL");

        var ex = await Assert.ThrowsAsync<CatalogException>(() => catalog.SearchAsync("vald", CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("Lidarr indisponible", ex.Message);
    }

    [Fact]
    public async Task Artist_FallsBackToASearchByName_WhenLidarrDoesNotHoldIt()
    {
        var (env, catalog) = Make();
        using var _ = env;

        var result = await catalog.ArtistAlbumsAsync(Vald, null, CancellationToken.None);

        Assert.Equal("Vald", result.Artist!.Name);
        Assert.Equal(2, result.Albums.Count);
        Assert.Equal(new[] { "lidarr:" + Vald, "Vald" }, env.Lidarr.SearchTerms);
    }

    [Fact]
    public async Task Artist_HeldByLidarr_ListsAllItsAlbums()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var artist = new LidarrArtist { Id = 3, ArtistName = "Vald", ForeignArtistId = Vald };
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Artist = artist } };
        env.Lidarr.Albums[1] = new LidarrAlbum { Id = 1, Title = "Agartha", ForeignAlbumId = "alb-1", Artist = artist };
        env.Lidarr.Albums[2] = new LidarrAlbum { Id = 2, Title = "XEU", ForeignAlbumId = "alb-2", Artist = artist };

        var result = await catalog.ArtistAlbumsAsync(Vald, null, CancellationToken.None);

        Assert.Equal(new[] { "alb-1", "alb-2" }, result.Albums.Select(a => a.Mbid));
        Assert.Single(env.Lidarr.SearchTerms);
    }

    [Fact]
    public async Task Search_WithAnAllowList_KeepsOnlyListedAlbums_AndTheirArtists()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Config.CatalogAllowList = "# demo\n" + Xeu.ToUpperInvariant() + "\n";

        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.Equal(new[] { Xeu }, result.Albums.Select(a => a.Mbid));
        Assert.Single(result.Artists);
    }

    [Fact]
    public async Task Search_WithAnAllowList_ByArtistName_ServesTheCachedAllowedAlbums_ResolvedOnce()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var artistOnly = Items("[{\"foreignId\":\"" + Vald + "\",\"artist\":{\"id\":0,\"artistName\":\"Vald\",\"foreignArtistId\":\"" + Vald + "\"}}]");
        var xeu = Items(SearchJson)[1].Album!;
        env.Lidarr.OnSearch = _ => artistOnly;
        env.Lidarr.OnLookup = term => term == "lidarr:" + Xeu ? new[] { xeu } : Array.Empty<LidarrAlbum>();
        env.Config.CatalogAllowList = Xeu;

        var result = await catalog.SearchAsync("VALD", CancellationToken.None);
        await catalog.SearchAsync("xeu", CancellationToken.None);

        Assert.Equal(Vald, Assert.Single(result.Artists).Mbid);
        Assert.Equal(new[] { Xeu }, result.Albums.Select(a => a.Mbid));
        Assert.Equal(1, env.Lidarr.LookupTerms.Count(t => t == "lidarr:" + Xeu));
        Assert.Equal(Xeu, Assert.Single((await catalog.ArtistAlbumsAsync(Vald, null, CancellationToken.None)).Albums).Mbid);
    }

    [Fact]
    public async Task Search_WithAnAllowList_StillAnswers_WhenLidarrSearchFails()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var xeu = Items(SearchJson)[1].Album!;
        env.Lidarr.OnSearch = _ => throw new LidarrException("503");
        env.Lidarr.OnLookup = term => term == "lidarr:" + Xeu ? new[] { xeu } : Array.Empty<LidarrAlbum>();
        env.Config.CatalogAllowList = Xeu;

        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.Single(result.Artists);
        Assert.Single(result.Albums);
    }

    [Fact]
    public async Task Search_WithoutAnAllowList_DoesNotExpandArtists()
    {
        var (env, catalog) = Make();
        using var _ = env;

        await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.DoesNotContain("lidarr:" + Vald, env.Lidarr.SearchTerms);
    }

    [Fact]
    public async Task Search_WithAnAllowListMatchingNothing_AnswersEmpty()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Config.CatalogAllowList = "other-album";

        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.Empty(result.Albums);
        Assert.Empty(result.Artists);
    }

    [Fact]
    public async Task Artist_WithAnAllowList_ListsOnlyListedAlbums_AndIs404WithNone()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var artist = new LidarrArtist { Id = 3, ArtistName = "Vald", ForeignArtistId = Vald };
        env.Lidarr.OnSearch = _ => new[] { new LidarrSearchItem { Artist = artist } };
        env.Lidarr.Albums[1] = new LidarrAlbum { Id = 1, Title = "Agartha", ForeignAlbumId = "alb-1", Artist = artist };
        env.Lidarr.Albums[2] = new LidarrAlbum { Id = 2, Title = "XEU", ForeignAlbumId = "alb-2", Artist = artist };
        env.Config.CatalogAllowList = "ALB-2";

        var result = await catalog.ArtistAlbumsAsync(Vald, null, CancellationToken.None);

        Assert.Equal(new[] { "alb-2" }, result.Albums.Select(a => a.Mbid));

        env.Config.CatalogAllowList = "alb-9";
        var ex = await Assert.ThrowsAsync<CatalogException>(() => catalog.ArtistAlbumsAsync(Vald, null, CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Artist_UsesTheNameHint_WhenTheIdLookupFindsNothing()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = term => term.StartsWith("lidarr:", StringComparison.Ordinal) ? Array.Empty<LidarrSearchItem>() : Items(SearchJson);

        var result = await catalog.ArtistAlbumsAsync(Vald, "Vald", CancellationToken.None);

        Assert.Equal(2, result.Albums.Count);
        Assert.NotNull(result.Artist);
    }

    [Fact]
    public async Task Artist_Unknown_Is404()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => Array.Empty<LidarrSearchItem>();

        var ex = await Assert.ThrowsAsync<CatalogException>(() => catalog.ArtistAlbumsAsync("unknown", null, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Json_MatchesTheShapeTheAppDecodes()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Store.Create(new RequestRecord { UserId = "u", ForeignAlbumId = Xeu });

        var json = JsonDocument.Parse(ApiJson.Result(await catalog.SearchAsync("vald", CancellationToken.None)).Content!).RootElement;

        var xeu = json.GetProperty("albums")[0];
        Assert.Equal(
            "{\"mbid\":\"" + Xeu + "\",\"artistMbid\":\"" + Vald + "\",\"title\":\"XEU\",\"artist\":\"Vald\",\"year\":2022,\"type\":\"Album\",\"coverUrl\":\"https://img/xeu.jpg\",\"state\":\"requested\",\"requestId\":1,\"albumType\":\"Album\",\"secondaryTypes\":[],\"discCount\":1,\"trackCount\":0,\"releaseDate\":\"2022-11-18T00:00:00Z\",\"disambiguation\":\"\",\"phase\":\"searching\"}",
            xeu.GetRawText());
        // no request: requestId is absent, not null
        Assert.False(json.GetProperty("albums")[1].TryGetProperty("requestId", out var ignored));
        Assert.Equal(
            "{\"mbid\":\"" + Vald + "\",\"name\":\"Vald\",\"disambiguation\":\"rap\",\"images\":[{\"coverType\":\"poster\",\"url\":\"https://img/vald.jpg\"}],\"artistType\":\"\",\"genres\":[]}",
            json.GetProperty("artists")[0].GetRawText());
    }

    [Theory]
    [InlineData("/MediaCover/1/c.jpg", "https://img/c.jpg", "https://img/c.jpg")]
    [InlineData("https://img/c.jpg", "", "https://img/c.jpg")]
    [InlineData("/MediaCover/1/c.jpg", "", "")]
    [InlineData("", "", "")]
    public void ImageUrl_PrefersTheRemoteUrl_AndIgnoresARelativeOne(string url, string remote, string expected)
        => Assert.Equal(expected, CatalogService.ImageUrl(new LidarrImage { Url = url, RemoteUrl = remote }));

    [Theory]
    [InlineData("2022-11-18T00:00:00Z", 2022)]
    [InlineData("2018", 2018)]
    [InlineData("20", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("abcd-01-01", 0)]
    public void YearOf_ReadsTheFirstFourDigits(string? date, int expected)
        => Assert.Equal(expected, CatalogService.YearOf(date));

    private static LidarrAlbum Rel(string id, string title, string type, int tracks, string date = "2014-09-29", string artist = "gilmour", string[]? secondary = null)
        => Album("{\"id\":0,\"title\":" + JsonSerializer.Serialize(title) + ",\"foreignAlbumId\":\"" + id + "\",\"releaseDate\":\"" + date + "\",\"albumType\":\"" + type + "\","
            + "\"secondaryTypes\":" + JsonSerializer.Serialize(secondary ?? Array.Empty<string>()) + ","
            + "\"releases\":[{\"mediumCount\":1,\"trackCount\":" + tracks + ",\"status\":\"Official\"}],"
            + "\"artist\":{\"artistName\":\"David Gilmour\",\"foreignArtistId\":\"" + artist + "\"}}");

    private static async Task<IReadOnlyList<string>> SearchIds(params LidarrAlbum[] found)
    {
        var (env, catalog) = Make();
        using var _ = env;

        // these tests are about which same-title release wins, so singles must not be hidden before that
        env.Config.HideSingles = false;
        env.Lidarr.OnSearch = _ => found.Select(a => new LidarrSearchItem { Album = a }).ToList();
        return (await catalog.SearchAsync("rattle", CancellationToken.None)).Albums.Select(a => a.Mbid).ToList();
    }

    [Fact]
    public async Task Search_ASingleAndAnAlbumWithTheSameTitle_OffersTheAlbum_InEitherOrder()
    {
        var single = Rel("single", "Rattle That Lock", "Single", 1);
        var album = Rel("album", "Rattle That Lock", "Album", 14, "2015-09-18");

        Assert.Equal(new[] { "album" }, await SearchIds(single, album));
        Assert.Equal(new[] { "album" }, await SearchIds(album, single));
    }

    [Fact]
    public async Task Search_ASingleWithoutASameTitleAlbum_StaysVisible()
    {
        var ids = await SearchIds(Rel("single", "Yes, I Have Ghosts", "Single", 1), Rel("album", "Rattle That Lock", "Album", 14));

        Assert.Equal(new[] { "single", "album" }, ids);
    }

    [Fact]
    public async Task Search_TitlesFoldCaseDiacriticsAndPunctuation()
    {
        var ids = await SearchIds(Rel("single", "RATTLE, that lock!", "Single", 1), Rel("album", "Rättle That Lock", "Album", 14));

        Assert.Equal(new[] { "album" }, ids);
    }

    [Fact]
    public async Task Search_TheSameTitleFromAnotherArtist_IsKept()
    {
        var ids = await SearchIds(Rel("single", "Rattle That Lock", "Single", 1), Rel("other", "Rattle That Lock", "Album", 10, artist: "someone-else"));

        Assert.Equal(new[] { "single", "other" }, ids);
    }

    [Fact]
    public async Task Search_SameTitle_AnAlbumDropsTheLesserKinds_AlbumsWithoutSecondaryTypesFirst_ThenTheMostEditions_ThenTheEarliest()
    {
        Assert.Equal(new[] { "live" }, await SearchIds(Rel("ep", "X", "EP", 5), Rel("single", "X", "Single", 1), Rel("live", "X", "Album", 8, secondary: new[] { "Live" })));
        Assert.Equal(new[] { "plain", "live" }, await SearchIds(Rel("live", "X", "Album", 20, secondary: new[] { "Live" }), Rel("plain", "X", "Album", 8)));
        Assert.Equal(new[] { "single", "ep" }, await SearchIds(Rel("single", "X", "Single", 3), Rel("ep", "X", "EP", 3)));
        Assert.Equal(new[] { "early", "late" }, await SearchIds(Rel("late", "X", "Album", 9, "2020-01-01"), Rel("early", "X", "Album", 9, "2010-01-01")));
    }

    [Fact]
    public async Task Search_WithAnAllowListHoldingOnlyTheSingle_KeepsTheSingle()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Config.CatalogAllowList = "single";
        env.Lidarr.OnSearch = _ => new[]
        {
            new LidarrSearchItem { Album = Rel("single", "Rattle That Lock", "Single", 1) },
            new LidarrSearchItem { Album = Rel("album", "Rattle That Lock", "Album", 14) },
        };

        var result = await catalog.SearchAsync("rattle", CancellationToken.None);

        Assert.Equal(new[] { "single" }, result.Albums.Select(a => a.Mbid));
    }

    private const string LesMis = "lesmis-artist";

    // Lidarr's lookup shape: a release group per recording, each with its releases
    private static LidarrAlbum LesMisGroup(string id, string date, string[] secondary, string? disambiguation, string[] releases, string country = "GB")
        => Album("{\"id\":0,\"title\":\"Les Mis\u00e9rables\",\"foreignAlbumId\":\"" + id + "\",\"releaseDate\":\"" + date + "\",\"albumType\":\"Album\","
            + "\"secondaryTypes\":" + JsonSerializer.Serialize(secondary) + ","
            + (disambiguation is null ? string.Empty : "\"disambiguation\":" + JsonSerializer.Serialize(disambiguation) + ",")
            + "\"releases\":[" + string.Join(",", releases.Select(d => "{\"mediumCount\":2,\"trackCount\":24,\"status\":\"Official\",\"country\":[\"" + country + "\"],\"disambiguation\":" + JsonSerializer.Serialize(d) + "}")) + "],"
            + "\"artist\":{\"artistName\":\"Original London Cast\",\"foreignArtistId\":\"" + LesMis + "\"}}");

    private static LidarrAlbum[] LesMisCatalogue() => new[]
    {
        LesMisGroup("japan-red", "1994-06-01", new[] { "Live", "Soundtrack" }, "Japanese 'Red' cast", new[] { "" }, "JP"),
        LesMisGroup("london-1985", "1985-12-01", new[] { "Soundtrack" }, "1985 London cast", new[] { "", "", "", "" }),
        LesMisGroup("film-2012", "2012-12-11", new[] { "Soundtrack" }, null, new[] { "film cast", "" }, "US"),
        LesMisGroup("studio-1980", "1980-01-01", Array.Empty<string>(), "French original", new[] { "" }, "FR"),
        Rel("single", "Les Mis\u00e9rables", "Single", 1, artist: LesMis),
    };

    private static async Task<CatalogSearchResult> SearchLesMis(params LidarrAlbum[] found)
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => found.Select(a => new LidarrSearchItem { Album = a }).ToList();
        return await catalog.SearchAsync("les miserables", CancellationToken.None);
    }

    [Fact]
    public async Task Search_SameTitleReleaseGroups_AreAllOffered_TheMostLikelyFirst_WithoutTheSameTitleSingle()
    {
        var result = await SearchLesMis(LesMisCatalogue());

        Assert.Equal(new[] { "studio-1980", "london-1985", "film-2012", "japan-red" }, result.Albums.Select(a => a.Mbid));
    }

    [Fact]
    public async Task Search_SameTitleReleaseGroups_ReachTheAppWhatTellsThemApart()
    {
        var result = await SearchLesMis(LesMisCatalogue());
        var byId = result.Albums.ToDictionary(a => a.Mbid);

        Assert.Equal(("1985 London cast", 1985, "GB"), (byId["london-1985"].Disambiguation, byId["london-1985"].Year, byId["london-1985"].Country));
        Assert.Equal(("Japanese 'Red' cast", 1994, "JP"), (byId["japan-red"].Disambiguation, byId["japan-red"].Year, byId["japan-red"].Country));
        Assert.Equal(new[] { "Live", "Soundtrack" }, byId["japan-red"].SecondaryTypes);

        // no release group disambiguation: the first release that has one speaks for it
        Assert.Equal("film cast", byId["film-2012"].Disambiguation);
    }

    [Fact]
    public async Task Search_TheSameTitleInAnyOrder_KeepsTheSameRanking()
    {
        var found = LesMisCatalogue();
        var forward = (await SearchLesMis(found)).Albums.Select(a => a.Mbid).ToList();
        var backward = (await SearchLesMis(found.Reverse().ToArray())).Albums.Select(a => a.Mbid).ToList();

        Assert.Equal(forward, backward);
    }

    [Fact]
    public async Task Search_SameTitleAlbums_TakeTheSlotsTheyHeld_BetweenOtherAlbums()
    {
        var other = Rel("other", "Cats", "Album", 20, artist: LesMis);
        var ids = (await SearchLesMis(LesMisGroup("a", "1994-06-01", new[] { "Live" }, null, new[] { "" }), other, LesMisGroup("b", "1985-12-01", Array.Empty<string>(), null, new[] { "", "" }))).Albums.Select(a => a.Mbid);

        Assert.Equal(new[] { "b", "other", "a" }, ids);
    }

    [Fact]
    public async Task Artist_ListsEverySameTitleReleaseGroup()
    {
        var (env, catalog) = Make();
        using var _ = env;
        env.Lidarr.OnSearch = _ => LesMisCatalogue().Select(a => new LidarrSearchItem { Album = a }).ToList();

        var result = await catalog.ArtistAlbumsAsync(LesMis, "Original London Cast", CancellationToken.None);

        Assert.Equal(new[] { "japan-red", "london-1985", "film-2012", "studio-1980" }.Order(), result.Albums.Where(a => a.AlbumType == "Album").Select(a => a.Mbid).Order());
    }

    [Fact]
    public async Task Search_Json_OmitsTheCountryWhenUnknown()
    {
        var (env, catalog) = Make();
        using var _ = env;
        var result = await catalog.SearchAsync("vald", CancellationToken.None);

        Assert.DoesNotContain("country", JsonSerializer.Serialize(result.Albums[0], ApiJson.Options));
    }
}
