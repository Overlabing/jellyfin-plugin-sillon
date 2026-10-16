using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>Reads a request body up to a limit.</summary>
internal static class BodyReader
{
    /// <summary>Reads the whole stream.</summary>
    /// <param name="body">The stream.</param>
    /// <param name="limit">The most bytes accepted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bytes, or null when the body is larger than the limit.</returns>
    public static async Task<byte[]?> ReadAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>Lidarr's webhook.</summary>
[ApiController]
[Route("Sillon")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class WebhookController : ControllerBase
{
    private const int MaxBody = 1 << 20;

    private readonly RequestService _requests;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<WebhookController> _logger;

    /// <summary>Initializes a new instance of the <see cref="WebhookController"/> class.</summary>
    /// <param name="requests">The request service.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="logger">The logger.</param>
    public WebhookController(RequestService requests, Func<PluginConfiguration> config, ILogger<WebhookController> logger)
    {
        _requests = requests;
        _config = config;
        _logger = logger;
    }

    /// <summary>Receives a Lidarr event: Grab, Download or AlbumImport, ImportFailure or DownloadFailure; others are ignored.</summary>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An acknowledgement.</returns>
    [HttpPost("webhooks/lidarr")]
    [AllowAnonymous]
    public async Task<ActionResult> Lidarr([FromQuery] string? secret, CancellationToken ct)
    {
        if (!SecretMatches(secret, _config().WebhookSecret))
        {
            return ApiJson.Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var bytes = await BodyReader.ReadAsync(Request.Body, MaxBody, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "corps trop volumineux");
        }

        LidarrEvent? lidarrEvent;
        try
        {
            lidarrEvent = JsonSerializer.Deserialize<LidarrEvent>(bytes, ApiJson.Options);
        }
        catch (JsonException)
        {
            lidarrEvent = null;
        }

        if (lidarrEvent is null)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "corps JSON invalide");
        }

        var albumIds = lidarrEvent.AlbumIds();
        IReadOnlyList<string> outcome;
        switch (lidarrEvent.EventType.ToLowerInvariant())
        {
            case "test":
                outcome = new[] { "test" };
                break;
            case "grab":
                outcome = _requests.OnGrab(albumIds);
                break;
            case "download":
            case "albumimport":
                outcome = await _requests.OnImportAsync(albumIds, lidarrEvent.Folders(), ct).ConfigureAwait(false);
                break;
            case "importfailure":
            case "downloadfailure":
                var message = lidarrEvent.Message.Length > 0 ? lidarrEvent.Message : lidarrEvent.EventType;
                outcome = await _requests.OnFailureAsync(albumIds, message, ct).ConfigureAwait(false);
                break;
            default:
                outcome = new[] { "ignored" };
                break;
        }

        _logger.LogInformation(
            "Lidarr webhook event={Event} instance={Instance} albums={Albums} result={Result}",
            lidarrEvent.EventType,
            lidarrEvent.InstanceName,
            string.Join(',', albumIds),
            string.Join("; ", outcome));
        return ApiJson.Result(new Ack(true));
    }

    /// <summary>Compares the given secret with the configured one in constant time; an empty configured secret never matches.</summary>
    /// <param name="given">The secret from the query string.</param>
    /// <param name="expected">The configured secret.</param>
    /// <returns><c>true</c> when they match.</returns>
    public static bool SecretMatches(string? given, string expected)
        => !string.IsNullOrEmpty(expected)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given ?? string.Empty), Encoding.UTF8.GetBytes(expected));

    private sealed record Ack([property: JsonPropertyName("ok")] bool Ok);
}
