using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public class AdminPageWordingTests
{
    private static string Page()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Sillon.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("Automatic downloads")]
    [InlineData("cloud_download")]
    [InlineData("download sources")]
    [InlineData("'Downloading'")]
    [InlineData("downloading: 'downloading'")]
    [InlineData("waiting or downloading")]
    [InlineData("Search again")]
    [InlineData("Searching again")]
    [InlineData("not found yet")]
    [InlineData("keeps searching")]
    [InlineData("no release found")]
    [InlineData("looks for an album")]
    [InlineData("stops looking")]
    [InlineData("could not get")]
    public void ThePage_DropsTheOldWording(string old)
    {
        Assert.DoesNotContain(old, Page(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lidarrTitle: 'Automatic additions'")]
    [InlineData("statusCard('library_add', 'Automatic additions',")]
    [InlineData("stat(req.downloading || 0, 'Pending', 'downloading')")]
    [InlineData("downloading: 'Pending'")]
    [InlineData("Singles may not be available")]
    [InlineData("can have pending.")]
    [InlineData("button('Try again',")]
    [InlineData("'Trying again.'")]
    [InlineData("Sillon keeps trying")]
    [InlineData("when Sillon asks Lidarr to add an album")]
    [InlineData("' not available. You can retry")]
    public void ThePage_UsesTheNewWording(string text)
    {
        Assert.Contains(text, Page(), StringComparison.Ordinal);
    }

    // the labels are the only thing that moved: what the page sends, reads and matches stays, and Lidarr's own label stays as Lidarr shows it
    [Theory]
    [InlineData("r.status === 'downloading'")]
    [InlineData("req.downloading")]
    [InlineData("id=\"NotFoundYetAfterDays\"")]
    [InlineData("config.NotFoundYetAfterDays")]
    [InlineData("'Sillon/v1/requests/' + id + '/retry'")]
    [InlineData("\"On Grab\", \"On Release Import\" and \"On Import Failure\"")]
    public void ThePage_KeepsWireValuesKeysAndLidarrLabels(string text)
    {
        Assert.Contains(text, Page(), StringComparison.Ordinal);
    }
}
