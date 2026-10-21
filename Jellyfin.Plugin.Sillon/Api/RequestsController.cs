using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>Body of POST v1/requests.</summary>
public sealed class CreateRequestBody
{
    /// <summary>Gets the MusicBrainz album id.</summary>
    [JsonPropertyName("foreignAlbumId")]
    public string ForeignAlbumId { get; init; } = string.Empty;

    /// <summary>Gets the MusicBrainz artist id.</summary>
    [JsonPropertyName("foreignArtistId")]
    public string ForeignArtistId { get; init; } = string.Empty;

    /// <summary>Gets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the artist name.</summary>
    [JsonPropertyName("artist")]
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the year.</summary>
    [JsonPropertyName("year")]
    public int Year { get; init; }

    /// <summary>Gets the cover URL.</summary>
    [JsonPropertyName("coverUrl")]
    public string CoverUrl { get; init; } = string.Empty;
}

/// <summary>Body of POST v1/devices.</summary>
public sealed class RegisterDeviceBody
{
    /// <summary>Gets the token.</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Gets the platform.</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    /// <summary>Gets the APNs environment, <c>development</c> or <c>production</c>; anything else means the default.</summary>
    [JsonPropertyName("env")]
    public string Env { get; init; } = string.Empty;

    /// <summary>Gets the device public key (65 bytes, base64url) the relay protocol encrypts for.</summary>
    [JsonPropertyName("devicePublicKey")]
    public string? DevicePublicKey { get; init; }

    /// <summary>Gets how the device wants to be notified: <c>relay</c> or <c>direct</c>; absent means direct (the former behaviour).</summary>
    [JsonPropertyName("pushMode")]
    public string? PushMode { get; init; }
}

/// <summary>The album-request API the Sillon app talks to (same routes and JSON as the former gateway).</summary>
[ApiController]
[Route("Sillon")]
[Produces(MediaTypeNames.Application.Json)]
public sealed partial class RequestsController : ControllerBase
{
    private const int MaxBody = 1 << 20;

    private readonly RequestService _requests;
    private readonly RequestStore _store;
    private readonly DeviceStore _devices;
    private readonly CatalogService _catalog;
    private readonly IJellyfinLibrary _library;
    private readonly INotifier _notifier;
    private readonly Func<PluginConfiguration> _config;
    private readonly ServerKeyStore? _serverKey;

    /// <summary>Initializes a new instance of the <see cref="RequestsController"/> class.</summary>
    /// <param name="requests">The request service.</param>
    /// <param name="store">The request store.</param>
    /// <param name="devices">The device store.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="library">The Jellyfin library.</param>
    /// <param name="notifier">The notifier.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="serverKey">The server's relay signing key; without it relay registrations are refused.</param>
    public RequestsController(
        RequestService requests,
        RequestStore store,
        DeviceStore devices,
        CatalogService catalog,
        IJellyfinLibrary library,
        INotifier notifier,
        Func<PluginConfiguration> config,
        ServerKeyStore? serverKey = null)
    {
        _serverKey = serverKey;
        _requests = requests;
        _store = store;
        _devices = devices;
        _catalog = catalog;
        _library = library;
        _notifier = notifier;
        _config = config;
    }

    /// <summary>Returns the calling user.</summary>
    /// <returns>User id, name, administrator flag and whether notifications reach devices.</returns>
    [HttpGet("v1/me")]
    [Authorize]
    public ActionResult Me()
    {
        var caller = Caller();
        return caller is null
            ? Unauthorized()
            : ApiJson.Result(new MeBody(caller.Id, caller.Name, caller.IsAdmin, _notifier.Enabled));
    }

