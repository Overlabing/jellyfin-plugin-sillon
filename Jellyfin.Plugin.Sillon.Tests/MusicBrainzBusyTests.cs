using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Lidarr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>What a 503 from MusicBrainz does: tried again politely, never remembered as an empty answer, and one warning per search.</summary>
public class MusicBrainzBusyTests
{
    private const string PabloHoney = "11111111-0000-0000-0000-000000000001";
    private const string CrazySexyCool = "11111111-0000-0000-0000-000000000002";
    private const string Obscure = "11111111-0000-0000-0000-000000000003";

    private static readonly TrackQuery Plain = new("Creep", null);

    private static string Recording(string artist, string groupId) => $$$"""
        {"id":"r-{{{artist}}}","score":100,"title":"Creep","artist-credit":[{"name":"{{{artist}}}","artist":{"id":"id-{{{artist}}}","name":"{{{artist}}}"}}],
          "releases":[{"id":"x","title":"A","date":"1993","release-group":{"id":"{{{groupId}}}","title":"A","primary-type":"Album"}}]}
        """;

    private static string Body(string artist, string groupId) => $$$"""{"recordings":[{{{Recording(artist, groupId)}}}]}""";

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Declined(TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        }

        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _lock = new();
        private int _inFlight;

        public Clock? Clock { get; set; }

        public List<string> Queries { get; } = new();

        public List<DateTimeOffset> Starts { get; } = new();

        public int MostInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int number;
            lock (_lock)
            {
                Queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
                number = Queries.Count;
                Starts.Add(Clock?.Now ?? default);
                MostInFlight = Math.Max(MostInFlight, ++_inFlight);
            }

            await Task.Yield();
            lock (_lock)
            {
                _inFlight--;
            }

