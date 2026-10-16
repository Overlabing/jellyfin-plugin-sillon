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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
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
}
