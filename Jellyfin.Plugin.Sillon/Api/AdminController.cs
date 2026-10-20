using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Sillon.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>Body of <c>POST Admin/Requests/Purge</c>.</summary>
public sealed class PurgeBody
{
    /// <summary>Gets how old, in days, a finished request must be to be removed.</summary>
    [JsonPropertyName("olderThanDays")]
    public int OlderThanDays { get; init; }
}

/// <summary>The endpoints the administration page needs beyond the app API; administrators only.</summary>
[ApiController]
[Route("Sillon/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed partial class AdminController : ControllerBase
{
    private const int MaxBody = 1 << 10;

    private readonly AdminService _admin;
    private readonly RequestStore _requests;
    private readonly DeviceStore _devices;

    /// <summary>Initializes a new instance of the <see cref="AdminController"/> class.</summary>
    /// <param name="admin">The administration logic.</param>
    /// <param name="requests">The request store.</param>
    /// <param name="devices">The device store.</param>
    public AdminController(AdminService admin, RequestStore requests, DeviceStore devices)
    {
        _admin = admin;
        _requests = requests;
        _devices = devices;
    }

    /// <summary>Gives the plugin version, Lidarr's reachability, the push state and the request counts.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The health.</returns>
    [HttpGet("Health")]
    public async Task<ActionResult> Health(CancellationToken ct)
        => ApiJson.Result(await _admin.HealthAsync(ct).ConfigureAwait(false));

    /// <summary>Lists the registered devices per user; tokens are never returned.</summary>
    /// <returns>The users and their devices.</returns>
    [HttpGet("Devices")]
    public ActionResult Devices() => ApiJson.Result(new { users = _admin.DevicesByUser() });

    /// <summary>Unregisters a device.</summary>
    /// <param name="id">The device id from <c>GET Devices</c>.</param>
    /// <returns>An acknowledgement, or 404.</returns>
    [HttpDelete("Devices/{id}")]
    public ActionResult DeleteDevice(string id)
    {
        if (!DeviceId().IsMatch(id))
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "Invalid device id.");
        }

        return _devices.DeleteByPublicId(id)
            ? ApiJson.Result(new { ok = true })
            : ApiJson.Error(StatusCodes.Status404NotFound, "Device not found.");
    }

    /// <summary>Removes a finished request from the list.</summary>
    /// <param name="id">The request id.</param>
    /// <returns>An acknowledgement, 404, or 409 when the request is still live (cancel it first).</returns>
    [HttpDelete("Requests/{id}")]
    public ActionResult DeleteRequest(string id)
    {
        if (!long.TryParse(id, out var requestId) || requestId <= 0)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "Invalid request id.");
        }

        var request = _requests.Get(requestId);
        if (request is null)
        {
            return ApiJson.Error(StatusCodes.Status404NotFound, "Request not found.");
        }

        return _requests.DeleteFinished(requestId)
            ? ApiJson.Result(new { ok = true })
            : ApiJson.Error(StatusCodes.Status409Conflict, "The request is still in progress: cancel it first.");
    }

    /// <summary>Removes the finished requests older than a number of days.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many were removed.</returns>
    [HttpPost("Requests/Purge")]
    public async Task<ActionResult> Purge(CancellationToken ct)
    {
        var bytes = await BodyReader.ReadAsync(Request.Body, MaxBody, ct).ConfigureAwait(false);
        PurgeBody? body = null;
        try
        {
            body = bytes is null ? null : System.Text.Json.JsonSerializer.Deserialize<PurgeBody>(bytes, ApiJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (body is null || body.OlderThanDays is < 1 or > 3650)
        {
            return ApiJson.Error(StatusCodes.Status400BadRequest, "olderThanDays must be between 1 and 3650.");
        }

        var removed = _requests.DeleteFinishedBefore(_requests.Now.AddDays(-body.OlderThanDays));
        return ApiJson.Result(new { removed });
    }

    [GeneratedRegex("^[0-9a-fA-F]{16}$")]
    private static partial Regex DeviceId();
}
