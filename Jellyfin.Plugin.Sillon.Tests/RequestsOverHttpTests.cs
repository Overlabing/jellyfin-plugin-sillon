using System.Net;
using System.Text;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>The request service over the real Lidarr client and a fake HTTP handler.</summary>
public class RequestsOverHttpTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        public bool RefuseAdd { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {body}".Trim());
            Assert.Equal("lk", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/v1/album" && request.Method == HttpMethod.Get)
            {
                return Json("[]");
            }

            if (path == "/api/v1/album" && request.Method == HttpMethod.Post)
            {
                return RefuseAdd
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("SECRET-DETAIL") }
                    : Json("{\"id\":41,\"foreignAlbumId\":\"alb-1\",\"artistId\":9}");
            }

            if (path == "/api/v1/album/41" && request.Method == HttpMethod.Get)
            {
                return Json("{\"id\":41,\"duration\":240000}");
            }

            if (path == "/api/v1/artist/9" && request.Method == HttpMethod.Get)
            {
                return Json("{\"id\":9,\"monitored\":false}");
            }

            return Json("[]");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (TestEnv Env, Handler Handler, RequestService Service) Make()
    {
        var env = new TestEnv();
        var handler = new Handler();
        var provider = new LidarrProvider(new Factory(handler), () => env.Config);
        var service = new RequestService(env.Store, provider, env.Library, env.Notifier, () => env.Config, NullLogger<RequestService>.Instance, now: () => env.Now);
        return (env, handler, service);
    }

    [Fact]
    public async Task Create_TalksToLidarrInTheDocumentedOrder()
    {
        var (env, handler, service) = Make();
        using var _ = env;

        var request = await service.CreateAsync(TestEnv.Input(), CancellationToken.None);

        Assert.Equal(41, request.LidarrAlbumId);
        Assert.Equal(7, handler.Calls.Count);
        Assert.Equal("GET /api/v1/album?includeAllArtistAlbums=false&foreignAlbumId=alb-1", handler.Calls[0]);
        Assert.StartsWith("POST /api/v1/album {\"foreignAlbumId\":\"alb-1\",\"monitored\":true,", handler.Calls[1]);
        Assert.Contains("\"qualityProfileId\":2,\"metadataProfileId\":1,\"rootFolderPath\":\"/data/media/music\"", handler.Calls[1]);
        Assert.Equal("GET /api/v1/album/41", handler.Calls[2]);
        Assert.Equal("GET /api/v1/artist/9", handler.Calls[3]);
        Assert.Equal("PUT /api/v1/artist/editor {\"artistIds\":[9],\"monitored\":true}", handler.Calls[4]);
        Assert.Equal("PUT /api/v1/album/monitor {\"albumIds\":[41],\"monitored\":true}", handler.Calls[5]);
        Assert.Equal("POST /api/v1/command {\"name\":\"AlbumSearch\",\"albumIds\":[41]}", handler.Calls[6]);
    }

    [Fact]
    public async Task Create_WhenLidarrAnswers500_StoresAGenericFailure()
    {
        var (env, handler, service) = Make();
        using var _ = env;
        handler.RefuseAdd = true;

        await Assert.ThrowsAsync<LidarrAddException>(() => service.CreateAsync(TestEnv.Input(), CancellationToken.None));

        var stored = Assert.Single(env.Store.List(null));
        Assert.Equal(RequestStatus.Failed, stored.Status);
        Assert.DoesNotContain("SECRET", stored.Error);
    }
}
