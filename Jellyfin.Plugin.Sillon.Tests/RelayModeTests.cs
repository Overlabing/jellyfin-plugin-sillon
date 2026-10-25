using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>Modes, the upgrade guarantee, relay registration, and the choice of sender per device.</summary>
public class RelayModeTests
{
    private const string Other = "0000000000000000000000000000bbbb";

    private static readonly string DeviceKey = RelayCrypto.B64(RelayCryptoTests.Hex(RelayCryptoTests.DevicePublic));

    private static string? NoEnv(string name) => null;

    // modes and the upgrade

    [Fact]
    public void ANewInstall_DefaultsToRelay_WithTheOfficialRelayUrl()
    {
        var config = new PluginConfiguration();

        Assert.Equal("relay", config.ResolvePushMode(NoEnv));
        Assert.Equal(PluginConfiguration.DefaultPushRelayUrl, config.PushRelayUrl);
    }

    [Theory]
    [InlineData(true, "", "", "")]
    [InlineData(false, "-----BEGIN PRIVATE KEY-----", "", "")]
    [InlineData(false, "", "KEYID", "")]
    [InlineData(true, "-----BEGIN PRIVATE KEY-----", "KEYID", "TEAM")]
    public void AnInstallThatHoldsAnApnsKey_StaysDirect_AfterTheUpgrade(bool enabled, string key, string keyId, string teamId)
    {
        var config = new PluginConfiguration { ApnsEnabled = enabled, ApnsPrivateKeyValue = key, ApnsKeyId = keyId, ApnsTeamId = teamId };

        Assert.Equal("direct", config.ResolvePushMode(NoEnv));
        Assert.True(config.EnsurePushMode(NoEnv));
        Assert.Equal("direct", config.PushMode);
        Assert.False(config.EnsurePushMode(NoEnv));
    }

    [Fact]
    public void AnInstallWhoseKeyComesFromTheEnvironment_StaysDirect()
    {
        var config = new PluginConfiguration();

        Assert.Equal("direct", config.ResolvePushMode(name => name == "SILLON_APNS_KEY" ? "pem" : null));
    }

    [Fact]
    public void TheDecisionIsMadeOnce_AndTheAdministratorsChoiceAlwaysWins()
    {
        var config = new PluginConfiguration { ApnsEnabled = true, ApnsPrivateKeyValue = "k", ApnsKeyId = "K", ApnsTeamId = "T" };
        config.EnsurePushMode(NoEnv);
        config.ApnsPrivateKeyValue = string.Empty;
        config.ApnsEnabled = false;

        // the key was cleared later: it does not silently flip to relay
        Assert.Equal("direct", config.ResolvePushMode(NoEnv));

        config.PushMode = "off";
        Assert.Equal("off", config.ResolvePushMode(NoEnv));
        config.PushMode = "RELAY ";
        config.Sanitize();
        Assert.Equal("relay", config.PushMode);
    }

    [Fact]
    public void AnOldConfigurationFile_WithoutTheNewFields_LoadsAndStaysDirectWithItsKey()
    {
        const string xml = "<?xml version=\"1.0\"?><PluginConfiguration><ApnsEnabled>true</ApnsEnabled><ApnsKeyId>ABC123</ApnsKeyId>"
            + "<ApnsTeamId>TEAM</ApnsTeamId><ApnsTopic>com.overglass.sillon</ApnsTopic><ApnsPrivateKeyValue>pem</ApnsPrivateKeyValue></PluginConfiguration>";

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(xml))!;

