using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class AdminTests
{
    private const string Alice = "0000000000000000000000000000a11c";

    private static AdminController Controller(TestEnv env, out AdminService service, string body = "")
    {
        env.Config.ApnsEnabled = true;
        env.Config.ApnsKeyId = "KEY";
        env.Config.ApnsTeamId = "TEAM";
        env.Config.ApnsPrivateKeyValue = "secret-key-material";
        env.Library.Users[Alice] = "Alice";
        var notifier = new ApnsNotifier(env.Devices, new NullSender(), () => env.Config, _ => null, NullLogger<ApnsNotifier>.Instance);
        service = new AdminService(env.Store, env.Devices, env.Library, env.Lidarr, notifier, () => env.Config, "9.9.9.0");
        var controller = new AdminController(service, env.Store, env.Devices);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static JsonElement Json(ActionResult result, out int status)
    {
        var content = Assert.IsType<ContentResult>(result);
        status = content.StatusCode ?? 200;
        return JsonDocument.Parse(content.Content!).RootElement;
    }

    [Fact]
    public void EveryAdminEndpointRequiresElevation()
    {
        var attribute = Assert.Single(typeof(AdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("RequiresElevation", attribute.Policy);
        Assert.Empty(typeof(AdminController).GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
    }

    [Fact]
    public async Task Health_ReportsLidarrPushAndCounts_WithoutAnySecret()
    {
        using var env = new TestEnv();
        env.Store.Create(new RequestRecord { UserId = Alice, ForeignAlbumId = "a1", ForeignArtistId = "x" });
        env.Devices.Upsert("abcdef0123456789", Alice, "ios", "production");
        var controller = Controller(env, out _);

        var json = Json(await controller.Health(default), out var status);

        Assert.Equal(200, status);
        Assert.Equal("9.9.9.0", json.GetProperty("version").GetString());
        Assert.True(json.GetProperty("lidarr").GetProperty("reachable").GetBoolean());
        Assert.Equal("2.9.6.4552", json.GetProperty("lidarr").GetProperty("version").GetString());
        Assert.True(json.GetProperty("push").GetProperty("enabled").GetBoolean());
        Assert.Equal("config", json.GetProperty("push").GetProperty("keySource").GetString());
        Assert.Equal(1, json.GetProperty("push").GetProperty("devices").GetInt32());
        Assert.Equal(1, json.GetProperty("requests").GetProperty("requested").GetInt32());
        var raw = json.GetRawText();
        Assert.DoesNotContain("secret-key-material", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lk\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef0123456789", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_ReportsUnreachableLidarr_WithTheMessage()
    {
        using var env = new TestEnv();
        env.Lidarr.StatusError = new LidarrException("Lidarr refused the API key");
        var controller = Controller(env, out _);

        var json = Json(await controller.Health(default), out _);

        Assert.True(json.GetProperty("lidarr").GetProperty("configured").GetBoolean());
        Assert.False(json.GetProperty("lidarr").GetProperty("reachable").GetBoolean());
        Assert.Equal("Lidarr refused the API key", json.GetProperty("lidarr").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Health_DoesNotCallLidarrWhenItIsNotConfigured()
    {
        using var env = new TestEnv();
        env.Config.LidarrApiKey = string.Empty;
        env.Lidarr.StatusError = new InvalidOperationException("must not be called");
        var controller = Controller(env, out _);

        var json = Json(await controller.Health(default), out _);

        Assert.False(json.GetProperty("lidarr").GetProperty("configured").GetBoolean());
    }

    [Fact]
    public void Devices_AreGroupedByUser_AndNeverShowTheToken()
    {
        using var env = new TestEnv();
        env.Devices.Upsert("0011223344556677889900", Alice, "ios", "production");
        env.Devices.Upsert("ffeeddccbbaa", "0000000000000000000000000000dead", "macos", string.Empty);
        var controller = Controller(env, out _);

        var json = Json(controller.Devices(), out _);

        var users = json.GetProperty("users").EnumerateArray().ToList();
        Assert.Equal(2, users.Count);
        Assert.Equal(string.Empty, users[0].GetProperty("userName").GetString());
        Assert.Equal("Alice", users[1].GetProperty("userName").GetString());
        var device = users[1].GetProperty("devices")[0];
        Assert.Equal("889900", device.GetProperty("tokenHint").GetString());
        Assert.Equal(16, device.GetProperty("id").GetString()!.Length);
        Assert.DoesNotContain("0011223344556677889900", json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteDevice_RemovesItById_AndValidatesTheId()
    {
        using var env = new TestEnv();
        env.Devices.Upsert("aa01", Alice, "ios", "production");
        var id = env.Devices.All()[0].PublicId;
        var controller = Controller(env, out _);

        Json(controller.DeleteDevice("not-an-id"), out var bad);
        Json(controller.DeleteDevice("0000000000000000"), out var missing);
        Json(controller.DeleteDevice(id), out var ok);

        Assert.Equal(400, bad);
        Assert.Equal(404, missing);
        Assert.Equal(200, ok);
        Assert.Empty(env.Devices.All());
    }

    [Fact]
    public void DeleteRequest_RemovesAFinishedOne_AndRefusesALiveOne()
    {
        using var env = new TestEnv();
        var live = env.Store.Create(new RequestRecord { UserId = Alice, ForeignAlbumId = "a1", ForeignArtistId = "x" });
        var done = env.Store.Create(new RequestRecord { UserId = Alice, ForeignAlbumId = "a2", ForeignArtistId = "x" });
        env.Store.Transition(done.Id, RequestStatus.Live, RequestStatus.Failed, "boom");
        var controller = Controller(env, out _);

        Json(controller.DeleteRequest("abc"), out var bad);
        Json(controller.DeleteRequest("99"), out var missing);
        Json(controller.DeleteRequest(live.Id.ToString()), out var conflict);
        Json(controller.DeleteRequest(done.Id.ToString()), out var ok);

        Assert.Equal(400, bad);
        Assert.Equal(404, missing);
        Assert.Equal(409, conflict);
        Assert.Equal(200, ok);
        Assert.Equal(new[] { live.Id }, env.Store.List(null).Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData("{\"olderThanDays\":0}")]
    [InlineData("{\"olderThanDays\":99999}")]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task Purge_RejectsBadInput(string body)
    {
        using var env = new TestEnv();
        var controller = Controller(env, out _, body);

        Json(await controller.Purge(default), out var status);

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Purge_RemovesOnlyOldFinishedRequests()
    {
        using var env = new TestEnv();
        var old = env.Store.Create(new RequestRecord { UserId = Alice, ForeignAlbumId = "a1", ForeignArtistId = "x" });
        env.Store.Transition(old.Id, RequestStatus.Live, RequestStatus.Cancelled, string.Empty);
        var oldLive = env.Store.Create(new RequestRecord { UserId = Alice, ForeignAlbumId = "a2", ForeignArtistId = "x" });
        env.Now = env.Now.AddDays(40);
        var recent = env.Store.Create(new RequestRecord { UserId = Alice, ForeignAlbumId = "a3", ForeignArtistId = "x" });
        env.Store.Transition(recent.Id, RequestStatus.Live, RequestStatus.Failed, "x");
        var controller = Controller(env, out _, "{\"olderThanDays\":30}");

        var json = Json(await controller.Purge(default), out var status);

        Assert.Equal(200, status);
        Assert.Equal(1, json.GetProperty("removed").GetInt32());
        Assert.Equal(new[] { recent.Id, oldLive.Id }, env.Store.List(null).Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("lidarr:8686")]
    [InlineData("javascript:alert(1)")]
    public async Task LidarrTest_RejectsAMissingOrNonHttpUrl_BeforeAnyCall(string url)
    {
        using var env = new TestEnv();
        var controller = new SillonController(NullLogger<SillonController>.Instance, null!, () => env.Config);

        var result = await controller.TestLidarr(new LidarrTestRequest(url, "***"), default);

        Assert.Equal(400, Assert.IsType<JsonResult>(result).StatusCode);
    }

    // request limits

    [Fact]
    public async Task Limits_StopAUserWithTooManyActiveRequests_ButNotAnotherUserOrAnAdmin()
    {
        using var env = new TestEnv();
        env.Config.MaxActiveRequestsPerUser = 2;
        await env.Service.CreateAsync(TestEnv.Input(album: "a1"), default);
        await env.Service.CreateAsync(TestEnv.Input(album: "a2"), default);

        await Assert.ThrowsAsync<RequestLimitException>(() => env.Service.CreateAsync(TestEnv.Input(album: "a3"), default));
        await env.Service.CreateAsync(TestEnv.Input(user: Alice, album: "a3"), default);
        await env.Service.CreateAsync(TestEnv.Input(album: "a4") with { IsAdmin = true }, default);

        env.Config.ExemptAdminsFromLimits = false;
        await Assert.ThrowsAsync<RequestLimitException>(() => env.Service.CreateAsync(TestEnv.Input(album: "a5") with { IsAdmin = true }, default));
    }

    [Fact]
    public async Task Limits_CountEveryRequestOfTheLast24Hours_EvenCancelledOnes()
    {
        using var env = new TestEnv();
        env.Config.MaxRequestsPerUserPerDay = 2;
        var first = await env.Service.CreateAsync(TestEnv.Input(album: "a1"), default);
        await env.Service.CancelAsync(first.Id, TestEnv.User, false, default);
        await env.Service.CreateAsync(TestEnv.Input(album: "a2"), default);

        var ex = await Assert.ThrowsAsync<RequestLimitException>(() => env.Service.CreateAsync(TestEnv.Input(album: "a3"), default));
        Assert.Contains("2 requests per day", ex.Message, StringComparison.Ordinal);

        env.Now = env.Now.AddHours(25);
        await env.Service.CreateAsync(TestEnv.Input(album: "a3"), default);
    }

    [Fact]
    public async Task Limits_AreOffByDefault()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 6; i++)
        {
            await env.Service.CreateAsync(TestEnv.Input(album: "a" + i), default);
        }

        Assert.Equal(6, env.Store.CountActive(TestEnv.User));
    }

    private sealed class NullSender : IPushSender
    {
        public Task<PushResult> SendAsync(ApnsCredentials? credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct)
            => Task.FromResult(PushResult.Sent);
    }
}