    /// <summary>Searches the catalogue.</summary>
    /// <param name="q">The search text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artists and albums.</returns>
    [HttpGet("v1/catalog/search")]
    [Authorize]
    public async Task<ActionResult> CatalogSearch([FromQuery] string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "q est obligatoire");
        }

        try
        {
            return ApiJson.Result(await _catalog.SearchAsync(query, ct).ConfigureAwait(false));
        }
        catch (CatalogException ex)
        {
            return ApiJson.Error(ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ClientLeft("catalog search");
        }
    }

    /// <summary>Lists an artist's albums.</summary>
    /// <param name="mbid">The artist's MusicBrainz id.</param>
    /// <param name="name">The artist's name, when the app knows it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artist and its albums.</returns>
    [HttpGet("v1/catalog/artist/{mbid}")]
    [Authorize]
    public async Task<ActionResult> CatalogArtist(string mbid, [FromQuery] string? name, CancellationToken ct)
    {
        try
        {
            return ApiJson.Result(await _catalog.ArtistAlbumsAsync(mbid, name, ct).ConfigureAwait(false));
        }
        catch (CatalogException ex)
        {
            return ApiJson.Error(ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ClientLeft("catalog artist");
        }
    }

    /// <summary>Lists the caller's requests; an administrator sees everyone's.</summary>
    /// <returns>The requests, newest first.</returns>
    [HttpGet("v1/requests")]
    [Authorize]
    public ActionResult ListRequests()
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        var requests = _store.List(caller.IsAdmin ? null : caller.Id).Select(RequestDto.From).ToList();
        return ApiJson.Result(new RequestList(requests));
    }

    /// <summary>Creates an album request.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The request; 409 with the existing one when the album already has a live request.</returns>
    [HttpPost("v1/requests")]
    [Authorize]
    public async Task<ActionResult> CreateRequest(CancellationToken ct)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        var (body, error) = await ReadBodyAsync<CreateRequestBody>(ct).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (body!.ForeignAlbumId.Length == 0 || body.ForeignArtistId.Length == 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "foreignAlbumId et foreignArtistId sont obligatoires");
        }

        if (!_config().RequestsEnabled())
        {
            return ApiJson.Error(StatusCodes.Status503ServiceUnavailable, "La liste d'envies n'est pas configurée");
        }

        try
        {
            var created = await _requests.CreateAsync(
                new NewRequest(caller.Id, caller.Name, body.ForeignAlbumId, body.ForeignArtistId, body.Title, body.Artist, body.Year, body.CoverUrl, caller.IsAdmin),
                ct).ConfigureAwait(false);
            return ApiJson.Result(RequestDto.From(created));
        }
        catch (ActiveRequestException)
        {
            var existing = _store.ActiveByForeignAlbum(body.ForeignAlbumId);
            return ApiJson.Result(
                new ConflictBody("Cet album est déjà dans votre liste d'envies", existing is null ? null : RequestDto.From(existing)),
                StatusCodes.Status409Conflict);
        }
        catch (AlbumNotAllowedException ex)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (RequestLimitException ex)
        {
            return ApiJson.Error(StatusCodes.Status429TooManyRequests, ex.Message);
        }
        catch (LidarrAddException ex)
        {
            return ApiJson.Error(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>Cancels a live request; users cancel their own, administrators any.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cancelled request.</returns>
    [HttpDelete("v1/requests/{id}")]
    [Authorize]
    public async Task<ActionResult> CancelRequest(string id, CancellationToken ct)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        if (!long.TryParse(id, out var requestId) || requestId <= 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "identifiant d'envie invalide");
        }

        try
        {
            return ApiJson.Result(RequestDto.From(await _requests.CancelAsync(requestId, caller.Id, caller.IsAdmin, ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is RequestNotFoundException or RequestForbiddenException or RequestNotActiveException or LidarrUnavailableException)
        {
            return ApiJson.Error(StatusOf(ex), ex.Message);
        }
    }

    /// <summary>Asks Lidarr to search again for a request that is requested, downloading or failed; users retry their own, administrators any.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The request.</returns>
    [HttpPost("v1/requests/{id}/retry")]
    [Authorize]
    public async Task<ActionResult> RetryRequest(string id, CancellationToken ct)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        if (!long.TryParse(id, out var requestId) || requestId <= 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "identifiant d'envie invalide");
        }

        try
        {
            return ApiJson.Result(RequestDto.From(await _requests.RetryAsync(requestId, caller.Id, caller.IsAdmin, ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is RequestNotFoundException or RequestForbiddenException or RequestNotRetryableException or LidarrUnavailableException)
        {
            return ApiJson.Error(StatusOf(ex), ex.Message);
        }
    }

    /// <summary>Marks a live request available by hand (manual mode).</summary>
    /// <param name="id">The request id.</param>
    /// <returns>The available request.</returns>
    [HttpPost("v1/requests/{id}/available")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult MarkAvailable(string id)
    {
        if (!long.TryParse(id, out var requestId) || requestId <= 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "identifiant d'envie invalide");
        }

        try
        {
            return ApiJson.Result(RequestDto.From(_requests.MarkAvailableByHand(requestId)));
        }
        catch (Exception ex) when (ex is RequestNotFoundException or RequestNotActiveException)
        {
            return ApiJson.Error(StatusOf(ex), ex.Message);
        }
    }

    /// <summary>Registers a device token.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An acknowledgement, or 409 when another user owns the token.</returns>
    [HttpPost("v1/devices")]
    [Authorize]
    public async Task<ActionResult> PutDevice(CancellationToken ct)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        var (body, error) = await ReadBodyAsync<RegisterDeviceBody>(ct).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var pushMode = body!.PushMode?.Trim().ToLowerInvariant();
        if (pushMode == PluginConfiguration.RelayMode)
        {
            return RegisterRelayDevice(caller.Id, body);
        }

        if (pushMode is not (null or "" or PluginConfiguration.DirectMode))
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "pushMode doit être relay ou direct");
        }

        var token = body.Token.Trim();
        if (token.Length == 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "token est obligatoire");
        }

        if (!HexToken().IsMatch(token))
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "token doit être hexadécimal");
        }

        var env = body.Env is "development" or "production" ? body.Env : string.Empty;
        if (!_devices.Upsert(token, caller.Id, body.Platform, env))
        {
            return ApiJson.Error(StatusCodes.Status409Conflict, "Cet appareil est déjà enregistré pour un autre compte");
        }

        // a body without the new fields gets the answer it always got
        return pushMode is null or ""
            ? ApiJson.Result(new AckBody(true))
            : ApiJson.Result(new { ok = true, push = new { protocol = 1, mode = PluginConfiguration.DirectMode } });
    }

    /// <summary>Removes one of the caller's relay devices.</summary>
    /// <param name="deviceId">The device id: SHA-256 of its public key, base64url.</param>
    /// <returns>An acknowledgement.</returns>
    [HttpDelete("v1/devices/by-id/{deviceId}")]
    [Authorize]
    public ActionResult DeleteDeviceById(string deviceId)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        _devices.DeleteByDeviceId(deviceId, caller.Id);
        return ApiJson.Result(new AckBody(true));
    }

    // relay mode: the device sends its public key and no token; the server answers with its own public key and the relay's address
    private ActionResult RegisterRelayDevice(string userId, RegisterDeviceBody body)
    {
        var config = _config();
        if (_serverKey is null || config.ResolvePushMode(Environment.GetEnvironmentVariable) != PluginConfiguration.RelayMode)
        {
            return ApiJson.Error(StatusCodes.Status409Conflict, "Ce serveur n'utilise pas le relais de notifications");
        }

        if (!string.IsNullOrWhiteSpace(body.Token))
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "token doit être omis en mode relay");
        }

        var key = RelayCrypto.FromB64(body.DevicePublicKey);
        if (key is null || !RelayCrypto.IsValidPublicKey(key))
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "devicePublicKey doit être une clé P-256 de 65 octets (X9.63) en base64url");
        }

        var env = body.Env is "development" or "production" ? body.Env : string.Empty;
        if (!_devices.UpsertRelay(RelayCrypto.B64(key), userId, body.Platform, env))
        {
            return ApiJson.Error(StatusCodes.Status409Conflict, "Cet appareil est déjà enregistré pour un autre compte");
        }

        return ApiJson.Result(new
        {
            ok = true,
            push = new
            {
                protocol = 1,
                mode = PluginConfiguration.RelayMode,
                serverPublicKey = _serverKey.PublicKeyText,
                kid = _serverKey.KidText,
                relayUrl = config.PushRelayUrl
            }
        });
    }

    /// <summary>Removes one of the caller's device tokens.</summary>
    /// <param name="token">The token.</param>
    /// <returns>An acknowledgement.</returns>
    [HttpDelete("v1/devices/{token}")]
    [Authorize]
    public ActionResult DeleteDevice(string token)
    {
        var caller = Caller();
        if (caller is null)
        {
            return Unauthorized();
        }

        _devices.Delete(token, caller.Id);
        return ApiJson.Result(new AckBody(true));
    }

    // the app cancels a search when the user types on: not an error, so it must not reach Jellyfin's exception middleware (which logs it as ERR)
    private ActionResult ClientLeft(string what)
    {
        HttpContext?.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger<RequestsController>()
            .LogDebug("The client cancelled the {What} request", what);
        return StatusCode(499);
    }

    [GeneratedRegex("^[0-9a-fA-F]{1,200}$")]
    private static partial Regex HexToken();

    private static int StatusOf(Exception ex) => ex switch
    {
        RequestNotFoundException => StatusCodes.Status404NotFound,
        RequestForbiddenException => StatusCodes.Status403Forbidden,
        RequestNotActiveException or RequestNotRetryableException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status502BadGateway
    };

    private async Task<(T? Body, ActionResult? Error)> ReadBodyAsync<T>(CancellationToken ct)
        where T : class, new()
    {
        var bytes = await BodyReader.ReadAsync(Request.Body, MaxBody, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return (null, ApiJson.Error(StatusCodes.Status400BadRequest, "corps trop volumineux"));
        }

        if (bytes.All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
        {
            return (new T(), null);
        }

        try
        {
            return (JsonSerializer.Deserialize<T>(bytes, ApiJson.Options) ?? new T(), null);
        }
        catch (JsonException)
        {
            return (null, ApiJson.Error(StatusCodes.Status400BadRequest, "corps JSON invalide"));
        }
    }

    private CallerInfo? Caller()
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id))
        {
            return null;
        }

        var userId = id.ToString("N");
        var name = _library.GetUser(userId)?.Name ?? User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
        return new CallerInfo(userId, name, User.IsInRole("Administrator"));
    }

    private sealed record CallerInfo(string Id, string Name, bool IsAdmin);

    private sealed record MeBody(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("isAdmin")] bool IsAdmin,
        [property: JsonPropertyName("pushEnabled")] bool PushEnabled);

    private sealed record RequestList([property: JsonPropertyName("requests")] IReadOnlyList<RequestDto> Requests);

    private sealed record ConflictBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("request")] RequestDto? Request);

    private sealed record AckBody([property: JsonPropertyName("ok")] bool Ok);
}
