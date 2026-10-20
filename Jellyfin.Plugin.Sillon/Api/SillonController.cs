using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Lidarr;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>Body of the Lidarr connection test.</summary>
/// <param name="Url">Lidarr URL.</param>
/// <param name="ApiKey">Lidarr API key.</param>
public sealed record LidarrTestRequest(string? Url, string? ApiKey);

/// <summary>The Sillon API.</summary>
[ApiController]
[Route("Sillon")]
[Produces(MediaTypeNames.Application.Json)]
public class SillonController : ControllerBase
{
    private readonly ILogger<SillonController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>Initializes a new instance of the <see cref="SillonController"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="config">The configuration accessor.</param>
    public SillonController(ILogger<SillonController> logger, IHttpClientFactory httpClientFactory, Func<PluginConfiguration> config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    /// <summary>Unauthenticated liveness probe.</summary>
    /// <returns>An ok status.</returns>
    [HttpGet("health")]
    [AllowAnonymous]
    public ActionResult Health() => new JsonResult(new { status = "ok" });

    /// <summary>Discovery endpoint for clients.</summary>
    /// <returns>Plugin version, features and mode.</returns>
    [HttpGet("Config")]
    [Authorize]
    public ActionResult GetConfig()
    {
        var config = _config();
        return new JsonResult(new
        {
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            features = config.ExposeToApps && config.RequestsEnabled() ? new[] { "requests" } : Array.Empty<string>(),
            mode = config.FulfillmentMode.ToString().ToLowerInvariant()
        });
    }

    /// <summary>Tests a Lidarr URL and API key.</summary>
    /// <param name="request">The URL and key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Lidarr version.</returns>
    [HttpPost("Lidarr/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> TestLidarr([FromBody] LidarrTestRequest request, CancellationToken ct)
    {
        var url = (request.Url ?? string.Empty).Trim();
        if (url.Length == 0 || !PluginConfiguration.IsValidLidarrUrl(url))
        {
            return Task.FromResult<ActionResult>(new JsonResult(new { message = "Enter an http:// or https:// URL." }) { StatusCode = StatusCodes.Status400BadRequest });
        }

        // the page never knows the stored key: the mask means "use the saved one"
        var key = string.IsNullOrEmpty(request.ApiKey) || request.ApiKey == PluginConfiguration.MaskedSecret ? _config().LidarrApiKey : request.ApiKey;
        return Run(new LidarrClient(Http(), url, key), async c =>
        {
            var status = await c.GetStatusAsync(ct).ConfigureAwait(false);
            return new { version = status.Version };
        });
    }

    /// <summary>Tests a Lidarr URL and API key and lists what the configuration page lets the administrator choose from, without saving anything.</summary>
    /// <param name="request">The URL and key; the mask or no key means the saved key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The version, quality profiles, metadata profiles and root folders.</returns>
    [HttpPost("Lidarr/Options")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> LidarrOptions([FromBody] LidarrTestRequest request, CancellationToken ct)
    {
        var url = (request.Url ?? string.Empty).Trim();
        if (url.Length == 0 || !PluginConfiguration.IsValidLidarrUrl(url))
        {
            return Task.FromResult<ActionResult>(new JsonResult(new { message = "Enter an http:// or https:// URL." }) { StatusCode = StatusCodes.Status400BadRequest });
        }

        var key = string.IsNullOrEmpty(request.ApiKey) || request.ApiKey == PluginConfiguration.MaskedSecret ? _config().LidarrApiKey : request.ApiKey;
        return Run(new LidarrClient(Http(), url, key), async c =>
        {
            var status = await c.GetStatusAsync(ct).ConfigureAwait(false);
            var quality = await c.GetQualityProfilesAsync(ct).ConfigureAwait(false);
            var metadata = await c.GetMetadataProfilesAsync(ct).ConfigureAwait(false);
            var folders = await c.GetRootFoldersAsync(ct).ConfigureAwait(false);
            return new
            {
                version = status.Version,
                qualityProfiles = quality.Select(p => new { id = p.Id, name = p.Name }).ToList(),
                metadataProfiles = metadata.Select(p => new { id = p.Id, name = p.Name }).ToList(),
                rootFolders = folders.Select(f => new { path = f.Path }).ToList()
            };
        });
    }

    /// <summary>Lists Lidarr quality profiles using the saved configuration.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Id and name pairs.</returns>
    [HttpGet("Lidarr/QualityProfiles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> QualityProfiles(CancellationToken ct)
        => Run(SavedClient(), async c =>
            (await c.GetQualityProfilesAsync(ct).ConfigureAwait(false)).Select(p => new { id = p.Id, name = p.Name }).ToList());

    /// <summary>Lists Lidarr metadata profiles using the saved configuration.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Id and name pairs.</returns>
    [HttpGet("Lidarr/MetadataProfiles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> MetadataProfiles(CancellationToken ct)
        => Run(SavedClient(), async c =>
            (await c.GetMetadataProfilesAsync(ct).ConfigureAwait(false)).Select(p => new { id = p.Id, name = p.Name }).ToList());

    /// <summary>Lists Lidarr root folders using the saved configuration.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paths.</returns>
    [HttpGet("Lidarr/RootFolders")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> RootFolders(CancellationToken ct)
        => Run(SavedClient(), async c =>
            (await c.GetRootFoldersAsync(ct).ConfigureAwait(false)).Select(f => new { path = f.Path }).ToList());

    private HttpClient Http() => _httpClientFactory.CreateClient(NamedClient.Default);

    private LidarrClient SavedClient()
    {
        var config = _config();
        return new LidarrClient(Http(), config.LidarrUrl, config.LidarrApiKey);
    }

    private async Task<ActionResult> Run<T>(LidarrClient client, Func<LidarrClient, Task<T>> call)
    {
        try
        {
            return new JsonResult(await call(client).ConfigureAwait(false));
        }
        catch (LidarrException ex)
        {
            // The message never carries the API key; log the message only.
            _logger.LogWarning("Lidarr call failed: {Message}", ex.Message);
            return new JsonResult(new { message = ex.Message }) { StatusCode = StatusCodes.Status502BadGateway };
        }
    }
}
