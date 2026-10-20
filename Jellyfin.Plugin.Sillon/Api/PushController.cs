using System.Net.Mime;
using Jellyfin.Plugin.Sillon.Push;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>The notification admin API.</summary>
[ApiController]
[Route("Sillon/Push")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class PushController : ControllerBase
{
    private readonly ApnsNotifier _notifier;

    /// <summary>Initializes a new instance of the <see cref="PushController"/> class.</summary>
    /// <param name="notifier">The APNs notifier.</param>
    public PushController(ApnsNotifier notifier) => _notifier = notifier;

    /// <summary>Tells where the key comes from and whether notifications are active.</summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Status()
        => new JsonResult(new { enabled = _notifier.Enabled, source = _notifier.KeySource().ToString().ToLowerInvariant() });

    /// <summary>Sends a test notification to the devices of the calling administrator, or of the given user.</summary>
    /// <param name="userId">The user to notify; the caller when omitted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One result per device.</returns>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> Test([FromQuery] string? userId, CancellationToken ct)
    {
        if (!Guid.TryParse(string.IsNullOrEmpty(userId) ? User.FindFirst("Jellyfin-UserId")?.Value : userId, out var id))
        {
            return string.IsNullOrEmpty(userId) ? Unauthorized() : BadRequest(new { message = "Invalid user id." });
        }

        if (!_notifier.Enabled)
        {
            return new JsonResult(new { message = "Notifications are not active (turned off, or no key configured)." }) { StatusCode = StatusCodes.Status409Conflict };
        }

        var results = await _notifier.SendAsync(
            id.ToString("N"),
            new PushMessage(null, "Sillon: notifications are working", new Dictionary<string, object>()),
            ct).ConfigureAwait(false);
        return new JsonResult(new
        {
            devices = results.Select(r => new
            {
                platform = r.Platform,
                env = r.Environment,
                result = r.Result.Outcome.ToString().ToLowerInvariant(),
                status = r.Result.StatusCode,
                reason = r.Result.Reason
            }).ToList()
        });
    }
}
