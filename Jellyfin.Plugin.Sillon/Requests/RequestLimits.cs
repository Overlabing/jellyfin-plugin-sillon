using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Storage;

namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>A user over the request limits the administrator set.</summary>
public sealed class RequestLimitException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestLimitException"/> class.</summary>
    /// <param name="message">What limit was reached.</param>
    public RequestLimitException(string message)
        : base(message)
    {
    }
}

/// <summary>Applies the per-user request limits.</summary>
public static class RequestLimits
{
    /// <summary>The window of the daily limit.</summary>
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);

    /// <summary>Checks a new request against the limits.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="store">The request store.</param>
    /// <param name="userId">The requester.</param>
    /// <param name="isAdmin">Whether the requester is an administrator.</param>
    /// <param name="now">The current time.</param>
    /// <exception cref="RequestLimitException">A limit is reached.</exception>
    public static void Check(PluginConfiguration config, RequestStore store, string userId, bool isAdmin, DateTimeOffset now)
    {
        if (isAdmin && config.ExemptAdminsFromLimits)
        {
            return;
        }

        if (config.MaxActiveRequestsPerUser > 0 && store.CountActive(userId) >= config.MaxActiveRequestsPerUser)
        {
            throw new RequestLimitException($"You already have {config.MaxActiveRequestsPerUser} requests in progress. Wait for one to finish or cancel it.");
        }

        if (config.MaxRequestsPerUserPerDay > 0 && store.CountCreatedSince(userId, now - Day) >= config.MaxRequestsPerUserPerDay)
        {
            throw new RequestLimitException($"You can make {config.MaxRequestsPerUserPerDay} requests per day. Try again later.");
        }
    }
}
