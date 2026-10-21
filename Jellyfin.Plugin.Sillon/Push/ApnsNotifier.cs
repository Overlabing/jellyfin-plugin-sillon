using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>The outcome for one device of a notification.</summary>
/// <param name="Platform">The device platform.</param>
/// <param name="Environment">The APNs environment the device registered with.</param>
/// <param name="Result">What APNs (or the relay) answered.</param>
public sealed record DevicePushResult(string Platform, string Environment, PushResult Result);

/// <summary>
/// Notifies every device of a user, by the configured mode. <c>direct</c>: through APNs with the administrator's own key (the
/// credentials are resolved on every send, so a configuration change applies at once; without enabled push and a key nothing is sent).
/// <c>relay</c>: through the sillon-push relay, to the devices that registered a device key. <c>off</c>: nothing.
/// </summary>
public sealed class ApnsNotifier : INotifier
{
    private const string Title = "Sillon";

    private readonly DeviceStore _devices;
    private readonly IPushSender _sender;
    private readonly RelayClient? _relay;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<string, string?> _environment;
    private readonly ILogger<ApnsNotifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="ApnsNotifier"/> class.</summary>
    /// <param name="devices">The device store.</param>
    /// <param name="sender">The APNs sender.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="relay">The relay client; without it relay mode sends nothing.</param>
    public ApnsNotifier(DeviceStore devices, IPushSender sender, Func<PluginConfiguration> config, Func<string, string?> environment, ILogger<ApnsNotifier> logger, RelayClient? relay = null)
    {
        _devices = devices;
        _sender = sender;
        _config = config;
        _environment = environment;
        _logger = logger;
        _relay = relay;
    }

    /// <inheritdoc />
    public bool Enabled => Mode() switch
    {
        PluginConfiguration.RelayMode => _relay is not null,
        PluginConfiguration.DirectMode => Credentials() is not null,
        _ => false
    };

    /// <summary>Gets the relay client, when there is one.</summary>
    public RelayClient? Relay => _relay;

    /// <summary>Gets the mode in force: <c>relay</c>, <c>direct</c> or <c>off</c>.</summary>
    /// <returns>The mode.</returns>
    public string Mode() => _config().ResolvePushMode(_environment);

    /// <summary>Tells where the key comes from, whether or not notifications are enabled.</summary>
    /// <returns>The key source.</returns>
    public ApnsKeySource KeySource() => ApnsCredentialResolver.Resolve(_config(), _environment)?.Source ?? ApnsKeySource.None;

    /// <summary>Resolves the credentials when direct notifications are enabled.</summary>
    /// <returns>The credentials, or <c>null</c> when push is off, not direct, or has no key.</returns>
    public ApnsCredentials? Credentials()
    {
        var config = _config();
        return config.ApnsEnabled && Mode() == PluginConfiguration.DirectMode ? ApnsCredentialResolver.Resolve(config, _environment) : null;
    }

    /// <inheritdoc />
    public Task<bool> NotifyAsync(string userId, NotificationEvent notification, CancellationToken ct)
        => NotifyAsync(userId, new PushMessage(Title, notification.DirectBody(), notification.DirectExtra(), notification), ct);

    /// <summary>Sends a notification given as a sentence and keys (direct mode only: the relay needs the event as data).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="body">The text.</param>
    /// <param name="extra">Values the app reads.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the user is done with.</returns>
    public Task<bool> NotifyAsync(string userId, string body, IReadOnlyDictionary<string, object> extra, CancellationToken ct)
        => NotifyAsync(userId, new PushMessage(Title, body, extra), ct);

    /// <summary>Sends a message to every device of a user and removes the devices that are gone.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One result per device; empty when push is off or the user has no device for the mode.</returns>
    public async Task<IReadOnlyList<DevicePushResult>> SendAsync(string userId, PushMessage message, CancellationToken ct)
    {
        var results = new List<DevicePushResult>();
        switch (Mode())
        {
            case PluginConfiguration.DirectMode:
                var credentials = Credentials();
                if (credentials is null)
                {
                    break;
                }

                foreach (var device in _devices.For(userId).Where(d => d.Token.Length > 0))
                {
                    var result = await _sender.SendAsync(credentials, device.Token, device.Env, message, ct).ConfigureAwait(false);
                    if (result.Outcome == PushOutcome.DeviceGone)
                    {
                        _devices.Delete(device.Token, userId);
                        _logger.LogInformation("Removed a device APNs no longer knows platform={Platform} reason={Reason}", device.Platform, result.Reason);
                    }

                    results.Add(new DevicePushResult(device.Platform, device.Env, result));
                }

                break;

            case PluginConfiguration.RelayMode when _relay is not null:
                foreach (var device in _devices.For(userId).Where(d => d.IsRelay))
                {
                    var result = await _relay.SendAsync(null, device.DeviceId, device.Env, message, ct).ConfigureAwait(false);
                    if (result.Outcome == PushOutcome.DeviceGone)
                    {
                        _devices.DeleteByDeviceId(device.DeviceId, userId);
                        _logger.LogInformation("Removed a device the relay no longer knows platform={Platform} reason={Reason}", device.Platform, result.Reason);
                    }

                    results.Add(new DevicePushResult(device.Platform, device.Env, result));
                }

                break;
        }

        return results;
    }

    private async Task<bool> NotifyAsync(string userId, PushMessage message, CancellationToken ct)
    {
        var results = await SendAsync(userId, message, ct).ConfigureAwait(false);
        return results.Count == 0
            || results.Any(r => r.Result.Outcome == PushOutcome.Sent)
            || results.All(r => r.Result.Outcome is PushOutcome.DeviceGone or PushOutcome.Skipped);
    }
}
