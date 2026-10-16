using Jellyfin.Plugin.Sillon.Configuration;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>Where the APNs key comes from.</summary>
public enum ApnsKeySource
{
    /// <summary>No key.</summary>
    None,

    /// <summary>The server's environment variables.</summary>
    Environment,

    /// <summary>The plugin configuration.</summary>
    Config
}

/// <summary>Everything needed to sign and address an APNs request.</summary>
/// <param name="PrivateKey">PEM content of the .p8 key (literal <c>\n</c> allowed).</param>
/// <param name="KeyId">The key id.</param>
/// <param name="TeamId">The team id.</param>
/// <param name="Topic">The topic, the app's bundle id.</param>
/// <param name="Source">Where the key was found.</param>
public sealed record ApnsCredentials(string PrivateKey, string KeyId, string TeamId, string Topic, ApnsKeySource Source);

/// <summary>Finds the APNs credentials: environment variables first, then the plugin configuration.</summary>
public static class ApnsCredentialResolver
{
    /// <summary>Environment variable holding the .p8 content.</summary>
    public const string KeyVariable = "SILLON_APNS_KEY";

    /// <summary>Environment variable holding the key id.</summary>
    public const string KeyIdVariable = "SILLON_APNS_KEY_ID";

    /// <summary>Environment variable holding the team id.</summary>
    public const string TeamIdVariable = "SILLON_APNS_TEAM_ID";

    /// <summary>Environment variable holding the topic.</summary>
    public const string TopicVariable = "SILLON_APNS_TOPIC";

    /// <summary>Resolves the credentials, field by field, environment over configuration.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The credentials, or <c>null</c> when the key or its id is missing.</returns>
    public static ApnsCredentials? Resolve(PluginConfiguration config, Func<string, string?> environment)
    {
        var envKey = Clean(environment(KeyVariable));
        var key = envKey.Length > 0 ? envKey : Clean(config.ApnsPrivateKeyValue);
        var source = envKey.Length > 0 ? ApnsKeySource.Environment : key.Length > 0 ? ApnsKeySource.Config : ApnsKeySource.None;
        var keyId = First(environment(KeyIdVariable), config.ApnsKeyId);
        var teamId = First(environment(TeamIdVariable), config.ApnsTeamId, PluginConfiguration.DefaultApnsTeamId);
        var topic = First(environment(TopicVariable), config.ApnsTopic, PluginConfiguration.DefaultApnsTopic);
        return source == ApnsKeySource.None || keyId.Length == 0
            ? null
            : new ApnsCredentials(key, keyId, teamId, topic, source);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static string First(params string?[] values) => values.Select(Clean).FirstOrDefault(v => v.Length > 0) ?? string.Empty;
}
