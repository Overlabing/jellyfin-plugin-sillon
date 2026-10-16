using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>The outcome for one device of a notification.</summary>
/// <param name="Platform">The device platform.</param>
/// <param name="Environment">The APNs environment the device registered with.</param>
/// <param name="Result">What APNs answered.</param>
public sealed record DevicePushResult(string Platform, string Environment, PushResult Result);

/// <summary>
/// Notifies every device of a user through APNs. The credentials are resolved on every send, so a configuration change applies at once;
/// without enabled push and a key it sends nothing, like <see cref="NoopNotifier"/>.
/// </summary>
public sealed class ApnsNotifier : INotifier
{
    private const string Title = "Sillon";

    private readonly DeviceStore _devices;
    private readonly IPushSender _sender;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<string, string?> _environment;
    private readonly ILogger<ApnsNotifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="ApnsNotifier"/> class.</summary>
    /// <param name="devices">The device store.</param>
    /// <param name="sender">The APNs sender.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="logger">The logger.</param>
    public ApnsNotifier(DeviceStore devices, IPushSender sender, Func<PluginConfiguration> config, Func<string, string?> environment, ILogger<ApnsNotifier> logger)
    {
        _devices = devices;
        _sender = sender;
        _config = config;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Enabled => Credentials() is not null;

    /// <summary>Tells where the key comes from, whether or not notifications are enabled.</summary>
    /// <returns>The key source.</returns>
    public ApnsKeySource KeySource() => ApnsCredentialResolver.Resolve(_config(), _environment)?.Source ?? ApnsKeySource.None;

    /// <summary>Resolves the credentials when notifications are enabled.</summary>
    /// <returns>The credentials, or <c>null</c> when push is off or has no key.</returns>
    public ApnsCredentials? Credentials()
    {
        var config = _config();
        return config.ApnsEnabled ? ApnsCredentialResolver.Resolve(config, _environment) : null;
    }

    /// <inheritdoc />
    public async Task<bool> NotifyAsync(string userId, string body, IReadOnlyDictionary<string, object> extra, CancellationToken ct)
    {
        var results = await SendAsync(userId, new PushMessage(Title, body, extra), ct).ConfigureAwait(false);
        return results.Count == 0
            || results.Any(r => r.Result.Outcome == PushOutcome.Sent)
            || results.All(r => r.Result.Outcome == PushOutcome.DeviceGone);
    }

    /// <summary>Sends a message to every device of a user and removes the devices APNs no longer knows.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One result per device; empty when push is off or the user has no device.</returns>
    public async Task<IReadOnlyList<DevicePushResult>> SendAsync(string userId, PushMessage message, CancellationToken ct)
    {
        var credentials = Credentials();
        if (credentials is null)
        {
            return Array.Empty<DevicePushResult>();
        }

        var results = new List<DevicePushResult>();
        foreach (var device in _devices.For(userId))
        {
            var result = await _sender.SendAsync(credentials, device.Token, device.Env, message, ct).ConfigureAwait(false);
            if (result.Outcome == PushOutcome.DeviceGone)
            {
                _devices.Delete(device.Token, userId);
                _logger.LogInformation("Removed a device APNs no longer knows platform={Platform} reason={Reason}", device.Platform, result.Reason);
            }

            results.Add(new DevicePushResult(device.Platform, device.Env, result));
        }

        return results;
    }
}
