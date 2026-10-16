using Jellyfin.Plugin.Sillon.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void Defaults_AreAsSpecified()
    {
        var config = new PluginConfiguration();

        Assert.Equal(FulfillmentMode.Manual, config.FulfillmentMode);
        Assert.Equal("http://lidarr:8686", config.LidarrUrl);
        Assert.False(config.ApnsEnabled);
        Assert.Equal(string.Empty, config.WebhookSecret);
    }

    [Fact]
    public void EnsureWebhookSecret_GeneratesA32CharToken_WhenEmpty()
    {
        var config = new PluginConfiguration();

        Assert.True(config.EnsureWebhookSecret());

        Assert.Equal(32, config.WebhookSecret.Length);
        Assert.All(config.WebhookSecret, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void EnsureWebhookSecret_KeepsAnExistingSecret()
    {
        var config = new PluginConfiguration { WebhookSecret = "already-set" };

        Assert.False(config.EnsureWebhookSecret());

        Assert.Equal("already-set", config.WebhookSecret);
    }

    [Fact]
    public void GenerateSecret_IsRandom()
    {
        Assert.NotEqual(PluginConfiguration.GenerateSecret(), PluginConfiguration.GenerateSecret());
    }

    [Fact]
    public void NewDefaults_AreAsSpecified()
    {
        var config = new PluginConfiguration();

        Assert.Equal(14, config.RequestTimeoutDays);
        Assert.Equal("/data/media/music", config.LidarrPathPrefix);
        Assert.Equal("/data/music", config.JellyfinPathPrefix);
    }

    [Theory]
    [InlineData(FulfillmentMode.Manual, "http://lidarr:8686", "", true)]
    [InlineData(FulfillmentMode.Lidarr, "http://lidarr:8686", "key", true)]
    [InlineData(FulfillmentMode.Lidarr, "", "key", false)]
    [InlineData(FulfillmentMode.Lidarr, "http://lidarr:8686", " ", false)]
    public void RequestsEnabled_DependsOnTheModeAndTheLidarrSetup(FulfillmentMode mode, string url, string key, bool expected)
    {
        var config = new PluginConfiguration { FulfillmentMode = mode, LidarrUrl = url, LidarrApiKey = key };

        Assert.Equal(expected, config.RequestsEnabled());
    }
}
