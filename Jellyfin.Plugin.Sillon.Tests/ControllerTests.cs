using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class ControllerTests
{
    private const string Other = "0000000000000000000000000000bbbb";
    private const string Admin = "0000000000000000000000000000cccc";
    private const string Xeu = "5f0e7c3a-2b1d-4e8f-9a6b-7d3c1e5a9b22";
    private const string Vald = "9d2d1c4e-7a0b-4f5c-8d6a-0c6f6d2a1b11";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ClaimsPrincipal Principal(string userId, bool admin = false)
    {
        var claims = new List<Claim> { new("Jellyfin-UserId", userId) };
        if (admin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static T WithContext<T>(T controller, ClaimsPrincipal? user, string body = "")
        where T : ControllerBase
    {
        var http = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static RequestsController Requests(TestEnv env, ClaimsPrincipal? user, string body = "")
    {
        env.Library.Users[TestEnv.User] = "JB";
        env.Library.Users[Other] = "Other";
        env.Library.Users[Admin] = "admin";
        var catalog = new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance);
        return WithContext(
            new RequestsController(env.Service, env.Store, env.Devices, catalog, env.Library, env.Notifier, () => env.Config),
            user,
            body);
    }

    private static WebhookController Webhook(TestEnv env, string body)
        => WithContext(new WebhookController(env.Service, () => env.Config, NullLogger<WebhookController>.Instance), null, body);

    private static JsonElement Json(ActionResult result, out int status)
    {
        var content = Assert.IsType<ContentResult>(result);
        status = content.StatusCode ?? 200;
        return JsonDocument.Parse(content.Content!).RootElement;
    }

    private static string CreateBody(string album = Xeu) =>
        "{\"foreignAlbumId\":\"" + album + "\",\"foreignArtistId\":\"" + Vald + "\",\"title\":\"XEU\",\"artist\":\"Vald\",\"year\":2022,\"coverUrl\":\"https://img/xeu.jpg\"}";

    [Fact]
    public void Me_ReturnsTheGatewayShape()
    {
        using var env = new TestEnv();

        var json = Json(Requests(env, Principal(TestEnv.User)).Me(), out var status);

        Assert.Equal(200, status);
        Assert.Equal("{\"userId\":\"" + TestEnv.User + "\",\"name\":\"JB\",\"isAdmin\":false,\"pushEnabled\":true}", json.GetRawText());
    }

    [Fact]
    public void Me_WithoutAUserClaim_IsUnauthorized()
    {
        using var env = new TestEnv();

        Assert.IsType<UnauthorizedResult>(Requests(env, null).Me());
    }

    [Fact]
    public async Task CreateRequest_ReturnsTheRequestInTheGatewayShape()
    {
        using var env = new TestEnv();

        var json = Json(await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None), out var status);

        Assert.Equal(200, status);
        Assert.Equal(1, json.GetProperty("id").GetInt64());
        Assert.Equal("requested", json.GetProperty("status").GetString());
        Assert.Equal(101, json.GetProperty("lidarrAlbumId").GetInt64());
        Assert.Equal(TestEnv.User, json.GetProperty("userId").GetString());
        Assert.Equal("JB", json.GetProperty("userName").GetString());
        Assert.Equal(2022, json.GetProperty("year").GetInt32());
        Assert.Equal(Xeu, json.GetProperty("foreignAlbumId").GetString());
        // optional fields are absent, not null or empty
        Assert.False(json.TryGetProperty("error", out _));
        Assert.False(json.TryGetProperty("jellyfinItemId", out _));
        Assert.False(json.TryGetProperty("tracksImported", out _));
        Assert.False(json.TryGetProperty("libraryAddedAt", out _));
        // the add asked Lidarr to search
        Assert.Equal("2026-10-01T12:00:00Z", json.GetProperty("lastSearchAt").GetString());
        // RFC 3339 dates the app parses
        Assert.Equal("2026-10-01T12:00:00Z", json.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task CreateRequest_AValidDuplicate_Is409WithTheExistingRequest()
    {
        using var env = new TestEnv();
        await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None);

        var json = Json(await Requests(env, Principal(Admin, admin: true), CreateBody()).CreateRequest(CancellationToken.None), out var status);

        Assert.Equal(409, status);
        Assert.Equal("Cet album est déjà dans votre liste d'envies", json.GetProperty("error").GetString());
        Assert.Equal(1, json.GetProperty("request").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task CreateRequest_AnAlbumOutsideTheAllowList_Is400WithAFrenchMessage()
    {
        using var env = new TestEnv();
        env.Config.CatalogAllowList = "something-else";

        var json = Json(await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None), out var status);

        Assert.Equal(400, status);
        Assert.Equal("Cet album n'est pas proposé sur ce serveur.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateRequest_MissingIds_Is400_AndBadJson_Is400()
    {
        using var env = new TestEnv();

        Json(await Requests(env, Principal(TestEnv.User), "{\"title\":\"x\"}").CreateRequest(CancellationToken.None), out var missing);
        Json(await Requests(env, Principal(TestEnv.User), "{not json").CreateRequest(CancellationToken.None), out var bad);

        Assert.Equal(400, missing);
        Assert.Equal(400, bad);
    }

    [Fact]
    public async Task CreateRequest_WhenLidarrRefuses_Is502WithAGenericMessage()
    {
        using var env = new TestEnv();
        env.Lidarr.AddError = new Jellyfin.Plugin.Sillon.Lidarr.LidarrException("SECRET-DETAIL");

        var json = Json(await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None), out var status);

        Assert.Equal(502, status);
        Assert.Equal("ajout Lidarr refusé", json.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET", json.GetRawText());
    }

    [Fact]
    public async Task CreateRequest_InLidarrModeWithoutLidarrSetUp_Is503()
    {
        using var env = new TestEnv();
        env.Config.LidarrApiKey = string.Empty;

        Json(await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None), out var status);

        Assert.Equal(503, status);
    }

    [Fact]
    public async Task ListRequests_UsersSeeTheirOwn_AdminsSeeAll()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        await Requests(env, Principal(TestEnv.User), CreateBody("alb-1")).CreateRequest(CancellationToken.None);
        await Requests(env, Principal(Other), CreateBody("alb-2")).CreateRequest(CancellationToken.None);

        var own = Json(Requests(env, Principal(TestEnv.User)).ListRequests(), out _).GetProperty("requests");
        var all = Json(Requests(env, Principal(Admin, admin: true)).ListRequests(), out _).GetProperty("requests");

        Assert.Equal(1, own.GetArrayLength());
        Assert.Equal(2, all.GetArrayLength());
        Assert.Equal(2, all[0].GetProperty("id").GetInt64());
        Assert.False(all[0].TryGetProperty("lastSearchAt", out _));
    }

    [Fact]
    public async Task ListRequests_CarriesLastSearchAt_WhenLidarrWasAsked()
    {
        using var env = new TestEnv();
        await Requests(env, Principal(TestEnv.User), CreateBody("alb-1")).CreateRequest(CancellationToken.None);

        var list = Json(Requests(env, Principal(TestEnv.User)).ListRequests(), out _).GetProperty("requests");

        Assert.Equal("2026-10-01T12:00:00Z", list[0].GetProperty("lastSearchAt").GetString());
    }

    [Fact]
    public async Task RetryRequest_FollowsTheOwnershipAndStateRules()
    {
        using var env = new TestEnv();
        env.Lidarr.AddError = new Lidarr.LidarrException("down");
        await Requests(env, Principal(TestEnv.User), CreateBody("alb-1")).CreateRequest(CancellationToken.None);
        env.Lidarr.AddError = null;
        await Requests(env, Principal(Other), CreateBody("alb-2")).CreateRequest(CancellationToken.None);
        env.Now = env.Now.AddMinutes(1);
        var controller = Requests(env, Principal(TestEnv.User));

        Json(await controller.RetryRequest("2", CancellationToken.None), out var stranger);
        var own = Json(await controller.RetryRequest("1", CancellationToken.None), out var ownStatus);
        var byAdmin = Json(await Requests(env, Principal(Admin, admin: true)).RetryRequest("2", CancellationToken.None), out var adminStatus);
        await controller.CancelRequest("1", CancellationToken.None);
        Json(await controller.RetryRequest("1", CancellationToken.None), out var cancelled);
        Json(await controller.RetryRequest("99", CancellationToken.None), out var missing);
        Json(await controller.RetryRequest("abc", CancellationToken.None), out var invalid);
        var anonymous = await Requests(env, null).RetryRequest("1", CancellationToken.None);

        Assert.Equal(403, stranger);
        Assert.Equal(200, ownStatus);
        Assert.Equal("requested", own.GetProperty("status").GetString());
        Assert.False(own.TryGetProperty("error", out _));
        Assert.Equal("2026-10-01T12:01:00Z", own.GetProperty("lastSearchAt").GetString());
        Assert.Equal(200, adminStatus);
        Assert.Equal(2, byAdmin.GetProperty("id").GetInt64());
        Assert.Equal(409, cancelled);
        Assert.Equal(404, missing);
        Assert.Equal(400, invalid);
        Assert.IsType<UnauthorizedResult>(anonymous);
    }

    [Fact]
    public async Task RetryRequest_InManualMode_Is409WithAFrenchMessage()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        await Requests(env, Principal(TestEnv.User), CreateBody("alb-1")).CreateRequest(CancellationToken.None);

        var json = Json(await Requests(env, Principal(TestEnv.User)).RetryRequest("1", CancellationToken.None), out var status);

        Assert.Equal(409, status);
        Assert.Contains("mode manuel", json.GetProperty("error").GetString());
    }

    [Fact]
    public void EmptyList_IsAnEmptyArray()
    {
        using var env = new TestEnv();

        var json = Json(Requests(env, Principal(TestEnv.User)).ListRequests(), out _);

        Assert.Equal("{\"requests\":[]}", json.GetRawText());
    }

    [Fact]
    public async Task CancelRequest_FollowsTheOwnershipAndStatusRules()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        await Requests(env, Principal(TestEnv.User), CreateBody("alb-1")).CreateRequest(CancellationToken.None);
        await Requests(env, Principal(Other), CreateBody("alb-2")).CreateRequest(CancellationToken.None);
        var controller = Requests(env, Principal(TestEnv.User));

        Json(await controller.CancelRequest("2", CancellationToken.None), out var stranger);
        var own = Json(await controller.CancelRequest("1", CancellationToken.None), out var ownStatus);
        Json(await controller.CancelRequest("1", CancellationToken.None), out var again);
        Json(await Requests(env, Principal(Admin, admin: true)).CancelRequest("2", CancellationToken.None), out var byAdmin);
        Json(await controller.CancelRequest("99", CancellationToken.None), out var missing);
        Json(await controller.CancelRequest("abc", CancellationToken.None), out var invalid);

        Assert.Equal(403, stranger);
        Assert.Equal(200, ownStatus);
        Assert.Equal("cancelled", own.GetProperty("status").GetString());
        Assert.Equal(409, again);
        Assert.Equal(200, byAdmin);
        Assert.Equal(404, missing);
        Assert.Equal(400, invalid);
    }

    [Fact]
    public async Task MarkAvailable_SettlesALiveRequest_AndRefusesASettledOne()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        await Requests(env, Principal(TestEnv.User), CreateBody("alb-1")).CreateRequest(CancellationToken.None);
        var admin = Requests(env, Principal(Admin, admin: true));

        var json = Json(admin.MarkAvailable("1"), out var status);
        await env.Service.WaitAsync();
        Json(admin.MarkAvailable("1"), out var again);
        Json(admin.MarkAvailable("42"), out var missing);

        Assert.Equal(200, status);
        Assert.Equal("available", json.GetProperty("status").GetString());
        Assert.Equal(409, again);
        Assert.Equal(404, missing);
    }

    [Fact]
    public async Task Devices_AreRegisteredPerUser_ValidatedAndOnlyDeletedByTheirOwner()
    {
        using var env = new TestEnv();

        Json(await Requests(env, Principal(TestEnv.User), "{\"token\":\"abc123\",\"platform\":\"ios\",\"env\":\"development\"}").PutDevice(CancellationToken.None), out var ok);
        Json(await Requests(env, Principal(TestEnv.User), "{\"platform\":\"ios\"}").PutDevice(CancellationToken.None), out var noToken);
        Json(await Requests(env, Principal(TestEnv.User), "{\"token\":\"not-hex!\"}").PutDevice(CancellationToken.None), out var notHex);
        await Requests(env, Principal(TestEnv.User), "{\"token\":\"bb\",\"env\":\"staging\"}").PutDevice(CancellationToken.None);

        Assert.Equal(200, ok);
        Assert.Equal(400, noToken);
        Assert.Equal(400, notHex);
        var devices = env.Devices.For(TestEnv.User);
        Assert.Equal(new[] { "abc123", "bb" }, devices.Select(d => d.Token));
        Assert.Equal(new[] { "development", string.Empty }, devices.Select(d => d.Env));

        Requests(env, Principal(Other)).DeleteDevice("abc123");
        Assert.Equal(2, env.Devices.For(TestEnv.User).Count);
        var json = Json(Requests(env, Principal(TestEnv.User)).DeleteDevice("abc123"), out _);
        Assert.Equal("{\"ok\":true}", json.GetRawText());
        Assert.Single(env.Devices.For(TestEnv.User));
    }

    [Fact]
    public async Task CatalogSearch_NeedsAQuery_AndIsUnavailableInManualMode()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        var controller = Requests(env, Principal(TestEnv.User));

        Json(await controller.CatalogSearch(" ", CancellationToken.None), out var empty);
        var json = Json(await controller.CatalogSearch("vald", CancellationToken.None), out var manual);

        Assert.Equal(400, empty);
        Assert.Equal(503, manual);
        Assert.Contains("mode manuel", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("s3cret", 200)]
    [InlineData("wrong", 401)]
    [InlineData("", 401)]
    [InlineData(null, 401)]
    public async Task Webhook_ChecksTheSecret(string? secret, int expected)
    {
        using var env = new TestEnv();

        Json(await Webhook(env, Fixture("lidarr_test.json")).Lidarr(secret, CancellationToken.None), out var status);

        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task Webhook_WithoutAConfiguredSecret_RefusesEverything()
    {
        using var env = new TestEnv();
        env.Config.WebhookSecret = string.Empty;

        Json(await Webhook(env, Fixture("lidarr_test.json")).Lidarr(string.Empty, CancellationToken.None), out var status);

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Webhook_GrabThenDownload_MovesTheRequestToAvailable_AndRemapsThePath()
    {
        using var env = new TestEnv();
        env.Library.AlbumPresent = true;
        await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None);

        Json(await Webhook(env, Fixture("lidarr_grab.json")).Lidarr("s3cret", CancellationToken.None), out var grab);
        Assert.Equal(200, grab);
        Assert.Equal(RequestStatus.Downloading, env.Store.Get(1)!.Status);

        Json(await Webhook(env, Fixture("lidarr_download.json")).Lidarr("s3cret", CancellationToken.None), out var download);
        await env.Service.WaitAsync();

        Assert.Equal(200, download);
        Assert.Equal(RequestStatus.Available, env.Store.Get(1)!.Status);
        Assert.Equal("/data/music/Vald/XEU (2022)", Assert.Single(env.Library.Reported));
        Assert.Single(env.Library.Favorites);
    }

    [Fact]
    public async Task Webhook_AcceptsASingularAlbumDownload()
    {
        using var env = new TestEnv();
        await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None);

        await Webhook(env, Fixture("lidarr_download_singular.json")).Lidarr("s3cret", CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(RequestStatus.Available, env.Store.Get(1)!.Status);
    }

    [Fact]
    public async Task Webhook_AlbumImportEvents_AreHandledLikeDownloads()
    {
        using var env = new TestEnv();
        await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None);

        await Webhook(env, Fixture("lidarr_download.json").Replace("\"Download\"", "\"AlbumImport\"")).Lidarr("s3cret", CancellationToken.None);
        await env.Service.WaitAsync();

        Assert.Equal(RequestStatus.Available, env.Store.Get(1)!.Status);
    }

    [Fact]
    public async Task Webhook_ImportFailure_RecordsTheGenericError()
    {
        using var env = new TestEnv();
        await Requests(env, Principal(TestEnv.User), CreateBody()).CreateRequest(CancellationToken.None);

        await Webhook(env, Fixture("lidarr_import_failure.json")).Lidarr("s3cret", CancellationToken.None);

        var stored = env.Store.Get(1)!;
        Assert.Equal(RequestStatus.Requested, stored.Status);
        Assert.Equal("import échoué", stored.Error);
    }

    [Fact]
    public async Task Webhook_IgnoresUnknownAlbumsAndEvents_AndRejectsBadJson()
    {
        using var env = new TestEnv();

        foreach (var name in new[] { "lidarr_grab.json", "lidarr_download.json", "lidarr_import_failure.json" })
        {
            Json(await Webhook(env, Fixture(name)).Lidarr("s3cret", CancellationToken.None), out var status);
            Assert.Equal(200, status);
        }

        Json(await Webhook(env, "{\"eventType\":\"Rename\"}").Lidarr("s3cret", CancellationToken.None), out var rename);
        Json(await Webhook(env, "{not json").Lidarr("s3cret", CancellationToken.None), out var bad);

        Assert.Equal(200, rename);
        Assert.Equal(400, bad);
        Assert.Empty(env.Library.Reported);
    }

    [Fact]
    public void WebhookSecretCheck_IsExactAndNeverAcceptsAnEmptySecret()
    {
        Assert.True(WebhookController.SecretMatches("abc", "abc"));
        Assert.False(WebhookController.SecretMatches("abd", "abc"));
        Assert.False(WebhookController.SecretMatches("abcd", "abc"));
        Assert.False(WebhookController.SecretMatches(string.Empty, string.Empty));
        Assert.False(WebhookController.SecretMatches(null, string.Empty));
    }

    [Fact]
    public void Config_HidesTheFeature_WhenNotExposedToApps()
    {
        using var env = new TestEnv(FulfillmentMode.Manual);
        env.Config.ExposeToApps = false;
        var controller = new SillonController(NullLogger<SillonController>.Instance, null!, () => env.Config);

        var result = Assert.IsType<JsonResult>(controller.GetConfig());
        var json = JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(0, json.GetProperty("features").GetArrayLength());
        Assert.True(env.Config.RequestsEnabled());
    }

    [Theory]
    [InlineData(FulfillmentMode.Manual, "", true)]
    [InlineData(FulfillmentMode.Lidarr, "key", true)]
    [InlineData(FulfillmentMode.Lidarr, "", false)]
    public void Config_AdvertisesTheRequestsFeature(FulfillmentMode mode, string key, bool expected)
    {
        using var env = new TestEnv(mode);
        env.Config.LidarrApiKey = key;
        var controller = new SillonController(NullLogger<SillonController>.Instance, null!, () => env.Config);

        var result = Assert.IsType<JsonResult>(controller.GetConfig());
        var json = JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(mode.ToString().ToLowerInvariant(), json.GetProperty("mode").GetString());
        Assert.Equal(expected, json.GetProperty("features").EnumerateArray().Any(f => f.GetString() == "requests"));
        Assert.Equal("0.6.1.0", json.GetProperty("version").GetString());
    }
}
