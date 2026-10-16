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
        return (env, new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance));
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
            "{\"mbid\":\"" + Xeu + "\",\"artistMbid\":\"" + Vald + "\",\"title\":\"XEU\",\"artist\":\"Vald\",\"year\":2022,\"type\":\"Album\",\"coverUrl\":\"https://img/xeu.jpg\",\"state\":\"requested\",\"requestId\":1}",
            xeu.GetRawText());
        // no request: requestId is absent, not null
        Assert.False(json.GetProperty("albums")[1].TryGetProperty("requestId", out var ignored));
        Assert.Equal(
            "{\"mbid\":\"" + Vald + "\",\"name\":\"Vald\",\"disambiguation\":\"rap\",\"images\":[{\"coverType\":\"poster\",\"url\":\"https://img/vald.jpg\"}]}",
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
}
