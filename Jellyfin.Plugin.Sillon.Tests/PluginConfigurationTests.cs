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
        Assert.Equal(string.Empty, config.LidarrUrl);
        Assert.Equal(string.Empty, config.ApnsTeamId);
        Assert.False(config.ApnsEnabled);
        Assert.True(config.ExposeToApps);
        Assert.Equal(string.Empty, config.WebhookSecret);
    }

    [Fact]
    public void AllowedReleaseGroups_IsEmptyByDefault()
    {
        Assert.Empty(new PluginConfiguration().AllowedReleaseGroups());
    }

    [Fact]
    public void AllowedReleaseGroups_IgnoresBlankLinesAndComments_TrimsAndIgnoresCase()
    {
        var config = new PluginConfiguration { CatalogAllowList = "# demo\r\n\r\n  AAA-1  \n   \n#bbb-2\nccc-3 # not a comment\r\naaa-1\n" };

        var allowed = config.AllowedReleaseGroups();

        Assert.Equal(2, allowed.Count);
        Assert.Contains("aaa-1", allowed);
        Assert.Contains("CCC-3 # NOT A COMMENT", allowed);
        Assert.DoesNotContain("bbb-2", allowed);
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
        Assert.Equal(string.Empty, config.LidarrPathPrefix);
        Assert.Equal(string.Empty, config.JellyfinPathPrefix);
        Assert.Equal(0, config.MaxActiveRequestsPerUser);
        Assert.Equal(0, config.MaxRequestsPerUserPerDay);
        Assert.True(config.ExemptAdminsFromLimits);
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

    [Fact]
    public void LidarrApiKey_IsNeverInTheJson_AndKeptWhenTheMaskComesBack()
    {
        var stored = new PluginConfiguration { LidarrApiKey = "real-key" };

        var json = System.Text.Json.JsonSerializer.Serialize(stored);

        Assert.DoesNotContain("real-key", json, StringComparison.Ordinal);
        Assert.Contains("\"LidarrApiKey\":\"***\"", json, StringComparison.Ordinal);

        var posted = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>("{\"LidarrApiKey\":\"***\",\"LidarrUrl\":\"http://l\"}")!;
        posted.CarryOverSecrets(stored);
        Assert.Equal("real-key", posted.LidarrApiKey);

        var replaced = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>("{\"LidarrApiKey\":\" new-key \"}")!;
        replaced.CarryOverSecrets(stored);
        Assert.Equal("new-key", replaced.LidarrApiKey);

        var cleared = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>("{\"LidarrApiKey\":\"\"}")!;
        cleared.CarryOverSecrets(stored);
        Assert.Equal(string.Empty, cleared.LidarrApiKey);
    }

    [Fact]
    public void LidarrApiKey_StaysUnderItsOldElementInTheXmlFile()
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { LidarrApiKey = "real-key" });

        Assert.Contains("<LidarrApiKey>real-key</LidarrApiKey>", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ClampsNumbersAndTrimsText()
    {
        var config = new PluginConfiguration
        {
            RequestTimeoutDays = 0,
            MaxActiveRequestsPerUser = -3,
            MaxRequestsPerUserPerDay = 99999,
            LidarrUrl = "  http://lidarr:8686/ ",
            ApnsTeamId = " ABC "
        };

        config.Sanitize();

        Assert.Equal(1, config.RequestTimeoutDays);
        Assert.Equal(0, config.MaxActiveRequestsPerUser);
        Assert.Equal(1000, config.MaxRequestsPerUserPerDay);
        Assert.Equal("http://lidarr:8686", config.LidarrUrl);
        Assert.Equal("ABC", config.ApnsTeamId);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("http://lidarr:8686", true)]
    [InlineData("https://lidarr.example.com/base", true)]
    [InlineData("lidarr:8686", false)]
    [InlineData("ftp://lidarr", false)]
    [InlineData("javascript:alert(1)", false)]
    public void IsValidLidarrUrl_AcceptsOnlyHttpUrls(string url, bool expected)
    {
        Assert.Equal(expected, PluginConfiguration.IsValidLidarrUrl(url));
    }
}
