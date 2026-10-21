using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>
/// Sends a notification through the sillon-push relay: builds the event, pads, encrypts it for the device, signs it with the server key
/// and posts it to <c>/v1/send</c>. It implements the answer table of section 6.5 of the protocol. In place of the APNs token the
/// notifier hands it the device id; it holds no APNs credentials.
/// </summary>
public sealed class RelayClient : IPushSender
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnknownDeviceGrace = TimeSpan.FromDays(30);
    private static readonly TimeSpan DefaultRateLimitBlock = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly ServerKeyStore _keys;
    private readonly RelayState _state;
    private readonly DeviceStore _devices;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<RelayClient> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<ECDiffieHellman> _ephemeral;
    private readonly Action<Span<byte>> _fillNonce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Initializes a new instance of the <see cref="RelayClient"/> class.</summary>
    /// <param name="http">The HTTP client (see <see cref="CreateHttpClient"/>).</param>
    /// <param name="keys">The server key.</param>
    /// <param name="state">The persistent state (sequence numbers, quota blocks).</param>
    /// <param name="devices">The device store.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    /// <param name="ephemeral">Makes the ephemeral P-256 key of an envelope; random when null.</param>
    /// <param name="fillNonce">Fills a nonce; random when null.</param>
    /// <param name="delay">Waits before the single retry of a 5xx; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    public RelayClient(
        HttpClient http,
        ServerKeyStore keys,
        RelayState state,
        DeviceStore devices,
        Func<PluginConfiguration> config,
        ILogger<RelayClient> logger,
        Func<DateTimeOffset>? now = null,
        Func<ECDiffieHellman>? ephemeral = null,
        Action<Span<byte>>? fillNonce = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _keys = keys;
        _state = state;
        _devices = devices;
        _config = config;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ephemeral = ephemeral ?? (() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
        _fillNonce = fillNonce ?? (span => RandomNumberGenerator.Fill(span));
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>Creates the HTTP client: one connection pool, connections renewed regularly.</summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateHttpClient()
        => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Reads the relay link's state for the settings page.</summary>
    /// <returns>The status; no secret.</returns>
    public RelayStatus Status()
    {
        var summary = _state.Summary();
        var kid = _keys.KidText;
        return new RelayStatus(
            _config().PushRelayUrl,
            kid,
            _keys.PublicKeyText,
            _state.BannedKid() == kid,
            summary.LastStatus,
            summary.LastAt,
            summary.Sent,
            summary.Refused,
            summary.QuotaHits,
            _state.ServerBlockedUntil() is { } until && until > _now() ? until : null);
    }

    /// <inheritdoc />
    public async Task<PushResult> SendAsync(ApnsCredentials? credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct)
    {
        // the notifier hands the device id where the direct client takes the token
        var deviceId = deviceToken;
        if (_state.BannedKid() == _keys.KidText)
        {
            return PushResult.Skipped(403, "server_banned");
        }

        var device = _devices.FindByDeviceId(deviceId);
        var devicePublicKey = device is null ? null : RelayCrypto.FromB64(device.DevicePublicKey);
        var rawDeviceId = RelayCrypto.FromB64(deviceId);
        if (device is null || devicePublicKey is null || rawDeviceId is null || message.Event is null)
        {
            return PushResult.Skipped(0, "unknown device or no event");
        }

        var url = _config().PushRelayUrl.TrimEnd('/');
        if (!PluginConfiguration.IsValidRelayUrl(url))
        {
            return PushResult.Failed(0, "invalid relay url");
        }

        var block = Blocked(deviceId);
        if (block is not null)
        {
            return PushResult.Skipped(429, $"quota spent until {block:O}");
        }

        var plaintext = RelayCrypto.PlaintextBlock(message.Event.ToProtocolJson());
        for (var attempt = 0; ; attempt++)
        {
            Reply reply;
            try
            {
                reply = await PostAsync(url, deviceId, rawDeviceId, devicePublicKey, plaintext, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                _state.Note(_now(), "unreachable", sent: false, quota: false);
                _logger.LogWarning("The relay did not answer: {Type}", ex.GetType().Name);
                return PushResult.Failed(0, "relay unreachable");
            }

            var status = reply.Status;
            if (status == 200)
            {
                _state.Update(deviceId, d => d.UnknownSince = null);
                _state.Note(_now(), "sent", sent: true, quota: false);
                return PushResult.Sent;
            }

            if (status == 409 && attempt == 0)
            {
                // the number was not above the relay's: the next one is max(seq + 1, clock), taken again by the retry
                continue;
            }

            if (status >= 500 && attempt == 0)
            {
                await _delay(RetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            return Handle(reply, deviceId);
        }
    }

    private static async Task<(string Error, string Scope, DateTimeOffset? ResetAt)> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length is 0 or > 8192)
            {
                return (string.Empty, string.Empty, null);
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            DateTimeOffset? reset = DateTimeOffset.TryParse(Text("reset_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            return (Text("error"), Text("scope"), reset);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, null);
        }
    }

    private static byte[] BuildBody(byte[] deviceId, byte[] serverPublicKey, long ts, long seq, byte[] envelope, byte[] signature)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("device_id", RelayCrypto.B64(deviceId));
            writer.WriteString("server_pub", RelayCrypto.B64(serverPublicKey));
            writer.WriteNumber("ts", ts);
            writer.WriteNumber("seq", seq);
            writer.WriteString("envelope", RelayCrypto.B64(envelope));
            writer.WriteString("sig", RelayCrypto.B64(signature));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private async Task<Reply> PostAsync(string url, string deviceId, byte[] rawDeviceId, byte[] devicePublicKey, byte[] plaintext, CancellationToken ct)
    {
        var now = _now();
        var ts = now.ToUnixTimeSeconds();
        var seq = _state.NextSeq(deviceId, now.ToUnixTimeMilliseconds());
        var serverPublicKey = _keys.PublicKey;
        var aad = RelayCrypto.Aad(RelayCrypto.Kid(serverPublicKey), rawDeviceId, ts, seq);
        var nonce = new byte[RelayCrypto.NonceSize];
        _fillNonce(nonce);
        byte[] envelope;
        using (var ephemeral = _ephemeral())
        {
            envelope = RelayCrypto.Seal(ephemeral, devicePublicKey, nonce, plaintext, aad);
        }

        var signature = _keys.Sign(RelayCrypto.SendMessage(rawDeviceId, ts, seq, envelope));
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/v1/send")
        {
            Content = new ByteArrayContent(BuildBody(rawDeviceId, serverPublicKey, ts, seq, envelope, signature))
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var (error, scope, resetAt) = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
        DateTimeOffset? retryAfter = response.Headers.RetryAfter?.Delta is { } delta
            ? now + delta
            : response.Headers.RetryAfter?.Date;
        return new Reply((int)response.StatusCode, error, scope, resetAt ?? retryAfter);
    }

    // when a spent quota ends, if it has not yet
    private DateTimeOffset? Blocked(string deviceId)
    {
        var now = _now();
        if (_state.ServerBlockedUntil() is { } server && server > now)
        {
            return server;
        }

        return _state.Get(deviceId).BlockedUntil is { } device && device > now ? device : null;
    }

    private PushResult Handle(Reply reply, string deviceId)
    {
        var now = _now();
        var label = reply.Error.Length > 0 ? reply.Error : reply.Status.ToString(CultureInfo.InvariantCulture);
        switch (reply.Status)
        {
            case 410:
                _state.Forget(deviceId);
                _state.Note(now, label, sent: false, quota: false);
                return PushResult.DeviceGone(410, label);

            case 404:
                // no such device, or it did not declare this server: the same answer, so give it time before dropping it
                var since = _state.Get(deviceId).UnknownSince ?? now;
                _state.Update(deviceId, d => d.UnknownSince ??= now);
                _state.Note(now, label, sent: false, quota: false);
                if (now - since >= UnknownDeviceGrace)
                {
                    _state.Forget(deviceId);
                    return PushResult.DeviceGone(404, label);
                }

                return PushResult.Skipped(404, label);

            case 429:
                var until = reply.ResetAt ?? (reply.Error == "rate_limited" ? now + DefaultRateLimitBlock : NextUtcMidnight(now));
                if (reply.Error == "rate_limited")
                {
                    _state.BlockServerUntil(until);
                }
                else if (reply.Scope == "server")
                {
                    _state.BlockServerUntil(until);
                }
                else
                {
                    _state.Update(deviceId, d => d.BlockedUntil = until);
                }

                _state.Note(now, $"{label} ({(reply.Scope.Length > 0 ? reply.Scope : "relay")})", sent: false, quota: true);
                return PushResult.Skipped(429, label);

            case 403:
                if (reply.Error == "server_banned")
                {
                    _state.Ban(_keys.KidText);
                    _logger.LogWarning("The relay banned this server's key; push through the relay stops");
                }

                _state.Note(now, label, sent: false, quota: false);
                return PushResult.Skipped(403, label);

            case 400 when reply.Error == "stale_timestamp":
                _logger.LogWarning("The relay says this server's clock is more than 5 minutes off: check the time synchronisation");
                _state.Note(now, label, sent: false, quota: false);
                return PushResult.Failed(400, label);

            case 409:
                _state.Note(now, label, sent: false, quota: false);
                return PushResult.Failed(409, label);

            case 502:
                // APNs refused for another reason: retried once already, then dropped
                _state.Note(now, label, sent: false, quota: false);
                return PushResult.Skipped(502, label);

            case >= 500:
                _state.Note(now, label, sent: false, quota: false);
                return PushResult.Failed(reply.Status, label);

            default:
                // 400, 401, 413: bugs on this side, retrying cannot help
                _logger.LogWarning("The relay refused a notification status={Status} error={Error}", reply.Status, reply.Error);
                _state.Note(now, label, sent: false, quota: false);
                return PushResult.Skipped(reply.Status, label);
        }
    }

    private static DateTimeOffset NextUtcMidnight(DateTimeOffset now)
        => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private sealed record Reply(int Status, string Error, string Scope, DateTimeOffset? ResetAt);
}
