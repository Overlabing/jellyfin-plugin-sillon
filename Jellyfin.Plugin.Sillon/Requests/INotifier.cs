using Jellyfin.Plugin.Sillon.Push;

namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>Tells a requester about the outcome of a request.</summary>
public interface INotifier
{
    /// <summary>Gets a value indicating whether notifications actually reach devices.</summary>
    bool Enabled { get; }

    /// <summary>Sends a notification to every device of a user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="notification">What happened, as data; each mode renders it its own way.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the user is done with; false only after a transient failure worth retrying.</returns>
    Task<bool> NotifyAsync(string userId, NotificationEvent notification, CancellationToken ct);
}

/// <summary>Notifier that sends nothing.</summary>
public sealed class NoopNotifier : INotifier
{
    /// <inheritdoc />
    public bool Enabled => false;

    /// <inheritdoc />
    public Task<bool> NotifyAsync(string userId, NotificationEvent notification, CancellationToken ct)
        => Task.FromResult(true);
}