            return respond(request, number);
        }
    }

    private sealed class Clock
    {
        private readonly object _lock = new();
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now
        {
            get
            {
                lock (_lock)
                {
                    return _now;
                }
            }
        }

        public List<TimeSpan> Waits { get; } = new();

        /// <summary>Gets or sets a value indicating whether a wait is cut short as if the caller ran out of time, no time passing.</summary>
        public bool CutShort { get; set; }

        public void Advance(TimeSpan span)
        {
            lock (_lock)
            {
                _now += span;
            }
        }

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            if (CutShort)
            {
                throw new OperationCanceledException();
            }

            lock (_lock)
            {
                Waits.Add(span);
                _now += span;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Warnings<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                lock (Lines)
                {
                    Lines.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class Hints(params string[] artists) : ITrackHints
    {
        public Task<IReadOnlyList<string>> ArtistsForTitleAsync(string title, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artists);
    }

    private static (MusicBrainzClient Client, Handler Handler, Clock Clock, Warnings<MusicBrainzClient> Log) Make(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var clock = new Clock();
        var handler = new Handler(respond) { Clock = clock };
        var log = new Warnings<MusicBrainzClient>();
        return (new MusicBrainzClient(new HttpClient(handler), "1.0.0.0", log, () => clock.Now, clock.Delay, "https://mb.test"), handler, clock, log);
    }

    private static (CatalogService Catalog, TestEnv Env, Warnings<CatalogService> Log) Catalog(IMusicBrainzApi musicBrainz, ITrackHints? hints, TimeSpan? budget = null)
    {
        var env = new TestEnv();
        env.Config.DeezerPopularityHints = hints is not null;
        env.Lidarr.OnLookup = term => term.StartsWith("lidarr:", StringComparison.Ordinal)
            ? new[] { new LidarrAlbum { ForeignAlbumId = term["lidarr:".Length..], Title = "A", AlbumType = "Album", Artist = new LidarrArtist { ArtistName = "x", ForeignArtistId = "y" } } }
            : Array.Empty<LidarrAlbum>();
        var log = new Warnings<CatalogService>();
        return (new CatalogService(env.Lidarr, env.Store, env.Library, () => env.Config, log, musicBrainz, hints: hints, searchDeadline: budget), env, log);
    }

    private static async Task<IReadOnlyList<string>> Ids(CatalogService catalog, string query)
        => (await catalog.SearchAsync(query, CancellationToken.None)).Albums.Select(a => a.Mbid).ToList();

    // ---- the client

    [Fact]
    public async Task A503ThenSuccess_IsTriedAgainAfterABackoff_AndTheAnswerKept()
    {
        var (client, handler, clock, log) = Make((_, n) => n == 1 ? Declined() : Ok(Body("Radiohead", PabloHoney)));

        var found = await client.SearchRecordingsAsync(Plain, CancellationToken.None);

        Assert.Equal("Radiohead", Assert.Single(found).Artist);
        Assert.Equal(2, handler.Queries.Count);
        Assert.True(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromMilliseconds(1200));
        Assert.Empty(log.Lines);

        clock.Advance(TimeSpan.FromHours(1));
        await client.SearchRecordingsAsync(Plain, CancellationToken.None);
        Assert.Equal(2, handler.Queries.Count);
    }

    [Fact]
    public async Task A503WithRetryAfter_WaitsThatLong()
    {
        var (client, handler, _, _) = Make((_, n) => n == 1 ? Declined(TimeSpan.FromSeconds(4)) : Ok(Body("Radiohead", PabloHoney)));

        await client.SearchRecordingsAsync(Plain, CancellationToken.None);

        Assert.True(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task A503WithRetryAfterZero_AsMusicBrainzSends_StillBacksOff_AndTheWarningNamesTheLimit()
    {
        var (client, handler, _, log) = Make((_, _) =>
        {
            var response = Declined(TimeSpan.Zero);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Who", "search-shed");
            return response;
        });

        Assert.Null(await client.GetReleasesAsync("group", CancellationToken.None));

        Assert.True(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromMilliseconds(1200));
        Assert.True(handler.Starts[2] - handler.Starts[1] >= TimeSpan.FromMilliseconds(2400));
        Assert.Equal("MusicBrainz request failed: MusicBrainz answered 503 (search-shed)", Assert.Single(log.Lines));
    }

    [Fact]
    public async Task A503WithARetryAfterDate_WaitsUntilThen()
    {
        Clock? at = null;
        var (client, handler, clock, _) = Make((_, n) =>
        {
            if (n > 1)
            {
                return Ok(Body("Radiohead", PabloHoney));
            }

            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(at!.Now.AddSeconds(4));
            return response;
        });
        at = clock;

        await client.SearchRecordingsAsync(Plain, CancellationToken.None);

        Assert.True(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task ALongRetryAfter_IsNotWaitedForInline_AndHoldsBackEveryQuestionUntilThen()
    {
        var (client, handler, clock, _) = Make((_, n) => n == 1 ? Declined(TimeSpan.FromSeconds(60)) : Ok(Body("Radiohead", PabloHoney)));

        var first = await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.True(first.Busy);
        Assert.Empty(clock.Waits);

        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchRecordingsAsync(new TrackQuery("Other", null), CancellationToken.None));
        Assert.Single(handler.Queries);

        // what is left of it is short enough to wait for
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Single(await client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.True(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task AllAttemptsFail_ThenALaterSearchSucceeds()
    {
        var down = true;
        var (client, handler, clock, _) = Make((_, _) => down ? Declined() : Ok(Body("Radiohead", PabloHoney)));

        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.Equal(3, handler.Queries.Count);

        // nothing was remembered against the question; a brief pause leaves MusicBrainz alone first
        down = false;
        Assert.Single(await client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.Equal(4, handler.Queries.Count);
        Assert.True(handler.Starts[3] - handler.Starts[2] >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AFailureIsNotAnEmptyAnswer_ButARealEmptyAnswerIsKept()
    {
        var (client, handler, clock, _) = Make((_, _) => Ok("""{"recordings":[]}"""));
        Assert.Empty(await client.SearchRecordingsAsync(Plain, CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.Single(handler.Queries);

        var (failing, _, _, _) = Make((_, _) => Declined());
        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => failing.SearchRecordingsAsync(Plain, CancellationToken.None));
    }

    [Fact]
    public async Task AnotherFailure_IsNotRetried_AndKeptForSecondsOnly()
    {
        var down = true;
        var (client, handler, clock, _) = Make((_, _) => down ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Ok(Body("Radiohead", PabloHoney)));

        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchRecordingsAsync(Plain, CancellationToken.None));
        await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.Single(handler.Queries);

        down = false;
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Single(await client.SearchRecordingsAsync(Plain, CancellationToken.None));
    }

    [Fact]
    public async Task ACallerOutOfTimeDuringABackoff_StillLeavesTheWaitOwed()
    {
        var (client, handler, clock, _) = Make((_, n) => n == 1 ? Declined(TimeSpan.FromSeconds(4)) : Ok(Body("Radiohead", PabloHoney)));
        clock.CutShort = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.SearchRecordingsAsync(Plain, CancellationToken.None));
        clock.CutShort = false;

        Assert.Single(await client.SearchRecordingsAsync(Plain, CancellationToken.None));
        Assert.True(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Pacing_HoldsAcrossConcurrentQueries_RetriesIncluded()
    {
        var (client, handler, _, _) = Make((_, n) => n % 3 == 1 ? Declined() : Ok("""{"recordings":[]}"""));

        await Task.WhenAll(new[] { "One", "Two", "Three", "Four", "Five" }.Select(t => Task.Run(() => client.SearchRecordingsAsync(new TrackQuery(t, t == "Two" ? "Someone" : null, t == "Two"), CancellationToken.None))));

        Assert.Equal(1, handler.MostInFlight);
        Assert.True(handler.Queries.Count > 5);
        Assert.All(handler.Starts.Zip(handler.Starts.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1), pair.ToString()));
    }

    [Fact]
    public async Task AFailedTracklistStaysNull_WithOneWarningAfterTheRetries()
    {
        var (client, handler, _, log) = Make((_, _) => Declined());

        Assert.Null(await client.GetTracklistAsync("rel", CancellationToken.None));

        Assert.Equal(3, handler.Queries.Count);
        Assert.Single(log.Lines);
    }

    // ---- the search

    private static Func<HttpRequestMessage, int, HttpResponseMessage> Creep(Func<string, bool> declines) => (request, _) =>
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        if (declines(query))
        {
            return Declined();
        }

        var found = new List<string>();
        if (query.Contains("Radiohead", StringComparison.Ordinal))
        {
            found.Add(Recording("Radiohead", PabloHoney));
        }

        if (query.Contains("TLC", StringComparison.Ordinal))
        {
            found.Add(Recording("TLC", CrazySexyCool));
        }

        return Ok($$$"""{"recordings":[{{{(found.Count > 0 ? string.Join(',', found) : Recording("Someone", Obscure))}}}]}""");
    };

    [Fact]
    public async Task Search_TheQuestionAboutTheHintsDeclined_AsksNothingMore_WithOneWarning_AndTheNextSearchHasThem()
    {
        var down = true;
        var (client, handler, clock, _) = Make(Creep(q => down && q.Contains("artist:", StringComparison.Ordinal)));
        var (catalog, env, log) = Catalog(client, new Hints("Radiohead", "TLC"));
        using var _ = env;

        Assert.Empty(await Ids(catalog, "Creep"));

        // its three tries, one question for both artists, and no plain question either: MusicBrainz asked to slow down
        Assert.Equal(3, handler.Queries.Count);
        Assert.Contains("503", Assert.Single(log.Lines), StringComparison.Ordinal);
        Assert.DoesNotContain("Creep", log.Lines[0], StringComparison.OrdinalIgnoreCase);

        down = false;
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(new[] { PabloHoney, CrazySexyCool }, await Ids(catalog, "Creep"));
        Assert.Single(log.Lines);
    }

    [Fact]
    public async Task Search_AHintedAnswer_MakesThePlainQuestionUnnecessary()
    {
        var (client, handler, _, _) = Make(Creep(q => !q.Contains("artist:", StringComparison.Ordinal)));
        var (catalog, env, log) = Catalog(client, new Hints("Radiohead"));
        using var _ = env;

        Assert.Equal(new[] { PabloHoney }, await Ids(catalog, "Creep"));

        Assert.Single(handler.Queries);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task Search_RetriedOnce_GivesTheFullOrder_WithoutAWarning()
    {
        var declined = new HashSet<string>();
        var (client, _, _, _) = Make(Creep(q => q.Contains("Radiohead", StringComparison.Ordinal) && declined.Add(q)));
        var (catalog, env, log) = Catalog(client, new Hints("Radiohead", "TLC"));
        using var _ = env;

        Assert.Equal(new[] { PabloHoney, CrazySexyCool }, await Ids(catalog, "Creep"));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task Search_WithoutHints_WarnsOnce_AndStillAnswers()
    {
        var (client, _, _, _) = Make((_, _) => Declined());
        var (catalog, env, log) = Catalog(client, null);
        using var _ = env;

        Assert.Empty(await Ids(catalog, "Creep"));
        Assert.Single(log.Lines);
    }

    [Fact]
    public async Task Search_PastTheDeadline_SaysSo_AndKeepsWhatItHas()
    {
        var mb = new StuckOnArtists();
        var (catalog, env, log) = Catalog(mb, new Hints("Radiohead"), TimeSpan.FromMilliseconds(50));
        using var _ = env;

        Assert.Empty(await Ids(catalog, "Creep"));
        Assert.Contains("no answer in time", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    private sealed class StuckOnArtists : IMusicBrainzApi
    {
        public Task<MbReleaseGroupPage> BrowseReleaseGroupsAsync(string artistId, IReadOnlyList<string> types, int offset, CancellationToken ct)
            => Task.FromResult(new MbReleaseGroupPage(Array.Empty<MbReleaseGroup>(), 0));

        public async Task<IReadOnlyList<MbRecording>> SearchRecordingsAsync(TrackQuery query, CancellationToken ct)
        {
            if (query.Artist is not null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Array.Empty<MbRecording>();
        }

        public Task<IReadOnlyList<MbRelease>?> GetReleasesAsync(string releaseGroupId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MbRelease>?>(null);

        public Task<MbTracklist?> GetTracklistAsync(string releaseId, CancellationToken ct) => Task.FromResult<MbTracklist?>(null);
    }
}
