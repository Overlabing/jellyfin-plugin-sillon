using System.Text.Json.Serialization;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Storage;

namespace Jellyfin.Plugin.Sillon.Api;

/// <summary>Whether Lidarr answers.</summary>
/// <param name="Configured">Whether a URL and an API key are set.</param>
/// <param name="Reachable">Whether the status call succeeded.</param>
/// <param name="Version">Lidarr's version when reachable.</param>
/// <param name="Message">Why it failed, safe to show to an administrator.</param>
public sealed record LidarrHealth(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>The push configuration state; never a secret.</summary>
/// <param name="Enabled">Whether notifications are on and keyed.</param>
/// <param name="TurnedOn">Whether the administrator ticked the box.</param>
/// <param name="KeySource">Where the key comes from: <c>none</c>, <c>config</c> or <c>environment</c>.</param>
/// <param name="HasKeyId">Whether a key id is set.</param>
/// <param name="HasTeamId">Whether a team id is set.</param>
/// <param name="Topic">The topic (the app's bundle id).</param>
/// <param name="Devices">How many devices are registered.</param>
public sealed record PushHealth(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("turnedOn")] bool TurnedOn,
    [property: JsonPropertyName("keySource")] string KeySource,
    [property: JsonPropertyName("hasKeyId")] bool HasKeyId,
    [property: JsonPropertyName("hasTeamId")] bool HasTeamId,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("devices")] int Devices);

/// <summary>The overview page's data.</summary>
/// <param name="Version">The plugin version.</param>
/// <param name="Mode">The fulfilment mode, lower case.</param>
/// <param name="Lidarr">The Lidarr state.</param>
/// <param name="Push">The push state.</param>
/// <param name="Requests">Request counts by status.</param>
public sealed record AdminHealth(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("lidarr")] LidarrHealth Lidarr,
    [property: JsonPropertyName("push")] PushHealth Push,
    [property: JsonPropertyName("requests")] IReadOnlyDictionary<string, int> Requests);

/// <summary>One registered device, without its token.</summary>
/// <param name="Id">A hash of the token, to address the device.</param>
/// <param name="Platform">The platform.</param>
/// <param name="Env">The APNs environment.</param>
/// <param name="TokenHint">The last characters of the token.</param>
/// <param name="CreatedAt">When it registered.</param>
/// <param name="UpdatedAt">When it last registered.</param>
public sealed record DeviceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("env")] string Env,
    [property: JsonPropertyName("tokenHint")] string TokenHint,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>A user and the devices they registered.</summary>
/// <param name="UserId">The user id.</param>
/// <param name="UserName">The user name, or empty when the user no longer exists.</param>
/// <param name="Devices">The devices.</param>
public sealed record UserDevices(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceDto> Devices);

/// <summary>The logic behind the administration page, apart from HTTP.</summary>
public sealed class AdminService
{
    private readonly RequestStore _requests;
    private readonly DeviceStore _devices;
    private readonly IJellyfinLibrary _library;
    private readonly ILidarrProvider _lidarr;
    private readonly ApnsNotifier _notifier;
    private readonly Func<PluginConfiguration> _config;
    private readonly string _version;

    /// <summary>Initializes a new instance of the <see cref="AdminService"/> class.</summary>
    /// <param name="requests">The request store.</param>
    /// <param name="devices">The device store.</param>
    /// <param name="library">The Jellyfin library.</param>
    /// <param name="lidarr">The Lidarr client provider.</param>
    /// <param name="notifier">The APNs notifier.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="version">The plugin version.</param>
    public AdminService(
        RequestStore requests,
        DeviceStore devices,
        IJellyfinLibrary library,
        ILidarrProvider lidarr,
        ApnsNotifier notifier,
        Func<PluginConfiguration> config,
        string version)
    {
        _requests = requests;
        _devices = devices;
        _library = library;
        _lidarr = lidarr;
        _notifier = notifier;
        _config = config;
        _version = version;
    }

    /// <summary>Probes Lidarr and gathers the push and request state.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The health.</returns>
    public async Task<AdminHealth> HealthAsync(CancellationToken ct)
    {
        var config = _config();
        var counts = new Dictionary<string, int>
        {
            [RequestStatus.Requested] = 0,
            [RequestStatus.Downloading] = 0,
            [RequestStatus.Available] = 0,
            [RequestStatus.Failed] = 0,
            [RequestStatus.Cancelled] = 0
        };
        foreach (var request in _requests.List(null))
        {
            counts[request.Status] = counts.GetValueOrDefault(request.Status) + 1;
        }

        var credentials = ApnsCredentialResolver.Resolve(config, Environment.GetEnvironmentVariable);
        var push = new PushHealth(
            _notifier.Enabled,
            config.ApnsEnabled,
            _notifier.KeySource().ToString().ToLowerInvariant(),
            !string.IsNullOrWhiteSpace(credentials?.KeyId),
            !string.IsNullOrWhiteSpace(credentials?.TeamId),
            string.IsNullOrWhiteSpace(config.ApnsTopic) ? PluginConfiguration.DefaultApnsTopic : config.ApnsTopic,
            _devices.All().Count);

        return new AdminHealth(_version, config.FulfillmentMode.ToString().ToLowerInvariant(), await ProbeLidarrAsync(config, ct).ConfigureAwait(false), push, counts);
    }

    /// <summary>Lists the registered devices per user.</summary>
    /// <returns>The users with their devices, by user name.</returns>
    public IReadOnlyList<UserDevices> DevicesByUser()
        => _devices.All()
            .GroupBy(d => d.UserId)
            .Select(g => new UserDevices(
                g.Key,
                _library.GetUser(g.Key)?.Name ?? string.Empty,
                g.Select(d => new DeviceDto(d.PublicId, d.Platform, d.Env, d.TokenHint, d.CreatedAt, d.UpdatedAt)).ToList()))
            .OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<LidarrHealth> ProbeLidarrAsync(PluginConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.LidarrUrl) || string.IsNullOrWhiteSpace(config.LidarrApiKey))
        {
            return new LidarrHealth(false, false, null, "Lidarr URL or API key not set.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var status = await _lidarr.Create().GetStatusAsync(timeout.Token).ConfigureAwait(false);
            return new LidarrHealth(true, true, status.Version, null);
        }
        catch (LidarrException ex)
        {
            return new LidarrHealth(true, false, null, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LidarrHealth(true, false, null, "Lidarr did not answer in time.");
        }
    }
}