        Assert.Equal(string.Empty, config.PushMode);
        Assert.Equal("direct", config.ResolvePushMode(NoEnv));
        Assert.Equal(PluginConfiguration.DefaultPushRelayUrl, config.PushRelayUrl);
        Assert.Equal("ABC123", config.ApnsKeyId);
        Assert.Equal("pem", config.ApnsPrivateKeyValue);
    }

    [Fact]
    public void TheModeAndTheUrlSurviveTheXmlFile_AndBadValuesFallBack()
    {
        var config = new PluginConfiguration { PushMode = "off", PushRelayUrl = "https://my.relay/" };
        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
        var back = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(writer.ToString()))!;
        back.Sanitize();

        Assert.Equal("off", back.PushMode);
        Assert.Equal("https://my.relay", back.PushRelayUrl);

        back.PushMode = "bogus";
        back.PushRelayUrl = "ftp://nope";
        back.Sanitize();
        Assert.Equal(string.Empty, back.PushMode);
        Assert.Equal(PluginConfiguration.DefaultPushRelayUrl, back.PushRelayUrl);
    }

    [Fact]
    public void TheDirectKeyFieldsAndTheirMasking_AreUntouched()
    {
        var config = new PluginConfiguration { ApnsPrivateKeyValue = "secret", ApnsKeyId = "K", ApnsTeamId = "T", PushMode = "direct" };

        Assert.Equal("***", config.ApnsPrivateKey);
        Assert.Equal("com.overglass.sillon", config.ApnsTopic);
        var credentials = ApnsCredentialResolver.Resolve(config, NoEnv);
        Assert.Equal(new ApnsCredentials("secret", "K", "T", "com.overglass.sillon", ApnsKeySource.Config), credentials);
    }

    // the sender per device and per mode

    [Fact]
    public async Task DirectMode_SendsToTokenDevicesOnly_ExactlyAsBefore()
    {
        var rig = new Rig("direct");
        using var _ = rig;
        rig.Env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        rig.Env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");

        var done = await rig.Notifier.NotifyAsync(TestEnv.User, NotificationEvent.Available(Record(), "alb"), default);

        Assert.True(done);
        var call = Assert.Single(rig.Direct.Calls);
        Assert.Equal("aa01", call.Token);
        Assert.Equal("Sillon", call.Message.Title);
        Assert.Equal("XEU de Vald est maintenant dans la bibliothèque (3/17 titres)", call.Message.Body);
        Assert.Equal(7L, call.Message.Extra["request_id"]);
        Assert.Equal("alb", call.Message.Extra["album_id"]);
        Assert.Empty(rig.Relay.Calls);
    }

    [Fact]
    public async Task RelayMode_SendsStructuredDataToRelayDevicesOnly_NeverTheFrenchSentence()
    {
        var rig = new Rig("relay");
        using var _ = rig;
        rig.Env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        rig.Env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");
        rig.Env.Devices.UpsertRelay(RelayCrypto.B64(RelayCryptoTests.Hex(RelayCryptoTests.ServerPublic)), Other, "ios", "production");

        var done = await rig.Notifier.NotifyAsync(TestEnv.User, NotificationEvent.Available(Record(), "alb"), default);

        Assert.True(done);
        Assert.Empty(rig.Direct.Calls);
        var call = Assert.Single(rig.Relay.Calls);
        var envelope = RelayCrypto.FromB64(JsonDocument.Parse(call.Body).RootElement.GetProperty("envelope").GetString())!;
        using var device = RelayCryptoTests.AgreementKey(RelayCryptoTests.DevicePrivate, RelayCryptoTests.DevicePublic);
        var body = JsonDocument.Parse(call.Body).RootElement;
        var plain = RelayCryptoTests.Open(
            device,
            envelope,
            RelayCrypto.Aad(rig.Keys.Kid, RelayCryptoTests.Hex(RelayCryptoTests.DeviceIdHex), body.GetProperty("ts").GetInt64(), body.GetProperty("seq").GetInt64()))!;
        var json = Encoding.UTF8.GetString(plain, 2, (plain[0] << 8) | plain[1]);
        Assert.Equal("{\"v\":1,\"type\":\"request.available\",\"request_id\":\"7\",\"album_id\":\"alb\",\"title\":\"XEU\",\"artist\":\"Vald\",\"tracks_imported\":3,\"tracks_total\":17}", json);
    }

    [Fact]
    public async Task OffMode_SendsNothing_AndReportsDisabled()
    {
        var rig = new Rig("off");
        using var _ = rig;
        rig.Env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        rig.Env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");

        Assert.True(await rig.Notifier.NotifyAsync(TestEnv.User, NotificationEvent.Test, default));

        Assert.False(rig.Notifier.Enabled);
        Assert.Empty(rig.Direct.Calls);
        Assert.Empty(rig.Relay.Calls);
    }

    [Fact]
    public async Task RelayMode_IsEnabledWithoutAnyApnsKey_AndDirectModeStillNeedsOne()
    {
        var relay = new Rig("relay", withKey: false);
        var direct = new Rig("direct", withKey: false);
        using var _ = relay;
        using var __ = direct;

        Assert.True(relay.Notifier.Enabled);
        Assert.False(direct.Notifier.Enabled);
        Assert.Empty(await direct.Notifier.SendAsync(TestEnv.User, new PushMessage("Sillon", "x", new Dictionary<string, object>()), default));
    }

    [Fact]
    public async Task ARelayDeviceTheRelayReportsGone_IsRemoved_ButOnlyThatOne()
    {
        var rig = new Rig("relay");
        using var _ = rig;
        rig.Env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");
        rig.Env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        rig.Relay.Enqueue(410, "{\"error\":\"device_gone\"}");

        var done = await rig.Notifier.NotifyAsync(TestEnv.User, NotificationEvent.Test, default);

        Assert.True(done);
        Assert.Equal(new[] { "aa01" }, rig.Env.Devices.For(TestEnv.User).Select(d => d.Token).ToArray());
    }

    [Fact]
    public async Task ATransientRelayFailure_AsksForARetry_ADroppedOneDoesNot()
    {
        var rig = new Rig("relay");
        using var _ = rig;
        rig.Env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");
        rig.Relay.Throw = new HttpRequestException("down");
        Assert.False(await rig.Notifier.NotifyAsync(TestEnv.User, NotificationEvent.Test, default));

        rig.Relay.Throw = null;
        rig.Relay.Enqueue(429, "{\"error\":\"quota_exceeded\",\"scope\":\"device\"}");
        Assert.True(await rig.Notifier.NotifyAsync(TestEnv.User, NotificationEvent.Test, default));
    }

    // the registration endpoint

    [Fact]
    public async Task RelayRegistration_StoresTheKeyNotAToken_AndAnswersWithTheServerIdentity()
    {
        using var env = new TestEnv();
        using var keys = new ServerKeyStore(env.DataDir);
        env.Config.PushMode = "relay";

        var json = Json(await Controller(env, keys, TestEnv.User, Body(DeviceKey)).PutDevice(default), out var status);

        Assert.Equal(200, status);
        Assert.True(json.GetProperty("ok").GetBoolean());
        var push = json.GetProperty("push");
        Assert.Equal(1, push.GetProperty("protocol").GetInt32());
        Assert.Equal("relay", push.GetProperty("mode").GetString());
        Assert.Equal(keys.PublicKeyText, push.GetProperty("serverPublicKey").GetString());
        Assert.Equal(keys.KidText, push.GetProperty("kid").GetString());
        Assert.Equal(RelayCrypto.B64(SHA256Of(keys.PublicKey)), push.GetProperty("kid").GetString());
        Assert.Equal(PluginConfiguration.DefaultPushRelayUrl, push.GetProperty("relayUrl").GetString());
        var device = Assert.Single(env.Devices.For(TestEnv.User));
        Assert.Equal(string.Empty, device.Token);
        Assert.True(device.IsRelay);
        Assert.Equal("K60P1hDZnq5EPpMqJhQryh5fqZW0UYRSgn547x8xf_A", device.DeviceId);
        Assert.Equal("production", device.Env);
        Assert.DoesNotContain("PRIVATE", json.GetRawText());
    }

    [Fact]
    public async Task RelayRegistration_Refuses_ATokenABadKeyAnOtherOwnersKeyAndAServerNotInRelayMode()
    {
        using var env = new TestEnv();
        using var keys = new ServerKeyStore(env.DataDir);
        env.Config.PushMode = "relay";
        var compressed = RelayCrypto.B64(new byte[33].Select((_, i) => i == 0 ? (byte)2 : (byte)0).ToArray());

        Json(await Controller(env, keys, TestEnv.User, $"{{\"token\":\"aa01\",\"devicePublicKey\":\"{DeviceKey}\",\"pushMode\":\"relay\"}}").PutDevice(default), out var withToken);
        Json(await Controller(env, keys, TestEnv.User, Body(compressed)).PutDevice(default), out var badKey);
        Json(await Controller(env, keys, TestEnv.User, "{\"pushMode\":\"relay\",\"platform\":\"ios\"}").PutDevice(default), out var noKey);
        Json(await Controller(env, keys, TestEnv.User, Body(DeviceKey)).PutDevice(default), out var first);
        Json(await Controller(env, keys, Other, Body(DeviceKey)).PutDevice(default), out var stolen);
        Json(await Controller(env, keys, TestEnv.User, "{\"pushMode\":\"carrier-pigeon\"}").PutDevice(default), out var unknownMode);
        env.Config.PushMode = "direct";
        Json(await Controller(env, keys, TestEnv.User, Body(DeviceKey)).PutDevice(default), out var notRelay);

        Assert.Equal(400, withToken);
        Assert.Equal(400, badKey);
        Assert.Equal(400, noKey);
        Assert.Equal(200, first);
        Assert.Equal(409, stolen);
        Assert.Equal(400, unknownMode);
        Assert.Equal(409, notRelay);
        Assert.Single(env.Devices.All());
        Assert.Equal(TestEnv.User, env.Devices.All()[0].UserId);
    }

    [Fact]
    public async Task TheTodaysAppBody_WithoutTheNewFields_StillWorksInDirectMode_WithTheSameAnswer()
    {
        using var env = new TestEnv();
        using var keys = new ServerKeyStore(env.DataDir);
        env.Config.PushMode = "direct";

        var json = Json(await Controller(env, keys, TestEnv.User, "{\"token\":\"abc123\",\"platform\":\"ios\",\"env\":\"development\"}").PutDevice(default), out var status);

        Assert.Equal(200, status);
        Assert.Equal("{\"ok\":true}", json.GetRawText());
        var device = Assert.Single(env.Devices.For(TestEnv.User));
        Assert.Equal("abc123", device.Token);
        Assert.False(device.IsRelay);
        Assert.Equal(string.Empty, device.DevicePublicKey);
    }

    [Fact]
    public async Task TheTodaysAppBody_IsStillAcceptedWhateverTheConfiguredMode()
    {
        using var env = new TestEnv();
        using var keys = new ServerKeyStore(env.DataDir);
        env.Config.PushMode = "relay";

        Json(await Controller(env, keys, TestEnv.User, "{\"token\":\"abc123\",\"platform\":\"ios\"}").PutDevice(default), out var status);

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task ExplicitDirectBody_GetsTheDirectPushBlock()
    {
        using var env = new TestEnv();
        using var keys = new ServerKeyStore(env.DataDir);

        var json = Json(await Controller(env, keys, TestEnv.User, "{\"token\":\"abc123\",\"platform\":\"ios\",\"pushMode\":\"direct\"}").PutDevice(default), out _);

        Assert.Equal("direct", json.GetProperty("push").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task ARelayDeviceIsDeletedByItsIdAndOnlyByItsOwner()
    {
        using var env = new TestEnv();
        using var keys = new ServerKeyStore(env.DataDir);
        env.Config.PushMode = "relay";
        await Controller(env, keys, TestEnv.User, Body(DeviceKey)).PutDevice(default);
        const string id = "K60P1hDZnq5EPpMqJhQryh5fqZW0UYRSgn547x8xf_A";

        Controller(env, keys, Other, string.Empty).DeleteDeviceById(id);
        Assert.Single(env.Devices.All());
        var json = Json(Controller(env, keys, TestEnv.User, string.Empty).DeleteDeviceById(id), out _);

        Assert.Equal("{\"ok\":true}", json.GetRawText());
        Assert.Empty(env.Devices.All());
    }

    [Fact]
    public async Task ARelayDevice_ShowsInTheAdminListWithoutAToken_AndCanBeRemovedThere()
    {
        using var env = new TestEnv();
        env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");
        env.Library.Users[TestEnv.User] = "JB";
        var notifier = new ApnsNotifier(env.Devices, new NoSender(), () => env.Config, NoEnv, NullLogger<ApnsNotifier>.Instance);
        var service = new AdminService(env.Store, env.Devices, env.Library, env.Lidarr, notifier, () => env.Config, "1.0");

        var device = Assert.Single(Assert.Single(service.DevicesByUser()).Devices);

        Assert.Equal("relay", device.Mode);
        Assert.Equal(16, device.Id.Length);
        Assert.Equal("K60P1h", device.TokenHint);
        Assert.True(env.Devices.DeleteByPublicId(device.Id));
        Assert.Empty(env.Devices.All());
        await Task.CompletedTask;
    }

    [Fact]
    public void AnOldDevicesFile_LoadsAsDirectTokenDevices()
    {
        using var env = new TestEnv();
        File.WriteAllText(
            Path.Combine(env.DataDir, "devices.json"),
            "{\"devices\":[{\"token\":\"abc123\",\"userId\":\"" + TestEnv.User + "\",\"platform\":\"ios\",\"env\":\"production\",\"createdAt\":\"2026-09-01T00:00:00Z\",\"updatedAt\":\"2026-09-01T00:00:00Z\"}]}");

        var store = new DeviceStore(env.DataDir);

        var device = Assert.Single(store.All());
        Assert.False(device.IsRelay);
        Assert.Equal("abc123", device.Token);
        Assert.Equal(string.Empty, device.PushMode);
    }

    [Fact]
    public void Config_AdvertisesTheProtocolTheModesAndTheMode()
    {
        using var env = new TestEnv();
        env.Config.PushMode = "relay";
        var controller = new SillonController(NullLogger<SillonController>.Instance, null!, () => env.Config);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(controller.GetConfig()).Value)).RootElement.GetProperty("push");

        Assert.Equal(1, json.GetProperty("protocol").GetInt32());
        Assert.Equal(new[] { "relay", "direct" }, json.GetProperty("modes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("relay", json.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task TheTestButton_WorksInEveryMode()
    {
        foreach (var mode in new[] { "relay", "direct" })
        {
            var rig = new Rig(mode);
            using var _ = rig;
            rig.Env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
            rig.Env.Devices.UpsertRelay(DeviceKey, TestEnv.User, "ios", "production");
            var push = new PushController(rig.Notifier)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", TestEnv.User) }, "Test"))
                    }
                }
            };

            var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(await push.Test(null, default)).Value)).RootElement;

            Assert.Equal(mode, json.GetProperty("mode").GetString());
            var devices = json.GetProperty("devices");
            Assert.Equal(1, devices.GetArrayLength());
            Assert.Equal("sent", devices[0].GetProperty("result").GetString());
            Assert.Equal(mode == "relay" ? 1 : 0, rig.Relay.Calls.Count);
            Assert.Equal(mode == "direct" ? 1 : 0, rig.Direct.Calls.Count);
        }

        var off = new Rig("off");
        using var __ = off;
        var offPush = new PushController(off.Notifier)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", TestEnv.User) }, "Test")) }
            }
        };
        Assert.Equal(409, Assert.IsType<JsonResult>(await offPush.Test(null, default)).StatusCode);
    }

    [Fact]
    public async Task TheStatus_ShowsTheModeAndThePublicIdentityOnly()
    {
        var rig = new Rig("relay");
        using var _ = rig;
        var push = new PushController(rig.Notifier);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(push.Status()).Value)).RootElement;

        Assert.Equal("relay", json.GetProperty("mode").GetString());
        Assert.Equal(rig.Keys.KidText, json.GetProperty("relay").GetProperty("kid").GetString());
        Assert.Equal(rig.Keys.PublicKeyText, json.GetProperty("relay").GetProperty("serverPublicKey").GetString());
        Assert.DoesNotContain("PRIVATE", json.GetRawText());
        await Task.CompletedTask;
    }

    // the event as data

    [Fact]
    public void TheEventJson_MatchesTheVectorsLayout_AndStaysDataNotSentences()
    {
        var evt = new NotificationEvent(NotificationEvent.Types.Available, 1, "a-1", "Kind of Blue", "Miles Davis", 5, 5);

        var json = Encoding.UTF8.GetString(evt.ToProtocolJson());

        Assert.Equal(RelayCryptoTests.EventJson.Replace("\"r-1\"", "\"1\""), json);
        Assert.Equal("{\"v\":1,\"type\":\"test\"}", Encoding.UTF8.GetString(NotificationEvent.Test.ToProtocolJson()));
        Assert.Equal(
            "{\"v\":1,\"type\":\"request.not_found\",\"request_id\":\"9\",\"title\":\"XEU\",\"artist\":\"Vald\"}",
            Encoding.UTF8.GetString(new NotificationEvent(NotificationEvent.Types.NotFound, 9, "ignored", "XEU", "Vald", 5, 9).ToProtocolJson()));
    }

    [Fact]
    public void TheEventJson_KeepsUtf8AsIs_NormalisesToNfc_DropsControlCharacters_AndBoundsTheNames()
    {
        var decomposed = "Beyoncé";
        var evt = new NotificationEvent(NotificationEvent.Types.Available, 1, "alb", "Café\n\t" + decomposed, new string('é', 300), 0, 0);

        var json = evt.ToProtocolJson();
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.Contains("é", Encoding.UTF8.GetString(json));
        Assert.DoesNotContain("\\u00", Encoding.UTF8.GetString(json));
        Assert.Equal("Café" + "Beyoncé", doc.GetProperty("title").GetString());
        Assert.Equal(200, Encoding.UTF8.GetByteCount(doc.GetProperty("artist").GetString()!));
        Assert.False(doc.TryGetProperty("tracks_total", out _));
    }

    [Fact]
    public void TheEventJson_AlwaysFitsThe1022Bytes_EvenWithNamesFullOfEscapes()
    {
        var evt = new NotificationEvent(NotificationEvent.Types.Available, long.MaxValue, new string('a', 64), new string('"', 500), new string('\\', 500), 9999, 9999);

        var json = evt.ToProtocolJson();

        Assert.True(json.Length <= 1022);
        Assert.Equal(1024, RelayCrypto.PlaintextBlock(json).Length);
        JsonDocument.Parse(json);
    }

    [Fact]
    public void AnAlbumIdThatIsNotAnId_IsLeftOut_AndTrackCountsAreBounded()
    {
        var evt = new NotificationEvent(NotificationEvent.Types.Available, 1, "not an id!", "T", "A", 123456, 654321);

        var doc = JsonDocument.Parse(evt.ToProtocolJson()).RootElement;

        Assert.False(doc.TryGetProperty("album_id", out _));
        Assert.Equal(9999, doc.GetProperty("tracks_imported").GetInt32());
        Assert.Equal(9999, doc.GetProperty("tracks_total").GetInt32());
    }

    [Fact]
    public void TheDirectSentences_AreTheOnesTheAppAlwaysGot()
    {
        var partial = new NotificationEvent(NotificationEvent.Types.Available, 1, "alb", "XEU", "Vald", 16, 17);
        var full = new NotificationEvent(NotificationEvent.Types.Available, 1, "alb", "XEU", "Vald", 17, 17);
        var gone = new NotificationEvent(NotificationEvent.Types.NotFound, 1, string.Empty, "XEU", "Vald", 0, 0);

        Assert.Equal("XEU de Vald est maintenant dans la bibliothèque (16/17 titres)", partial.DirectBody());
        Assert.Equal("XEU de Vald est maintenant dans la bibliothèque", full.DirectBody());
        Assert.Equal("XEU de Vald est introuvable", gone.DirectBody());
        Assert.Equal("Sillon: notifications are working", NotificationEvent.Test.DirectBody());
        Assert.Equal(1L, gone.DirectExtra()["request_id"]);
        Assert.False(gone.DirectExtra().ContainsKey("album_id"));
        Assert.Empty(NotificationEvent.Test.DirectExtra());
    }

    private static byte[] SHA256Of(byte[] data) => System.Security.Cryptography.SHA256.HashData(data);

    private static string Body(string key) => $"{{\"platform\":\"ios\",\"env\":\"production\",\"devicePublicKey\":\"{key}\",\"pushMode\":\"relay\"}}";

    private static RequestRecord Record() => new() { Id = 7, UserId = TestEnv.User, Title = "XEU", Artist = "Vald", TracksImported = 3, TracksTotal = 17 };

    private static JsonElement Json(ActionResult result, out int status)
    {
        var content = Assert.IsType<ContentResult>(result);
        status = content.StatusCode ?? 200;
        return JsonDocument.Parse(content.Content!).RootElement;
    }

    private static RequestsController Controller(TestEnv env, ServerKeyStore keys, string userId, string body)
    {
        env.Library.Users[userId] = "name";
        var catalog = new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userId) }, "Test")) };
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return new RequestsController(env.Service, env.Store, env.Devices, catalog, env.Library, env.Notifier, () => env.Config, keys)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private sealed class NoSender : IPushSender
    {
        public Task<PushResult> SendAsync(ApnsCredentials? credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct)
            => Task.FromResult(PushResult.Sent);
    }

    private sealed class DirectSender : IPushSender
    {
        public List<(string Token, string Env, PushMessage Message)> Calls { get; } = new();

        public Task<PushResult> SendAsync(ApnsCredentials? credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct)
        {
            Calls.Add((deviceToken, environment, message));
            return Task.FromResult(PushResult.Sent);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(string mode, bool withKey = true)
        {
            Env = new TestEnv();
            Env.Config.PushMode = mode;
            Env.Config.PushRelayUrl = "https://relay.test";
            if (withKey)
            {
                Env.Config.ApnsEnabled = true;
                Env.Config.ApnsKeyId = "K";
                Env.Config.ApnsTeamId = "T";
                Env.Config.ApnsPrivateKeyValue = "pem";
            }

            Keys = new ServerKeyStore(Env.DataDir);
            Relay = new RelayClientTests.FakeRelay();
            var client = new RelayClient(new HttpClient(Relay), Keys, new RelayState(Env.DataDir), Env.Devices, () => Env.Config, NullLogger<RelayClient>.Instance, delay: (_, _) => Task.CompletedTask);
            Notifier = new ApnsNotifier(Env.Devices, Direct, () => Env.Config, NoEnv, NullLogger<ApnsNotifier>.Instance, client);
        }

        public TestEnv Env { get; }

        public ServerKeyStore Keys { get; }

        public RelayClientTests.FakeRelay Relay { get; }

        public DirectSender Direct { get; } = new();

        public ApnsNotifier Notifier { get; }

        public void Dispose()
        {
            Keys.Dispose();
            Env.Dispose();
        }
    }
}
