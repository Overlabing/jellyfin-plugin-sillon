using System.Net;
using System.Text;
using Jellyfin.Plugin.Sillon.Lidarr;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class LidarrClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (LidarrClient Client, FakeHandler Handler) Make(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string url = "http://lidarr:8686/")
    {
        var handler = new FakeHandler(respond);
        return (new LidarrClient(new HttpClient(handler), url, "secret-key"), handler);
    }

    [Fact]
    public async Task CutoffUnmetSearch_PostsTheCommand()
    {
        string? body = null;
        var client = new LidarrClient(
            new HttpClient(new FakeHandler(r =>
            {
                body = r.Content!.ReadAsStringAsync().Result;
                Assert.Equal("/api/v1/command", r.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
            })),
            "http://lidarr:8686",
            "k");

        await client.CutoffUnmetSearchAsync();

        Assert.Equal("{\"name\":\"CutoffUnmetAlbumSearch\"}", body);
    }

    [Fact]
    public async Task GetAlbumFolder_IsTheParentOfTheDiscFolders()
    {
        var client = new LidarrClient(
            new HttpClient(new FakeHandler(r => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"path\":\"/m/A/B (2000)/CD 01/1.flac\"},{\"path\":\"/m/A/B (2000)/CD 02/1.flac\"}]")
            })),
            "http://lidarr:8686",
            "k");

        Assert.Equal("/m/A/B (2000)", await client.GetAlbumFolderAsync(41));
    }

    [Fact]
    public async Task SetAnyReleaseOk_PutsTheWholeAlbum_OnlyWhenTheFlagWasOff()
    {
        var flag = "false";
        var (client, handler) = Make(_ => Json("{\"id\":7,\"title\":\"t\",\"anyReleaseOk\":" + flag + ",\"extra\":{\"k\":1}}"));

        await client.SetAnyReleaseOkAsync(7);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("/api/v1/album/7", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("{\"id\":7,\"title\":\"t\",\"anyReleaseOk\":true,\"extra\":{\"k\":1}}", handler.Bodies[1]);

        flag = "true";
        await client.SetAnyReleaseOkAsync(7);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task DownloadedAlbumsScan_PostsTheCommandWithThePath()
    {
        var (client, handler) = Make(_ => Json("{}"));

        await client.DownloadedAlbumsScanAsync("/dl/x");

        Assert.Equal("{\"name\":\"DownloadedAlbumsScan\",\"path\":\"/dl/x\"}", handler.Bodies[0]);
    }

    [Fact]
    public async Task Status_ReturnsVersion_AndSendsApiKeyHeader()
    {
        var (client, handler) = Make(_ => Json("{\"version\":\"2.9.6.4552\",\"other\":1}"));

        var status = await client.GetStatusAsync();

        Assert.Equal("2.9.6.4552", status.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://lidarr:8686/api/v1/system/status", request.RequestUri!.ToString());
        Assert.Equal("secret-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task QualityProfiles_AreMapped()
    {
        var (client, handler) = Make(_ => Json("[{\"id\":1,\"name\":\"Any\"},{\"id\":2,\"name\":\"Lossless\",\"cutoff\":3}]"));

        var profiles = await client.GetQualityProfilesAsync();

        Assert.Equal(new[] { new LidarrProfile(1, "Any"), new LidarrProfile(2, "Lossless") }, profiles);
        Assert.EndsWith("/api/v1/qualityprofile", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task MetadataProfiles_AreMapped()
    {
        var (client, handler) = Make(_ => Json("[{\"id\":5,\"name\":\"Standard\"}]"));

        var profiles = await client.GetMetadataProfilesAsync();

        Assert.Equal(new LidarrProfile(5, "Standard"), Assert.Single(profiles));
        Assert.EndsWith("/api/v1/metadataprofile", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RootFolders_AreMapped()
    {
        var (client, handler) = Make(_ => Json("[{\"id\":1,\"path\":\"/music/\",\"freeSpace\":10}]"));

        var folders = await client.GetRootFoldersAsync();

        Assert.Equal(new LidarrRootFolder(1, "/music/"), Assert.Single(folders));
        Assert.EndsWith("/api/v1/rootfolder", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Clé API refusée")]
    [InlineData(HttpStatusCode.Forbidden, "Clé API refusée")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    public async Task HttpErrors_AreMapped_AndNeverLeakTheKey(HttpStatusCode status, string expected)
    {
        var (client, _) = Make(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<LidarrException>(() => client.GetStatusAsync());

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("secret-key", ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_IsMapped()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("boom"));

        var ex = await Assert.ThrowsAsync<LidarrException>(() => client.GetStatusAsync());

        Assert.Contains("injoignable", ex.Message);
    }

    [Fact]
    public async Task InvalidJson_IsMapped()
    {
        var (client, _) = Make(_ => Json("<html>not json</html>"));

        var ex = await Assert.ThrowsAsync<LidarrException>(() => client.GetStatusAsync());

        Assert.Contains("illisible", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://lidarr")]
    public async Task InvalidUrl_IsRejected_WithoutCallingOut(string url)
    {
        var (client, handler) = Make(_ => Json("{}"), url);

        await Assert.ThrowsAsync<LidarrException>(() => client.GetStatusAsync());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_EscapesTheTerm()
    {
        var (client, handler) = Make(_ => Json("[{\"artist\":{\"artistName\":\"AC/DC\",\"foreignArtistId\":\"x\"}}]"));

        var items = await client.SearchAsync("AC/DC & co");

        Assert.Equal("AC/DC", items[0].Artist!.ArtistName);
        Assert.Equal("/api/v1/search", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?term=AC%2FDC%20%26%20co", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task LookupAlbums_UsesTheAlbumLookupEndpoint()
    {
        var (client, handler) = Make(_ => Json("[{\"id\":0,\"title\":\"XEU\",\"foreignAlbumId\":\"m\"}]"));

        var albums = await client.LookupAlbumsAsync("xeu");

        Assert.Equal("m", Assert.Single(albums).ForeignAlbumId);
        Assert.Equal("/api/v1/album/lookup", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AlbumByForeignId_AndByArtist_UseTheDocumentedQueries()
    {
        var (client, handler) = Make(_ => Json("[]"));

        await client.GetAlbumsByForeignIdAsync("mbid-1");
        await client.GetAlbumsByArtistAsync(7);

        Assert.Equal("?includeAllArtistAlbums=false&foreignAlbumId=mbid-1", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("/api/v1/album?artistId=7", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task AddAlbum_PostsTheMonitoredArtistPayload()
    {
        var (client, handler) = Make(_ => Json("{\"id\":41,\"foreignAlbumId\":\"m\"}"));

        var added = await client.AddAlbumAsync(new LidarrAddAlbum
        {
            ForeignAlbumId = "m",
            Monitored = true,
            AnyReleaseOk = true,
            Artist = new LidarrAddArtist { ForeignArtistId = "a", QualityProfileId = 2, MetadataProfileId = 1, RootFolderPath = "/music", Monitored = true }
        });

        Assert.Equal(41, added.Id);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(
            "{\"foreignAlbumId\":\"m\",\"monitored\":true,\"anyReleaseOk\":true,\"artist\":{\"foreignArtistId\":\"a\",\"qualityProfileId\":2,\"metadataProfileId\":1,\"rootFolderPath\":\"/music\",\"monitored\":true,\"monitorNewItems\":\"none\",\"addOptions\":{\"monitor\":\"none\",\"searchForMissingAlbums\":false}},\"addOptions\":{\"searchForNewAlbum\":false}}",
            handler.Bodies[0]);
    }

    [Fact]
    public async Task MonitorArtist_OfAnUnmonitoredArtist_WritesOnlyTheMonitoredFlag()
    {
        var (client, handler) = Make(r => Json(r.Method == HttpMethod.Get ? "{\"id\":7,\"monitored\":false,\"monitorNewItems\":\"all\"}" : "[]"));

        await client.MonitorArtistAsync(7);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v1/artist/7", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("/api/v1/artist/editor", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("{\"artistIds\":[7],\"monitored\":true}", handler.Bodies[1]);
    }

    [Fact]
    public async Task MonitorArtist_OfAMonitoredArtist_DoesNotWrite()
    {
        var (client, handler) = Make(_ => Json("{\"id\":7,\"monitored\":true}"));

        await client.MonitorArtistAsync(7);

        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Monitor_AndAlbumSearch_SendTheLidarrBodies()
    {
        var (client, handler) = Make(_ => Json("[]"));

        await client.MonitorAlbumsAsync(new[] { 41 }, false);
        await client.AlbumSearchAsync(new[] { 41 });

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/v1/album/monitor", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("{\"albumIds\":[41],\"monitored\":false}", handler.Bodies[0]);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/api/v1/command", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("{\"name\":\"AlbumSearch\",\"albumIds\":[41]}", handler.Bodies[1]);
    }

    [Fact]
    public async Task Monitor_AcceptsAnEmptyAnswer()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await client.MonitorAlbumsAsync(new[] { 1 }, true);
    }

    [Fact]
    public async Task Album_ReadsTheStatistics_AndTheQueueTheRecords()
    {
        var (client, handler) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal)
            ? Json("{\"page\":1,\"records\":[{\"albumId\":41,\"album\":{\"id\":41}},{\"albumId\":0,\"album\":{\"id\":42}}]}")
            : Json("{\"id\":41,\"statistics\":{\"percentOfTracks\":94.1,\"trackFileCount\":16,\"trackCount\":17},\"artist\":{\"path\":\"/music/Vald\"}}"));

        var album = await client.GetAlbumAsync(41);
        var queue = await client.GetQueueAsync();

        Assert.Equal((16, 17), (album.Statistics!.TrackFileCount, album.Statistics.TrackCount));
        Assert.Equal("/music/Vald", album.Artist!.Path);
        Assert.Equal(new[] { 41, 0 }, queue.Select(q => q.AlbumId));
        Assert.Equal("?includeAlbum=true&pageSize=200", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task TrackFiles_AreListedByAlbum()
    {
        var (client, handler) = Make(_ => Json("[{\"path\":\"/music/a.flac\"}]"));

        var files = await client.GetTrackFilesAsync(41);

        Assert.Equal("/music/a.flac", Assert.Single(files).Path);
        Assert.Equal("/api/v1/trackfile?albumId=41", handler.Requests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task TheKeyIsSentOnEveryCall_AndNeverInAnError()
    {
        var (client, handler) = Make(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<LidarrException>(() => client.AddAlbumAsync(new LidarrAddAlbum()));

        Assert.DoesNotContain("secret-key", ex.Message);
        Assert.Equal("secret-key", Assert.Single(handler.Requests[0].Headers.GetValues("X-Api-Key")));
    }
}
