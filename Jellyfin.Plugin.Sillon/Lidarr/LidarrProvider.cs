using Jellyfin.Plugin.Sillon.Configuration;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.Sillon.Lidarr;

/// <summary>Builds a Lidarr client from the saved configuration.</summary>
public interface ILidarrProvider
{
    /// <summary>Creates a client for the current configuration.</summary>
    /// <returns>The client.</returns>
    ILidarrApi Create();
}

/// <summary>Builds <see cref="LidarrClient"/> instances over Jellyfin's HTTP client factory.</summary>
public sealed class LidarrProvider : ILidarrProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>Initializes a new instance of the <see cref="LidarrProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="config">The configuration accessor.</param>
    public LidarrProvider(IHttpClientFactory httpClientFactory, Func<PluginConfiguration> config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    /// <inheritdoc />
    public ILidarrApi Create()
    {
        var config = _config();
        return new LidarrClient(_httpClientFactory.CreateClient(NamedClient.Default), config.LidarrUrl, config.LidarrApiKey);
    }
}
