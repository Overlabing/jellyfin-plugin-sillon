using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>What a push is made of.</summary>
/// <param name="Title">The alert title, or <c>null</c> for none.</param>
/// <param name="Body">The alert body.</param>
/// <param name="Extra">Keys placed next to <c>aps</c> in the payload (request_id, album_id).</param>
/// <param name="Event">The same notification as data; the relay sends this, never the sentence.</param>
public sealed record PushMessage(string? Title, string Body, IReadOnlyDictionary<string, object> Extra, NotificationEvent? Event = null);

/// <summary>How an APNs delivery ended.</summary>
public enum PushOutcome
{
    /// <summary>APNs accepted the push.</summary>
    Sent,

    /// <summary>The token is dead (410, <c>BadDeviceToken</c>, <c>Unregistered</c>): delete the device.</summary>
    DeviceGone,

    /// <summary>Any other failure, worth retrying later.</summary>
    Failed,

    /// <summary>Not delivered and not worth retrying: a spent quota, a refusal, a device the relay does not know (yet).</summary>
    Skipped
}

/// <summary>Result of one push.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="StatusCode">The HTTP status, 0 when none came back.</param>
/// <param name="Reason">APNs's reason, or a short local description of the failure.</param>
public sealed record PushResult(PushOutcome Outcome, int StatusCode, string Reason)
{
    /// <summary>Gets the accepted result.</summary>
    public static PushResult Sent { get; } = new(PushOutcome.Sent, 200, string.Empty);

    /// <summary>Builds the dead-token result.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="reason">APNs's reason.</param>
    /// <returns>The result.</returns>
    public static PushResult DeviceGone(int status, string reason) => new(PushOutcome.DeviceGone, status, reason);

    /// <summary>Builds a failure result.</summary>
    /// <param name="status">The HTTP status, 0 when none.</param>
    /// <param name="reason">The reason.</param>
    /// <returns>The result.</returns>
    public static PushResult Failed(int status, string reason) => new(PushOutcome.Failed, status, reason);

    /// <summary>Builds a dropped result: not delivered, not worth retrying.</summary>
    /// <param name="status">The HTTP status, 0 when none.</param>
    /// <param name="reason">The reason.</param>
    /// <returns>The result.</returns>
    public static PushResult Skipped(int status, string reason) => new(PushOutcome.Skipped, status, reason);
}

/// <summary>Sends one push to one device.</summary>
public interface IPushSender
{
    /// <summary>Sends an alert.</summary>
    /// <param name="credentials">The APNs credentials; <c>null</c> for the relay, which holds none.</param>
    /// <param name="deviceToken">The device token; the device id (base64url) for the relay.</param>
    /// <param name="environment"><c>development</c> for the sandbox, anything else for production.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PushResult> SendAsync(ApnsCredentials? credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct);
}

/// <summary>APNs over HTTP/2 with a provider JWT (ES256), using only the framework.</summary>
public sealed class ApnsClient : IPushSender, IDisposable
{
    /// <summary>The production host.</summary>
    public const string ProductionHost = "https://api.push.apple.com";

    /// <summary>The sandbox host.</summary>
    public const string SandboxHost = "https://api.sandbox.push.apple.com";

    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(50); // APNs refuses tokens older than an hour
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Expiration = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly ILogger<ApnsClient> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private ApnsCredentials? _credentials;
    private ECDsa? _key;
    private string _jwt = string.Empty;
    private DateTimeOffset _jwtAt;

    /// <summary>Initializes a new instance of the <see cref="ApnsClient"/> class.</summary>
    /// <param name="http">A long-lived client (see <see cref="CreateHttpClient"/>).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    public ApnsClient(HttpClient http, ILogger<ApnsClient> logger, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Builds the client APNs wants: connections kept alive and pinged, recycled every ten minutes.</summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        KeepAlivePingDelay = TimeSpan.FromSeconds(30)
    });

    /// <inheritdoc />
    public async Task<PushResult> SendAsync(ApnsCredentials? credentials, string deviceToken, string environment, PushMessage message, CancellationToken ct)
    {
        if (credentials is null)
        {
            return PushResult.Failed(0, "no APNs credentials");
        }

        string jwt;
        try
        {
            jwt = Token(credentials);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            _logger.LogWarning("APNs key cannot be used: {Message}", ex.Message);
            return PushResult.Failed(0, "invalid key");
        }

        var host = environment == "development" ? SandboxHost : ProductionHost;
        using var request = new HttpRequestMessage(HttpMethod.Post, host + "/3/device/" + deviceToken)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(Payload(message))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("authorization", "bearer " + jwt);
        request.Headers.TryAddWithoutValidation("apns-topic", credentials.Topic);
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", "10");
        request.Headers.TryAddWithoutValidation("apns-expiration", (_now() + Expiration).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return PushResult.Sent;
            }

            var reason = await ReadReasonAsync(response, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "Unregistered")
            {
                return PushResult.DeviceGone(status, reason);
            }

            _logger.LogWarning("APNs refused a notification status={Status} reason={Reason} env={Env}", status, reason, environment);
            return PushResult.Failed(status, reason);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            // the message of an HttpRequestException names the host, never the token
            _logger.LogWarning("APNs push failed env={Env}: {Message}", environment, ex.Message);
            return PushResult.Failed(0, "unreachable");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _key?.Dispose();

    private static byte[] Payload(PushMessage message)
    {
        var alert = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(message.Title))
        {
            alert["title"] = message.Title;
        }

        alert["body"] = message.Body;
        var payload = new Dictionary<string, object> { ["aps"] = new Dictionary<string, object> { ["alert"] = alert, ["sound"] = "default" } };
        foreach (var (key, value) in message.Extra)
        {
            payload[key] = value;
        }

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    // returns the provider JWT, signed again after 50 minutes or when the credentials change
    private string Token(ApnsCredentials credentials)
    {
        lock (_lock)
        {
            var now = _now();
            if (_jwt.Length > 0 && credentials == _credentials && now - _jwtAt < JwtLifetime)
            {
                return _jwt;
            }

            if (credentials != _credentials || _key is null)
            {
                var key = ECDsa.Create();
                try
                {
                    key.ImportFromPem(credentials.PrivateKey.Replace("\\n", "\n", StringComparison.Ordinal));
                }
                catch
                {
                    key.Dispose();
                    throw;
                }

                _key?.Dispose();
                _key = key;
                _credentials = credentials;
            }

            var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "ES256", ["kid"] = credentials.KeyId });
            var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["iss"] = credentials.TeamId, ["iat"] = now.ToUnixTimeSeconds() });
            var signing = B64(header) + "." + B64(claims);
            var signature = _key.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            _jwt = signing + "." + B64(signature);
            _jwtAt = now;
            return _jwt;
        }
    }
}
