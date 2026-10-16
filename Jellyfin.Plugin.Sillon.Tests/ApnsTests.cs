using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class ApnsTests
{
    private const string Token = "aabbccddeeff00112233445566778899";

    private static readonly IReadOnlyDictionary<string, object> NoExtra = new Dictionary<string, object>();

    private static (ECDsa Key, string Pem) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, key.ExportPkcs8PrivateKeyPem());
    }

    private static ApnsCredentials Credentials(string pem, string keyId = "KEY1234567", string teamId = "TEAM123456", string topic = "com.overglass.sillon")
        => new(pem, keyId, teamId, topic, ApnsKeySource.Config);

    private static (ApnsClient Client, FakeApns Handler, Clock Clock) Client(string status = "200", string body = "")
    {
        var handler = new FakeApns { Status = int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), Body = body };
        var clock = new Clock();
        return (new ApnsClient(new HttpClient(handler), NullLogger<ApnsClient>.Instance, () => clock.Now), handler, clock);
    }

    private static JsonElement Decode(string part) => JsonDocument.Parse(Base64Url.DecodeFromChars(part)).RootElement;

    [Fact]
    public async Task Jwt_HasTheEs256StructureAndAVerifiableSignature()
    {
        var (key, pem) = NewKey();
        var (client, handler, clock) = Client();

        await client.SendAsync(Credentials(pem), Token, "production", new PushMessage(null, "x", NoExtra), default);

        var jwt = handler.Requests[0].Headers["authorization"].Split(' ')[1];
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Decode(parts[0]);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("KEY1234567", header.GetProperty("kid").GetString());
        var claims = Decode(parts[1]);
        Assert.Equal("TEAM123456", claims.GetProperty("iss").GetString());
        Assert.Equal(clock.Now.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        var signature = Base64Url.DecodeFromChars(parts[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Jwt_IsCachedFor50MinutesThenRefreshed()
    {
        var (_, pem) = NewKey();
        var (client, handler, clock) = Client();
        var credentials = Credentials(pem);

        await client.SendAsync(credentials, Token, "production", new PushMessage(null, "x", NoExtra), default);
        clock.Now += TimeSpan.FromMinutes(49);
        await client.SendAsync(credentials, Token, "production", new PushMessage(null, "x", NoExtra), default);
        clock.Now += TimeSpan.FromMinutes(2);
        await client.SendAsync(credentials, Token, "production", new PushMessage(null, "x", NoExtra), default);

        var tokens = handler.Requests.Select(r => r.Headers["authorization"]).ToList();
        Assert.Equal(tokens[0], tokens[1]);
        Assert.NotEqual(tokens[0], tokens[2]);
    }

    [Fact]
    public async Task Request_UsesHttp2AndTheRightHeaders()
    {
        var (_, pem) = NewKey();
        var (client, handler, _) = Client();

        var result = await client.SendAsync(
            Credentials(pem),
            Token,
            "production",
            new PushMessage("Sillon", "XEU est là", new Dictionary<string, object> { ["request_id"] = 7L, ["album_id"] = "abc" }),
            default);

        Assert.Equal(PushOutcome.Sent, result.Outcome);
        var call = handler.Requests[0];
        Assert.Equal(new Version(2, 0), call.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, call.Policy);
        Assert.Equal("https://api.push.apple.com/3/device/" + Token, call.Uri);
        Assert.StartsWith("bearer ", call.Headers["authorization"]);
        Assert.Equal("com.overglass.sillon", call.Headers["apns-topic"]);
        Assert.Equal("alert", call.Headers["apns-push-type"]);
        Assert.Equal("10", call.Headers["apns-priority"]);
        Assert.True(long.Parse(call.Headers["apns-expiration"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        using var body = JsonDocument.Parse(call.Body);
        var alert = body.RootElement.GetProperty("aps").GetProperty("alert");
        Assert.Equal("Sillon", alert.GetProperty("title").GetString());
        Assert.Equal("XEU est là", alert.GetProperty("body").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("request_id").GetInt64());
        Assert.Equal("abc", body.RootElement.GetProperty("album_id").GetString());
    }

    [Theory]
    [InlineData("development", "https://api.sandbox.push.apple.com/3/device/" + Token)]
    [InlineData("production", "https://api.push.apple.com/3/device/" + Token)]
    [InlineData("", "https://api.push.apple.com/3/device/" + Token)]
    public async Task Host_FollowsTheDeviceEnvironment(string env, string expected)
    {
        var (_, pem) = NewKey();
        var (client, handler, _) = Client();

        await client.SendAsync(Credentials(pem), Token, env, new PushMessage(null, "x", NoExtra), default);

        Assert.Equal(expected, handler.Requests[0].Uri);
    }

    [Theory]
    [InlineData("410", "{\"reason\":\"Unregistered\"}")]
    [InlineData("400", "{\"reason\":\"BadDeviceToken\"}")]
    [InlineData("410", "")]
    public async Task DeadTokens_AreReportedAsDeviceGone(string status, string body)
    {
        var (_, pem) = NewKey();
        var (client, _, _) = Client(status, body);

        var result = await client.SendAsync(Credentials(pem), Token, "production", new PushMessage(null, "x", NoExtra), default);

        Assert.Equal(PushOutcome.DeviceGone, result.Outcome);
    }

    [Theory]
    [InlineData("500", "{\"reason\":\"InternalServerError\"}", "InternalServerError")]
    [InlineData("403", "{\"reason\":\"InvalidProviderToken\"}", "InvalidProviderToken")]
    [InlineData("429", "not json", "")]
    public async Task OtherErrors_AreFailures(string status, string body, string reason)
    {
        var (_, pem) = NewKey();
        var (client, _, _) = Client(status, body);

        var result = await client.SendAsync(Credentials(pem), Token, "production", new PushMessage(null, "x", NoExtra), default);

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task UnreachableApns_IsAFailure()
    {
        var (_, pem) = NewKey();
        var handler = new FakeApns { Error = new HttpRequestException("boom") };
        var client = new ApnsClient(new HttpClient(handler), NullLogger<ApnsClient>.Instance);

        var result = await client.SendAsync(Credentials(pem), Token, "production", new PushMessage(null, "x", NoExtra), default);

        Assert.Equal(PushOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AKeyThatIsNotPem_IsAFailureWithoutARequest()
    {
        var (client, handler, _) = Client();

        var result = await client.SendAsync(Credentials("not a key"), Token, "production", new PushMessage(null, "x", NoExtra), default);

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AKeyWithLiteralBackslashN_IsAccepted()
    {
        var (_, pem) = NewKey();
        var (client, handler, _) = Client();

        var result = await client.SendAsync(Credentials(pem.TrimEnd().Replace("\n", "\\n", StringComparison.Ordinal)), Token, "production", new PushMessage(null, "x", NoExtra), default);

        Assert.Equal(PushOutcome.Sent, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void CreateHttpClient_BuildsAClient()
    {
        using var client = ApnsClient.CreateHttpClient();

        Assert.NotNull(client);
    }

    // notifier

    private static (ApnsNotifier Notifier, TestEnv Env, FakeSender Sender) Notifier(Dictionary<string, string>? vars = null, bool enabled = true)
    {
        var env = new TestEnv();
        env.Config.ApnsEnabled = enabled;
        env.Config.ApnsPrivateKeyValue = "config-key";
        env.Config.ApnsKeyId = "CFGKEY";
        var sender = new FakeSender();
        var notifier = new ApnsNotifier(env.Devices, sender, () => env.Config, name => vars is not null && vars.TryGetValue(name, out var v) ? v : null, NullLogger<ApnsNotifier>.Instance);
        return (notifier, env, sender);
    }

    [Fact]
    public async Task Notifier_SendsToEveryDeviceOfTheRequesterOnly()
    {
        var (notifier, env, sender) = Notifier();
        using var _ = env;
        env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        env.Devices.Upsert("aa02", TestEnv.User, "ios", "development");
        env.Devices.Upsert("bb01", "someone-else", "ios", "production");

        var done = await notifier.NotifyAsync(TestEnv.User, "XEU est là", new Dictionary<string, object> { ["album_id"] = "abc" }, default);

        Assert.True(done);
        Assert.Equal(new[] { ("aa01", "production"), ("aa02", "development") }, sender.Calls.Select(c => (c.Token, c.Env)).ToArray());
        Assert.All(sender.Calls, c =>
        {
            Assert.Equal("Sillon", c.Message.Title);
            Assert.Equal("XEU est là", c.Message.Body);
            Assert.Equal("abc", c.Message.Extra["album_id"]);
        });
    }

    [Fact]
    public async Task Notifier_RemovesTheDevicesThatAreGone()
    {
        var (notifier, env, sender) = Notifier();
        using var _ = env;
        env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        env.Devices.Upsert("aa02", TestEnv.User, "ios", "production");
        sender.Outcomes["aa01"] = PushResult.DeviceGone(410, "Unregistered");

        var done = await notifier.NotifyAsync(TestEnv.User, "x", NoExtra, default);

        Assert.True(done);
        Assert.Equal(new[] { "aa02" }, env.Devices.For(TestEnv.User).Select(d => d.Token).ToArray());
    }

    [Fact]
    public async Task Notifier_AsksForARetryOnlyWhenNobodyGotIt()
    {
        var (notifier, env, sender) = Notifier();
        using var _ = env;
        env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        env.Devices.Upsert("aa02", TestEnv.User, "ios", "production");
        sender.Outcomes["aa01"] = PushResult.Failed(500, "InternalServerError");

        Assert.True(await notifier.NotifyAsync(TestEnv.User, "x", NoExtra, default));

        sender.Outcomes["aa02"] = PushResult.Failed(500, "InternalServerError");

        Assert.False(await notifier.NotifyAsync(TestEnv.User, "x", NoExtra, default));
        Assert.Equal(2, env.Devices.For(TestEnv.User).Count);
    }

    [Fact]
    public async Task Notifier_SendsNothingWhenDisabledOrKeyless()
    {
        var (disabled, env1, sender1) = Notifier(enabled: false);
        using var _1 = env1;
        env1.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        var (keyless, env2, sender2) = Notifier();
        using var _2 = env2;
        env2.Config.ApnsPrivateKeyValue = string.Empty;
        env2.Devices.Upsert("aa01", TestEnv.User, "ios", "production");

        Assert.True(await disabled.NotifyAsync(TestEnv.User, "x", NoExtra, default));
        Assert.True(await keyless.NotifyAsync(TestEnv.User, "x", NoExtra, default));

        Assert.Empty(sender1.Calls);
        Assert.Empty(sender2.Calls);
        Assert.False(disabled.Enabled);
        Assert.False(keyless.Enabled);
    }

    [Fact]
    public void Notifier_IsEnabledWhenOnAndKeyed_AndFollowsConfigChanges()
    {
        var (notifier, env, _) = Notifier();
        using var _ = env;

        Assert.True(notifier.Enabled);
        Assert.Equal(ApnsKeySource.Config, notifier.KeySource());

        env.Config.ApnsEnabled = false;

        Assert.False(notifier.Enabled);
        Assert.Equal(ApnsKeySource.Config, notifier.KeySource());
    }

    // credentials

    [Fact]
    public void Credentials_PreferTheEnvironmentOverTheConfiguration()
    {
        var config = new PluginConfiguration { ApnsPrivateKeyValue = "cfg-key", ApnsKeyId = "CFGKEY", ApnsTeamId = "CFGTEAM", ApnsTopic = "cfg.topic" };
        var vars = new Dictionary<string, string>
        {
            ["SILLON_APNS_KEY"] = " env-key ",
            ["SILLON_APNS_KEY_ID"] = "ENVKEY",
            ["SILLON_APNS_TEAM_ID"] = "ENVTEAM",
            ["SILLON_APNS_TOPIC"] = "env.topic"
        };

        var credentials = ApnsCredentialResolver.Resolve(config, n => vars.GetValueOrDefault(n));

        Assert.Equal(new ApnsCredentials("env-key", "ENVKEY", "ENVTEAM", "env.topic", ApnsKeySource.Environment), credentials);
    }

    [Fact]
    public void Credentials_FallBackToTheConfigurationThenTheDefaults()
    {
        var config = new PluginConfiguration { ApnsPrivateKeyValue = "cfg-key", ApnsKeyId = "CFGKEY", ApnsTeamId = " ", ApnsTopic = string.Empty };

        var credentials = ApnsCredentialResolver.Resolve(config, _ => null);

        Assert.Equal(new ApnsCredentials("cfg-key", "CFGKEY", "XXXXXXXXXX", "com.overglass.sillon", ApnsKeySource.Config), credentials);
    }

    [Fact]
    public void Credentials_MixTheEnvironmentKeyWithConfigurationFields()
    {
        var config = new PluginConfiguration { ApnsKeyId = "CFGKEY" };

        var credentials = ApnsCredentialResolver.Resolve(config, n => n == "SILLON_APNS_KEY" ? "env-key" : null);

        Assert.Equal(ApnsKeySource.Environment, credentials?.Source);
        Assert.Equal("CFGKEY", credentials?.KeyId);
    }

    [Theory]
    [InlineData("", "KEY")]
    [InlineData("key", "")]
    public void Credentials_AreMissingWithoutAKeyOrItsId(string key, string keyId)
    {
        var config = new PluginConfiguration { ApnsPrivateKeyValue = key, ApnsKeyId = keyId };

        Assert.Null(ApnsCredentialResolver.Resolve(config, _ => null));
    }

    // key masking

    [Fact]
    public void ConfigurationJson_NeverCarriesTheKey()
    {
        var config = new PluginConfiguration { ApnsPrivateKey = "-----BEGIN PRIVATE KEY-----secret" };

        var json = JsonSerializer.Serialize(config);

        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Contains("\"ApnsPrivateKey\":\"***\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApnsPrivateKeyValue", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationJson_ShowsNothingWhenNoKeyIsStored()
    {
        var json = JsonSerializer.Serialize(new PluginConfiguration());

        Assert.Contains("\"ApnsPrivateKey\":\"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingTheMask_KeepsTheStoredKey()
    {
        var stored = new PluginConfiguration { ApnsPrivateKey = "the-key" };
        var posted = JsonSerializer.Deserialize<PluginConfiguration>("{\"ApnsPrivateKey\":\"***\",\"ApnsKeyId\":\"NEWID\"}")!;

        posted.CarryOverSecrets(stored);

        Assert.Equal("the-key", posted.ApnsPrivateKeyValue);
        Assert.Equal("NEWID", posted.ApnsKeyId);
        Assert.Equal("***", posted.ApnsPrivateKey);
    }

    [Fact]
    public void SavingAnotherValue_ReplacesTheKey_AndEmptyClearsIt()
    {
        var stored = new PluginConfiguration { ApnsPrivateKey = "old-key" };
        var replaced = JsonSerializer.Deserialize<PluginConfiguration>("{\"ApnsPrivateKey\":\"new-key\"}")!;
        var cleared = JsonSerializer.Deserialize<PluginConfiguration>("{\"ApnsPrivateKey\":\"\"}")!;

        replaced.CarryOverSecrets(stored);
        cleared.CarryOverSecrets(stored);

        Assert.Equal("new-key", replaced.ApnsPrivateKeyValue);
        Assert.Equal(string.Empty, cleared.ApnsPrivateKeyValue);
    }

    [Fact]
    public void ThePlugin_FileKeepsTheRealKey_ButNotTheMask()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { ApnsPrivateKey = "the-key" });
        var xml = writer.ToString();

        Assert.Contains("<ApnsPrivateKeyValue>the-key</ApnsPrivateKeyValue>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ApnsPrivateKey>", xml, StringComparison.Ordinal);
        var back = (PluginConfiguration)serializer.Deserialize(new StringReader(xml))!;
        Assert.Equal("the-key", back.ApnsPrivateKeyValue);
    }

    // endpoints

    [Fact]
    public void Me_ReportsPushEnabledOnlyWhenActive()
    {
        var (notifier, env, _) = Notifier();
        using var _ = env;
        var controller = Requests(env, notifier);

        Assert.True(MeJson(controller.Me()).GetProperty("pushEnabled").GetBoolean());

        env.Config.ApnsEnabled = false;

        Assert.False(MeJson(controller.Me()).GetProperty("pushEnabled").GetBoolean());
    }

    [Fact]
    public async Task TestEndpoint_PushesToTheCallersDevicesAndReportsEach()
    {
        var (notifier, env, sender) = Notifier();
        using var _ = env;
        env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");
        env.Devices.Upsert("aa02", TestEnv.User, "ios", "development");
        env.Devices.Upsert("bb01", "other", "ios", "production");
        sender.Outcomes["aa02"] = PushResult.DeviceGone(410, "Unregistered");
        var controller = Push(notifier, TestEnv.User);

        var result = await controller.Test(default);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value)).RootElement;
        var devices = json.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(new[] { "sent", "devicegone" }, devices.Select(d => d.GetProperty("result").GetString()).ToArray());
        Assert.All(sender.Calls, c => Assert.Equal("Sillon : notifications actives", c.Message.Body));
        Assert.Null(sender.Calls[0].Message.Title);
        Assert.Equal(new[] { "aa01" }, env.Devices.For(TestEnv.User).Select(d => d.Token).ToArray());
    }

    [Fact]
    public async Task TestEndpoint_RefusesWhenNotificationsAreOff()
    {
        var (notifier, env, sender) = Notifier(enabled: false);
        using var _ = env;
        env.Devices.Upsert("aa01", TestEnv.User, "ios", "production");

        var result = await Push(notifier, TestEnv.User).Test(default);

        Assert.Equal(409, Assert.IsType<JsonResult>(result).StatusCode);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public void StatusEndpoint_TellsTheKeySource()
    {
        var (notifier, env, _) = Notifier(new Dictionary<string, string> { ["SILLON_APNS_KEY"] = "k" });
        using var _ = env;

        var result = Push(notifier, TestEnv.User).Status();

        var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value)).RootElement;
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("environment", json.GetProperty("source").GetString());
    }

    private static PushController Push(ApnsNotifier notifier, string userId)
    {
        var claims = new[] { new Claim("Jellyfin-UserId", userId) };
        return new PushController(notifier)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static RequestsController Requests(TestEnv env, INotifier notifier)
    {
        env.Library.Users[TestEnv.User] = "JB";
        var catalog = new Jellyfin.Plugin.Sillon.Catalog.CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<Jellyfin.Plugin.Sillon.Catalog.CatalogService>.Instance);
        var claims = new[] { new Claim("Jellyfin-UserId", TestEnv.User) };
        return new RequestsController(env.Service, env.Store, env.Devices, catalog, env.Library, notifier, () => env.Config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static JsonElement MeJson(ActionResult result)
        => JsonDocument.Parse(Assert.IsType<ContentResult>(result).Content!).RootElement;

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed record Call(string Uri, Version Version, HttpVersionPolicy Policy, Dictionary<string, string> Headers, string Body);

    private sealed class FakeApns : HttpMessageHandler
    {
        public int Status { get; set; } = 200;

        public string Body { get; set; } = string.Empty;

        public Exception? Error { get; set; }

        public List<Call> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                throw Error;
            }

            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            Requests.Add(new Call(request.RequestUri!.ToString(), request.Version, request.VersionPolicy, headers, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Body) };
        }
    }

    private sealed class FakeSender : IPushSender
    {
        public List<(string Token, string Env, PushMessage Message)> Calls { get; } = new();

        public Dictionary<string, PushResult> Outcomes { get; } = new();

        public Task<PushResult> SendAsync(ApnsCredentials credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct)
        {
            Calls.Add((deviceToken, environment, message));
            return Task.FromResult(Outcomes.GetValueOrDefault(deviceToken) ?? PushResult.Sent);
        }
    }
}
