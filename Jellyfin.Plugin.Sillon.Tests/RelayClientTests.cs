using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class RelayClientTests : IDisposable
{
    private const string User = "0000000000000000000000000000aaaa";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sillon-relay-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();
    private readonly PluginConfiguration _config = new() { PushMode = "relay" };
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1790000000123);
    private int _delays;

    public RelayClientTests()
    {
        Directory.CreateDirectory(_dir);
        Devices = new DeviceStore(_dir);
        Keys = new ServerKeyStore(_dir);
        State = new RelayState(_dir);
        Devices.UpsertRelay(RelayCrypto.B64(RelayCryptoTests.Hex(RelayCryptoTests.DevicePublic)), User, "ios", "production");
        Client = NewClient();
    }

    private DeviceStore Devices { get; }

    private ServerKeyStore Keys { get; }

    private RelayState State { get; }

    private RelayClient Client { get; set; }

    private string DeviceId => RelayCrypto.B64(RelayCryptoTests.Hex(RelayCryptoTests.DeviceIdHex));

    public void Dispose()
    {
        Keys.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Send_PostsASignedEncryptedEventTheDeviceCanOpen()
    {
        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Sent, result.Outcome);
        var call = Assert.Single(_relay.Calls);
        Assert.Equal("https://relay.test/v1/send", call.Uri);
        using var doc = JsonDocument.Parse(call.Body);
        var root = doc.RootElement;
        Assert.Equal(new[] { "v", "device_id", "server_pub", "ts", "seq", "envelope", "sig" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(DeviceId, root.GetProperty("device_id").GetString());
        Assert.Equal(Keys.PublicKeyText, root.GetProperty("server_pub").GetString());
        Assert.Equal(1790000000, root.GetProperty("ts").GetInt64());
        Assert.Equal(1790000000123, root.GetProperty("seq").GetInt64());
        var envelope = RelayCrypto.FromB64(root.GetProperty("envelope").GetString())!;
        var signature = RelayCrypto.FromB64(root.GetProperty("sig").GetString())!;
        Assert.Equal(1118, envelope.Length);
        Assert.Equal(1, envelope[0]);

        // the relay can verify it with the key the request carries
        using var verifier = RelayCrypto.ImportVerifyKey(RelayCrypto.FromB64(root.GetProperty("server_pub").GetString())!);
        Assert.True(RelayCrypto.Verify(verifier, RelayCrypto.SendMessage(RelayCryptoTests.Hex(RelayCryptoTests.DeviceIdHex), 1790000000, 1790000000123, envelope), signature));

        // the device can open it: the event is data, not a sentence, padded to the fixed size
        using var device = RelayCryptoTests.AgreementKey(RelayCryptoTests.DevicePrivate, RelayCryptoTests.DevicePublic);
        var aad = RelayCrypto.Aad(Keys.Kid, RelayCryptoTests.Hex(RelayCryptoTests.DeviceIdHex), 1790000000, 1790000000123);
        var plain = RelayCryptoTests.Open(device, envelope, aad);
        Assert.NotNull(plain);
        var length = (plain![0] << 8) | plain[1];
        Assert.Equal(Encoding.UTF8.GetString(Event().ToProtocolJson()), Encoding.UTF8.GetString(plain, 2, length));
        Assert.All(plain[(2 + length)..], b => Assert.Equal(0, b));
        Assert.DoesNotContain("bibliothèque", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public async Task EveryEnvelopeHasAFreshEphemeralKeyAndNonce()
    {
        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        await Client.SendAsync(null, DeviceId, "production", Message(), default);

        var envelopes = _relay.Calls.Select(c => RelayCrypto.FromB64(JsonDocument.Parse(c.Body).RootElement.GetProperty("envelope").GetString())!).ToList();
        Assert.NotEqual(envelopes[0][1..66], envelopes[1][1..66]);
        Assert.NotEqual(envelopes[0][66..78], envelopes[1][66..78]);
    }

    [Fact]
    public async Task Sequence_IncreasesForEverySend_EvenWhenTheClockStandsStillOrGoesBack()
    {
        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        _now = _now.AddMinutes(-10);
        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        _now = _now.AddHours(1);
        await Client.SendAsync(null, DeviceId, "production", Message(), default);

        var seqs = _relay.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(1790000000123, seqs[0]);
        Assert.Equal(1790000000124, seqs[1]);
        Assert.Equal(1790000000125, seqs[2]);
        Assert.Equal(_now.ToUnixTimeMilliseconds(), seqs[3]);
        Assert.Equal(seqs.OrderBy(s => s), seqs);
    }

    [Fact]
    public async Task Sequence_SurvivesARestart()
    {
        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        await Client.SendAsync(null, DeviceId, "production", Message(), default);

        var reloaded = new RelayState(_dir);

        Assert.Equal(1790000000125, reloaded.NextSeq(DeviceId, 5));
    }

    [Fact]
    public void Sequence_IsKeptPerDevice()
    {
        Assert.Equal(1000, State.NextSeq("a", 1000));
        Assert.Equal(1001, State.NextSeq("a", 10));
        Assert.Equal(10, State.NextSeq("b", 10));
    }

    [Fact]
    public async Task A200_CountsAsSent_AndClearsAnUnknownDeviceMark()
    {
        State.Update(DeviceId, d => d.UnknownSince = _now.AddDays(-5));

        await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Null(State.Get(DeviceId).UnknownSince);
        var status = Client.Status();
        Assert.Equal(1, status.Sent);
        Assert.Equal("sent", status.LastStatus);
        Assert.Equal(Keys.KidText, status.Kid);
        Assert.False(status.Banned);
    }

    [Fact]
    public async Task A429OnTheDevice_DropsTheNotification_WithoutRetry_AndSpareTheRelayUntilTheReset()
    {
        _relay.Enqueue(429, "{\"error\":\"quota_exceeded\",\"scope\":\"device\",\"reset_at\":\"2026-10-22T00:00:00Z\"}");
        _now = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

        var first = await Client.SendAsync(null, DeviceId, "production", Message(), default);
        var second = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Skipped, first.Outcome);
        Assert.Equal(429, first.StatusCode);
        Assert.Equal(PushOutcome.Skipped, second.Outcome);
        Assert.Single(_relay.Calls);
        Assert.Equal(0, _delays);
        Assert.Equal(1, Client.Status().QuotaHits);

        // after the reset the device is tried again
        _now = new DateTimeOffset(2026, 10, 22, 0, 0, 1, TimeSpan.Zero);
        Assert.Equal(PushOutcome.Sent, (await Client.SendAsync(null, DeviceId, "production", Message(), default)).Outcome);
        Assert.Equal(2, _relay.Calls.Count);
    }

    [Fact]
    public async Task A429OnTheServer_BlocksEveryDeviceUntilTheRetryAfter()
    {
        var otherKey = RelayCryptoTests.Hex(ServerOther());
        var other = RelayCrypto.B64(RelayCrypto.DeviceId(otherKey));
        Devices.UpsertRelay(RelayCrypto.B64(otherKey), User, "ios", "production");
        _relay.Enqueue(429, "{\"error\":\"quota_exceeded\",\"scope\":\"server\"}", retryAfterSeconds: 3600);

        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        var blocked = await Client.SendAsync(null, other, "production", Message(), default);

        Assert.Equal(PushOutcome.Skipped, blocked.Outcome);
        Assert.Single(_relay.Calls);
        Assert.NotNull(Client.Status().BlockedUntil);

        _now = _now.AddSeconds(3601);
        Assert.Equal(PushOutcome.Sent, (await Client.SendAsync(null, other, "production", Message(), default)).Outcome);
    }

    [Fact]
    public async Task ARateLimit429_PausesBriefly_NotUntilMidnight()
    {
        _relay.Enqueue(429, "{\"error\":\"rate_limited\"}");

        await Client.SendAsync(null, DeviceId, "production", Message(), default);
        Assert.Equal(PushOutcome.Skipped, (await Client.SendAsync(null, DeviceId, "production", Message(), default)).Outcome);
        _now = _now.AddSeconds(61);

        Assert.Equal(PushOutcome.Sent, (await Client.SendAsync(null, DeviceId, "production", Message(), default)).Outcome);
    }

    [Fact]
    public async Task A410_MeansTheDeviceIsGone()
    {
        _relay.Enqueue(410, "{\"error\":\"device_gone\"}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.DeviceGone, result.Outcome);
        Assert.Equal(410, result.StatusCode);
    }

    [Fact]
    public async Task A404_KeepsTheDeviceFor30Days_ThenDropsIt_AndNever_RetriesImmediately()
    {
        _relay.Enqueue(404, "{\"error\":\"unknown_device\"}");
        _relay.Enqueue(404, "{\"error\":\"unknown_device\"}");
        _relay.Enqueue(404, "{\"error\":\"unknown_device\"}");

        var first = await Client.SendAsync(null, DeviceId, "production", Message(), default);
        _now = _now.AddDays(29);
        var second = await Client.SendAsync(null, DeviceId, "production", Message(), default);
        _now = _now.AddDays(2);
        var third = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Skipped, first.Outcome);
        Assert.Equal(PushOutcome.Skipped, second.Outcome);
        Assert.Equal(PushOutcome.DeviceGone, third.Outcome);
        Assert.Equal(3, _relay.Calls.Count);
    }

    [Fact]
    public async Task A409_IsRetriedOnceWithAHigherSequence()
    {
        _relay.Enqueue(409, "{\"error\":\"replay\"}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Sent, result.Outcome);
        var seqs = _relay.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(2, seqs.Count);
        Assert.True(seqs[1] > seqs[0]);
        Assert.Equal(0, _delays);
    }

    [Fact]
    public async Task ASecond409_IsAFailure_NotALoop()
    {
        _relay.Enqueue(409, "{\"error\":\"replay\"}");
        _relay.Enqueue(409, "{\"error\":\"replay\"}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Equal(2, _relay.Calls.Count);
    }

    [Fact]
    public async Task A502_IsRetriedOnceAfterADelay_ThenDropped()
    {
        _relay.Enqueue(502, "{\"error\":\"apns_error\"}");
        _relay.Enqueue(502, "{\"error\":\"apns_error\"}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Skipped, result.Outcome);
        Assert.Equal(2, _relay.Calls.Count);
        Assert.Equal(1, _delays);
    }

    [Fact]
    public async Task A503ThenA200_Succeeds_AfterOneDelay()
    {
        _relay.Enqueue(503, "{\"error\":\"env_unavailable\"}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Sent, result.Outcome);
        Assert.Equal(2, _relay.Calls.Count);
        Assert.Equal(1, _delays);
    }

    [Fact]
    public async Task A503Twice_IsAFailureWorthRetryingLater_WithBoundedAttempts()
    {
        _relay.Enqueue(503, "{}");
        _relay.Enqueue(503, "{}");
        _relay.Enqueue(503, "{}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Equal(2, _relay.Calls.Count);
    }

    [Fact]
    public async Task AnUnreachableRelay_IsATransientFailure()
    {
        _relay.Throw = new HttpRequestException("down");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Equal("unreachable", Client.Status().LastStatus);
    }

    [Fact]
    public async Task ABan_StopsRelayModeForThatKey_WithoutAnyMoreRequest()
    {
        _relay.Enqueue(403, "{\"error\":\"server_banned\"}");

        var first = await Client.SendAsync(null, DeviceId, "production", Message(), default);
        var second = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(PushOutcome.Skipped, first.Outcome);
        Assert.Equal(PushOutcome.Skipped, second.Outcome);
        Assert.Single(_relay.Calls);
        Assert.True(Client.Status().Banned);

        // rotating the key (deleting the file) is a new identity: not banned
        Keys.Dispose();
        File.Delete(Path.Combine(_dir, ServerKeyStore.FileName));
        var fresh = new ServerKeyStore(_dir);
        var rotated = NewClient(fresh);
        Assert.False(rotated.Status().Banned);
        Assert.Equal(PushOutcome.Sent, (await rotated.SendAsync(null, DeviceId, "production", Message(), default)).Outcome);
    }

    [Theory]
    [InlineData(400, "stale_timestamp", PushOutcome.Failed)]
    [InlineData(400, "bad_request", PushOutcome.Skipped)]
    [InlineData(401, "bad_signature", PushOutcome.Skipped)]
    [InlineData(413, "too_large", PushOutcome.Skipped)]
    public async Task OtherRefusals_AreClassified(int status, string error, PushOutcome expected)
    {
        _relay.Enqueue(status, "{\"error\":\"" + error + "\"}");

        var result = await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Single(_relay.Calls);
        Assert.Equal(error, Client.Status().LastStatus);
    }

    [Fact]
    public async Task ADeviceTheStoreDoesNotKnow_SendsNothing()
    {
        var result = await Client.SendAsync(null, RelayCrypto.B64(new byte[32]), "production", Message(), default);

        Assert.Equal(PushOutcome.Skipped, result.Outcome);
        Assert.Empty(_relay.Calls);
    }

    [Fact]
    public async Task AMessageWithoutTheEventAsData_IsNeverSent()
    {
        var result = await Client.SendAsync(null, DeviceId, "production", new PushMessage("Sillon", "a French sentence", new Dictionary<string, object>()), default);

        Assert.Equal(PushOutcome.Skipped, result.Outcome);
        Assert.Empty(_relay.Calls);
    }

    [Fact]
    public async Task TheRelayUrlIsConfigurable()
    {
        _config.PushRelayUrl = "https://other.example/base/";

        await Client.SendAsync(null, DeviceId, "production", Message(), default);

        Assert.Equal("https://other.example/base/v1/send", _relay.Calls.Single().Uri);
    }

    [Fact]
    public void TheServerKey_IsGeneratedOnce_KeptInAFileOutsideTheConfiguration_AndOnlyItsPublicPartIsExposed()
    {
        var path = Path.Combine(_dir, ServerKeyStore.FileName);
        var first = Keys.PublicKeyText;

        Assert.True(File.Exists(path));
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(path));
        Assert.Equal(first, new ServerKeyStore(_dir).PublicKeyText);
        Assert.Equal(87, first.Length);
        Assert.Equal(RelayCrypto.B64(SHA256.HashData(RelayCrypto.FromB64(first)!)), Keys.KidText);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        // nothing of the private key reaches the XML configuration or the status
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        xml.Serialize(writer, _config);
        Assert.DoesNotContain("PRIVATE", writer.ToString());
        Assert.DoesNotContain(Keys.KidText, writer.ToString());
        Assert.DoesNotContain("BEGIN", JsonSerializer.Serialize(Client.Status()));
    }

    [Fact]
    public void RotatingTheKey_IsDeletingTheFile()
    {
        var before = Keys.KidText;
        var path = Path.Combine(_dir, ServerKeyStore.FileName);
        Keys.Dispose();
        File.Delete(path);

        Assert.NotEqual(before, new ServerKeyStore(_dir).KidText);
    }

    private static string ServerOther()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToHexString(RelayCrypto.ExportPublic(key));
    }

    private static NotificationEvent Event() => new(NotificationEvent.Types.Available, 42, "0a1b2c3d4e5f60718293a4b5c6d7e8f9", "Kind of Blue", "Miles Davis", 5, 5);

    private static PushMessage Message()
    {
        var e = Event();
        return new PushMessage("Sillon", e.DirectBody(), e.DirectExtra(), e);
    }

    private RelayClient NewClient(ServerKeyStore? keys = null)
    {
        _config.PushRelayUrl = "https://relay.test";
        return new RelayClient(
            new HttpClient(_relay),
            keys ?? Keys,
            State,
            Devices,
            () => _config,
            NullLogger<RelayClient>.Instance,
            now: () => _now,
            delay: (_, _) =>
            {
                _delays++;
                return Task.CompletedTask;
            });
    }

    internal sealed record RelayCall(string Uri, string Body);

    internal sealed class FakeRelay : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body, int? RetryAfter)> _answers = new();

        public List<RelayCall> Calls { get; } = new();

        public Exception? Throw { get; set; }

        public void Enqueue(int status, string body, int? retryAfterSeconds = null) => _answers.Enqueue((status, body, retryAfterSeconds));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add(new RelayCall(request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            var (status, body, retryAfter) = _answers.Count > 0 ? _answers.Dequeue() : (200, "{\"ok\":true}", null);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
