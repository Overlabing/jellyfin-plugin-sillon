using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Lidarr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>The allow list is absolute, and the disc count follows the edition an album is known by. Fixtures are trimmed answers of a real Lidarr.</summary>
public class CatalogAllowListTests
{
    private const string Directionless = "ce0eb31a-142a-4ac8-9cb9-6a3adb3ad19e";
    private const string LostAndDirectionless = "615e8bed-bc67-49ef-a4d0-7d9c27721f5b";
    private const string PabloHoney = "cd76f76b-ff15-3784-a71d-4da3078a6851";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static T Fixture<T>(string name)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), Json)!;

    private sealed class Hints(params string[] artists) : ITrackHints
    {
        public Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artists);
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request, Interlocked.Increment(ref _count)));
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MusicBrainzClient MusicBrainz(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        => new(new HttpClient(new Handler(respond)), "1.0.0.0", NullLogger.Instance, delay: (_, _) => Task.CompletedTask, baseUrl: "https://mb.test");

    private static string Recording(string artist, string groupId) => $$$"""
        {"recordings":[{"id":"r","score":100,"title":"Creep","artist-credit":[{"name":"{{{artist}}}"}],
          "releases":[{"id":"x","title":"A","date":"1993","release-group":{"id":"{{{groupId}}}","title":"A","primary-type":"Album"}}]}]}
        """;

    private static CatalogService Catalog(TestEnv env, IMusicBrainzApi? musicBrainz = null, ITrackHints? hints = null)
        => new(env.Lidarr, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance, musicBrainz, hints: hints);

    private static TestEnv DirectionlessEnv(string allowList = Directionless)
    {
        var env = new TestEnv();
        env.Config.CatalogAllowList = allowList;
        env.Lidarr.OnSearch = _ => Fixture<List<LidarrSearchItem>>("lidarr_search_directionless.json");
        env.Lidarr.OnLookup = term => Fixture<List<LidarrAlbum>>("lidarr_lookup_directionless.json")
            .Where(a => !term.StartsWith("lidarr:", StringComparison.Ordinal) || a.ForeignAlbumId == term["lidarr:".Length..])
            .ToList();
        return env;
    }

    // ---- the allow list

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllowList_AHintedAlbumOutsideIt_IsNeverReturned_NorLookedUp_EvenAfterARetry(bool retried)
    {
        using var env = DirectionlessEnv();
        env.Config.DeezerPopularityHints = true;
        var mb = MusicBrainz((request, n) => retried && n == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Ok(Recording(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("Radiohead", StringComparison.Ordinal) ? "Radiohead" : "Someone", PabloHoney)));
        env.Lidarr.OnLookup = term => term == "lidarr:" + PabloHoney ? Fixture<List<LidarrAlbum>>("lidarr_lookup_pablo_honey.json") : Array.Empty<LidarrAlbum>();

        var result = await Catalog(env, mb, new Hints("Radiohead")).SearchAsync("Creep", CancellationToken.None);

        Assert.DoesNotContain(result.Albums, a => a.Mbid != Directionless);
        Assert.DoesNotContain("lidarr:" + PabloHoney, env.Lidarr.LookupTerms);
    }

    [Fact]
    public async Task AllowList_AListedAlbumAmongSongHits_IsNotCutByTheOnesOutsideIt()
    {
        using var env = DirectionlessEnv();
        var outside = string.Concat(Enumerable.Range(0, 12).Select(i => $$$"""{"release-group":{"id":"out-{{{i}}}","title":"O","primary-type":"Album"}},"""));
        var body = $$$"""
            {"recordings":[{"id":"r","score":100,"title":"Directionless","artist-credit":[{"name":"Broke For Free"}],
              "releases":[{{{outside}}}{"release-group":{"id":"{{{Directionless}}}","title":"Directionless","primary-type":"EP"}}]}]}
            """;

        var result = await Catalog(env, MusicBrainz((_, _) => Ok(body))).SearchAsync("Directionless", CancellationToken.None);

        Assert.Equal(Directionless, Assert.Single(result.Albums).Mbid);
        Assert.DoesNotContain(env.Lidarr.LookupTerms, t => t.StartsWith("lidarr:out-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AllowList_Directionless_IsReturned_WithLidarrsRealAnswers_WhateverIsHidden(bool hideEps, bool withSongSearch)
    {
        using var env = DirectionlessEnv();
        env.Config.HideEps = hideEps;
        var mb = withSongSearch ? MusicBrainz((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : null;

        var result = await Catalog(env, mb).SearchAsync("Directionless", CancellationToken.None);

        var album = Assert.Single(result.Albums);
        Assert.Equal((Directionless, "Directionless", "Broke For Free", "EP", 1), (album.Mbid, album.Title, album.Artist, album.AlbumType, album.DiscCount));
    }

    [Fact]
    public async Task AllowList_Directionless_ComesThroughTheRealClient()
    {
        using var env = new TestEnv();
        env.Config.CatalogAllowList = "# demo\r\n " + Directionless.ToUpperInvariant() + " \r\n";
        var read = (string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var http = new HttpClient(new Handler((request, _) => Ok(request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? read("lidarr_search_directionless.json")
            : read("lidarr_lookup_directionless.json"))));
        var provider = new RealClient(new LidarrClient(http, "http://lidarr:8686", "lk"));
        var catalog = new CatalogService(provider, env.Store, env.Library, () => env.Config, NullLogger<CatalogService>.Instance);

        Assert.Equal(Directionless, Assert.Single((await catalog.SearchAsync("directionless", CancellationToken.None)).Albums).Mbid);
    }

    private sealed class RealClient(ILidarrApi client) : ILidarrProvider
    {
        public ILidarrApi Create() => client;
    }

    [Fact]
    public async Task AllowList_LidarrSearchDown_StillAnswersFromTheListedAlbums()
    {
        using var env = DirectionlessEnv();
        env.Lidarr.OnSearch = _ => throw new LidarrException("down");

        var result = await Catalog(env).SearchAsync("Directionless", CancellationToken.None);

        Assert.Equal(Directionless, Assert.Single(result.Albums).Mbid);
    }

    [Fact]
    public async Task AllowList_ListingAnotherAlbumOfTheAnswer_GivesThatOneOnly()
    {
        using var env = DirectionlessEnv(LostAndDirectionless);

        var result = await Catalog(env).SearchAsync("Directionless", CancellationToken.None);

        Assert.Equal(LostAndDirectionless, Assert.Single(result.Albums).Mbid);
    }

    // ---- the disc count

    private static async Task<CatalogAlbum> OnlyAlbum(IReadOnlyList<LidarrAlbum> lookup)
    {
        using var env = new TestEnv();
        env.Lidarr.OnLookup = _ => lookup;
        return Assert.Single((await Catalog(env).SearchAsync("Pablo Honey", CancellationToken.None)).Albums);
    }

    /// <summary>An album with so many releases per disc count, the largest edition monitored as Lidarr does.</summary>
    private static LidarrAlbum WithReleases(params (int Discs, int Releases)[] counts)
    {
        var largest = counts.Max(c => c.Discs);
        var releases = counts.SelectMany(c => Enumerable.Repeat($$$"""{"mediumCount":{{{c.Discs}}},"monitored":{{{(c.Discs == largest ? "true" : "false")}}}}""", c.Releases));
        return JsonSerializer.Deserialize<LidarrAlbum>($$$"""{"foreignAlbumId":"a","title":"T","albumType":"Album","releases":[{{{string.Join(',', releases)}}}]}""", Json)!;
    }

    [Fact]
    public async Task DiscCount_PabloHoney_IsOneDisc_DespiteItsDeluxeEditions()
    {
        var real = Fixture<List<LidarrAlbum>>("lidarr_lookup_pablo_honey.json");
        Assert.Equal((36, 7), (real[0].Releases!.Count, real[0].Releases!.Count(r => r.MediumCount == 2)));

        Assert.Equal(1, (await OnlyAlbum(real)).DiscCount);
    }

    [Theory]
    [InlineData(1, 1, 11, 2, 1, 0, 0)] // CrazySexyCool
    [InlineData(1, 1, 22, 2, 15, 6, 1)] // OK Computer, whose monitored edition is the 6-disc box
    [InlineData(2, 2, 49, 3, 3, 6, 4)] // The Beatles, a double album
    [InlineData(2, 2, 5, 0, 0, 0, 0)] // a double album in every edition
    [InlineData(1, 1, 1, 3, 1, 0, 0)] // a tie gives the fewer
    public async Task DiscCount_IsThatOfMostEditions_WhicheverLidarrMonitors(int expected, int discs1, int count1, int discs2, int count2, int discs3, int count3)
    {
        var counts = new[] { (discs1, count1), (discs2, count2), (discs3, count3) }.Where(c => c.Item2 > 0).ToArray();

        Assert.Equal(expected, (await OnlyAlbum(new[] { WithReleases(counts) })).DiscCount);
    }

    [Fact]
    public async Task DiscCount_UnknownCounts_AreOne()
        => Assert.Equal(1, (await OnlyAlbum(new[] { WithReleases((0, 2)) })).DiscCount);
}
