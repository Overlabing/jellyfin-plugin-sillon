using System.Globalization;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>A new album request.</summary>
/// <param name="UserId">The requester's Jellyfin user id.</param>
/// <param name="UserName">The requester's name.</param>
/// <param name="ForeignAlbumId">The MusicBrainz album id.</param>
/// <param name="ForeignArtistId">The MusicBrainz artist id.</param>
/// <param name="Title">The album title.</param>
/// <param name="Artist">The artist name.</param>
/// <param name="Year">The release year, 0 when unknown.</param>
/// <param name="CoverUrl">The cover URL.</param>
public sealed record NewRequest(
    string UserId, string UserName, string ForeignAlbumId, string ForeignArtistId, string Title, string Artist, int Year, string CoverUrl);

/// <summary>The delays of the request service.</summary>
public sealed record RequestTiming
{
    /// <summary>Gets how long a live request may sit without a Lidarr album id before the reconcile retries the add (so it does not race an add still in flight); it doubles after each failed attempt.</summary>
    public TimeSpan AddRetryAfter { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the longest wait between two retries of the Lidarr add of the same request.</summary>
    public TimeSpan AddRetryMax { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Gets how long one Lidarr add may run; it is not tied to the HTTP request that started it.</summary>
    public TimeSpan AddTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets how often Lidarr is asked whether it has finished refreshing a new album.</summary>
    public TimeSpan RefreshPollEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets how long the plugin waits for that refresh before it searches anyway.</summary>
    public TimeSpan RefreshWaitMax { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Gets how long a live request may go without a grab, since its last search, before the reconcile searches again.</summary>
    public TimeSpan ReSearchAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the minimum time between two automatic searches of the same request.</summary>
    public TimeSpan ReSearchEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Gets how often the library is polled for a new album.</summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets how long the library is polled after an import.</summary>
    public TimeSpan PollMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the poll budget of a catch-up by the reconcile.</summary>
    public TimeSpan ResumePollMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long after a webhook that left a request live the reconcile runs again; negative is off.</summary>
    public TimeSpan RecheckDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the minimum time between two scans of the same album folder while the library does not show the album.</summary>
    public TimeSpan FolderRefreshEvery { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the minimum time between two Lidarr cutoff-unmet searches.</summary>
    public TimeSpan CutoffSearchEvery { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Gets the minimum time between two retries of a failed import for the same request.</summary>
    public TimeSpan ImportRetryEvery { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets how long an available request that never reached its requester's library is retried.</summary>
    public TimeSpan ResumeWindow { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>The album-request state machine: it creates requests, follows them through Lidarr's webhook and a periodic reconcile, refreshes the library and adds the album to the requester's favourites.</summary>
public sealed class RequestService
{
    private const string ImportFailedMessage = "import échoué";
    private const int DefaultTimeoutDays = 14;

    private readonly RequestStore _store;
    private readonly ILidarrProvider _lidarr;
    private readonly IJellyfinLibrary _library;
    private readonly INotifier _notifier;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<RequestService> _logger;
    private readonly RequestTiming _timing;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationToken _shutdown;

    private readonly object _lock = new();
    private readonly HashSet<long> _finalizing = new();
    private readonly List<Task> _background = new();
    private readonly Dictionary<long, Task<int>> _adding = new();
    private bool _rechecking;

    /// <summary>Initializes a new instance of the <see cref="RequestService"/> class.</summary>
    /// <param name="store">The request store.</param>
    /// <param name="lidarr">The Lidarr client provider.</param>
    /// <param name="library">The Jellyfin library.</param>
    /// <param name="notifier">The notifier.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timing">The delays; the defaults when null.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    /// <param name="shutdown">Cancelled when the server stops; background work ends with it.</param>
    public RequestService(
        RequestStore store,
        ILidarrProvider lidarr,
        IJellyfinLibrary library,
        INotifier notifier,
        Func<PluginConfiguration> config,
        ILogger<RequestService> logger,
        RequestTiming? timing = null,
        Func<DateTimeOffset>? now = null,
        CancellationToken shutdown = default)
    {
        _store = store;
        _lidarr = lidarr;
        _library = library;
        _notifier = notifier;
        _config = config;
        _logger = logger;
        _timing = timing ?? new RequestTiming();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _shutdown = shutdown;
    }

    private bool ManualMode => _config().FulfillmentMode == FulfillmentMode.Manual;

    /// <summary>Waits for the background library polls to end (tests, shutdown).</summary>
    /// <returns>A task.</returns>
    public async Task WaitAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_lock)
            {
                _background.RemoveAll(t => t.IsCompleted);
                pending = _background.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <summary>Records the request and, in Lidarr mode, asks Lidarr to monitor and search the album.</summary>
    /// <param name="input">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored request.</returns>
    /// <exception cref="AlbumNotAllowedException">The catalogue allow-list is set and does not list the album.</exception>
    /// <exception cref="ActiveRequestException">The album already has a live request.</exception>
    /// <exception cref="LidarrAddException">Lidarr refused the album; the request is kept as failed so the user can ask again.</exception>
    public async Task<RequestRecord> CreateAsync(NewRequest input, CancellationToken ct)
    {
        var allowed = _config().AllowedReleaseGroups();
        if (allowed.Count > 0 && !allowed.Contains(input.ForeignAlbumId))
        {
            throw new AlbumNotAllowedException();
        }

        var created = _store.Create(new RequestRecord
        {
            UserId = input.UserId,
            UserName = input.UserName,
            ForeignAlbumId = input.ForeignAlbumId,
            ForeignArtistId = input.ForeignArtistId,
            Artist = input.Artist,
            Title = input.Title,
            Year = input.Year,
            CoverUrl = input.CoverUrl
        });

        if (ManualMode)
        {
            _logger.LogInformation("Album requested (manual) request={Id} user={User} artist={Artist} title={Title}", created.Id, created.UserName, created.Artist, created.Title);
            return created;
        }

        try
        {
            // the add runs on its own token: the client hanging up (the app's URLSession timeout) must not abort it half-way
            await StartAdd(created).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Client left before Lidarr answered, the add goes on in the background request={Id}", created.Id);
            return _store.Get(created.Id) ?? created;
        }
        catch (Exception)
        {
            _store.Transition(created.Id, RequestStatus.Live, RequestStatus.Failed, LidarrAddException.Text);
            throw new LidarrAddException();
        }

        _logger.LogInformation("Album requested request={Id} user={User} artist={Artist} title={Title}", created.Id, created.UserName, created.Artist, created.Title);
        return _store.Get(created.Id) ?? created;
    }

    /// <summary>Stops a live request: the album is unmonitored in Lidarr, not deleted.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="admin">Whether the caller is an administrator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cancelled request.</returns>
    public async Task<RequestRecord> CancelAsync(long id, string userId, bool admin, CancellationToken ct)
    {
        var request = _store.Get(id) ?? throw new RequestNotFoundException();
        if (request.UserId != userId && !admin)
        {
            throw new RequestForbiddenException();
        }

        if (!request.IsActive)
        {
            throw new RequestNotActiveException();
        }

        if (request.LidarrAlbumId is { } lidarrId)
        {
            try
            {
                await _lidarr.Create().MonitorAlbumsAsync(new[] { (int)lidarrId }, false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("Unmonitoring the album in Lidarr failed request={Id}: {Message}", id, ex.Message);
                throw new LidarrUnavailableException();
            }
        }

        // an import or a timeout may have got there first
        if (!_store.Transition(id, RequestStatus.Live, RequestStatus.Cancelled, string.Empty))
        {
            throw new RequestNotActiveException();
        }

        return _store.Get(id) ?? request;
    }

    /// <summary>Asks Lidarr to search for the album again, adding and monitoring it first if it vanished; a failed request goes back to requested.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="admin">Whether the caller is an administrator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The request.</returns>
    /// <exception cref="RequestNotRetryableException">The request is not requested, downloading or failed, or the mode is manual.</exception>
    /// <exception cref="LidarrUnavailableException">Lidarr could not be reached or refused the album.</exception>
    public async Task<RequestRecord> RetryAsync(long id, string userId, bool admin, CancellationToken ct)
    {
        var request = _store.Get(id) ?? throw new RequestNotFoundException();
        if (request.UserId != userId && !admin)
        {
            throw new RequestForbiddenException();
        }

        if (!request.IsActive && request.Status != RequestStatus.Failed)
        {
            throw new RequestNotRetryableException();
        }

        if (ManualMode)
        {
            throw new RequestNotRetryableException("Relance impossible en mode manuel : seul le mode Lidarr relance une recherche.");
        }

        int lidarrId;
        try
        {
            lidarrId = await StartAdd(request).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Client left before Lidarr answered, the retry goes on in the background request={Id}", id);
            throw;
        }
        catch (Exception)
        {
            throw new LidarrUnavailableException();
        }

        // a cancellation or an import may have got there first
        if (!_store.MarkRetried(id, lidarrId))
        {
            throw new RequestNotRetryableException();
        }

        _logger.LogInformation("Album search retried request={Id} user={User}", id, userId);
        return _store.Get(id) ?? request;
    }

    /// <summary>Marks a live request available by hand (manual mode), then runs the post-availability steps.</summary>
    /// <param name="id">The request id.</param>
    /// <returns>The available request.</returns>
    public RequestRecord MarkAvailableByHand(long id)
    {
        var request = _store.Get(id) ?? throw new RequestNotFoundException();
        if (!request.IsActive)
        {
            throw new RequestNotActiveException();
        }

        MarkAvailable(request, Array.Empty<string>(), 0, 0);
        return _store.Get(id) ?? request;
    }

    /// <summary>Marks the live requests of these albums as in progress.</summary>
    /// <param name="albumIds">The MusicBrainz album ids.</param>
    /// <returns>What happened to each album, for the webhook log.</returns>
    public IReadOnlyList<string> OnGrab(IEnumerable<string> albumIds)
    {
        var outcome = new List<string>();
        foreach (var albumId in albumIds)
        {
            var request = _store.ActiveByForeignAlbum(albumId);
            if (request is null)
            {
                outcome.Add(albumId + ": no active request");
                continue;
            }

            SetDownloading(request);
            outcome.Add($"request {request.Id}: {request.Status} -> downloading");
        }

        return outcome;
    }

    /// <summary>Handles Lidarr's import event for these albums; a partial album (some tracks missing) is still available, with the counts recorded.</summary>
    /// <param name="albumIds">The MusicBrainz album ids.</param>
    /// <param name="folders">The album folders as Lidarr sees them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What happened to each album, for the webhook log.</returns>
    public async Task<IReadOnlyList<string>> OnImportAsync(IEnumerable<string> albumIds, IReadOnlyList<string> folders, CancellationToken ct)
    {
        var outcome = new List<string>();
        foreach (var albumId in albumIds)
        {
            var request = _store.ActiveByForeignAlbum(albumId);
            if (request is null)
            {
                outcome.Add(albumId + ": no active request");
                continue;
            }

            int imported = 0, total = 0;
            if (request.LidarrAlbumId is { } lidarrId)
            {
                try
                {
                    (imported, total) = Counts(await _lidarr.Create().GetAlbumAsync((int)lidarrId, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Reading the album after an import failed request={Id}: {Message}", request.Id, ex.Message);
                }
            }

            outcome.Add(MarkAvailable(request, folders, imported, total));
        }

        RecheckLater();
        return outcome;
    }

    /// <summary>Handles an import failure. When Lidarr already holds track files for the album and nothing of it is left in the queue (the automatic import was rejected, a manual one then succeeded) the request is available, partial if tracks are missing. Otherwise the failure is recorded as a generic error; the status stays and the reconcile decides.</summary>
    /// <param name="albumIds">The MusicBrainz album ids.</param>
    /// <param name="message">Lidarr's message, logged only.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What happened to each album, for the webhook log.</returns>
    public async Task<IReadOnlyList<string>> OnFailureAsync(IEnumerable<string> albumIds, string message, CancellationToken ct)
    {
        var outcome = new List<string>();
        foreach (var albumId in albumIds)
        {
            var request = _store.ActiveByForeignAlbum(albumId);
            if (request is null)
            {
                outcome.Add(albumId + ": no active request");
                continue;
            }

            _logger.LogWarning("Lidarr reported a failure request={Id} message={Message}", request.Id, message);
            var imported = await ImportedAfterFailureAsync(request, ct).ConfigureAwait(false);
            if (imported is not null)
            {
                outcome.Add(imported);
                continue;
            }

            _store.SetError(request.Id, ImportFailedMessage);
            outcome.Add($"request {request.Id}: {request.Status}, error recorded");
        }

        RecheckLater();
        return outcome;
    }

    /// <summary>Compares live requests with Lidarr in case a webhook was lost, and catches up the available requests that did not reach their requester's library. A request still in Lidarr's queue is failed anyway once it is twice as old as the timeout. In manual mode the administrator is the one who settles requests, so nothing is retried or timed out.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        ResumeAvailable();
        if (ManualMode)
        {
            return;
        }

        await CutoffSearchAsync(ct).ConfigureAwait(false);

        var live = _store.ListByStatus(RequestStatus.Requested, RequestStatus.Downloading);
        if (live.Count == 0)
        {
            return;
        }

        var lidarr = _lidarr.Create();
        IReadOnlyList<LidarrQueueRecord> queue = Array.Empty<LidarrQueueRecord>();
        var queueRead = true;
        try
        {
            queue = await lidarr.GetQueueAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            queueRead = false;
            _logger.LogWarning("Reconcile: reading the queue failed: {Message}", ex.Message);
        }

        var queued = QueuedAlbums(queue);
        var days = _config().RequestTimeoutDays;
        var limit = TimeSpan.FromDays(days > 0 ? days : DefaultTimeoutDays);
        foreach (var listed in live)
        {
            // a webhook may have moved the request since the listing
            var request = _store.Get(listed.Id);
            if (request is null || !request.IsActive)
            {
                continue;
            }

            var age = _now() - request.CreatedAt;
            if (request.LidarrAlbumId is not { } lidarrId)
            {
                await RetryAddAsync(request, age, limit, days, ct).ConfigureAwait(false);
                continue;
            }

            LidarrAlbum album;
            try
            {
                album = await lidarr.GetAlbumAsync((int)lidarrId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Reconcile: reading the album failed request={Id}: {Message}", request.Id, ex.Message);
                continue;
            }

            var (imported, total) = Counts(album);
            var isQueued = queued.Contains((int)lidarrId);
            if (album.Statistics is { PercentOfTracks: >= 100 })
            {
                MarkAvailable(request, await AlbumFoldersAsync(lidarr, (int)lidarrId, album, ct).ConfigureAwait(false), imported, total);
            }
            else if (imported > 0 && queueRead && !isQueued)
            {
                MarkAvailable(request, await AlbumFoldersAsync(lidarr, (int)lidarrId, album, ct).ConfigureAwait(false), imported, total);
            }
            else if (age > 2 * limit)
            {
                await TimeoutAsync(request, days, ct).ConfigureAwait(false);
            }
            else if (isQueued)
            {
                SetDownloading(request);
                await RetryFailedImportAsync(request, lidarr, (int)lidarrId, queue, ct).ConfigureAwait(false);
            }
            else if (queueRead && age > limit)
            {
                await TimeoutAsync(request, days, ct).ConfigureAwait(false);
            }
            else if (album.Monitored == false || (queueRead && imported == 0))
            {
                await ReSearchAsync(request, lidarr, (int)lidarrId, ct).ConfigureAwait(false);
            }
        }
    }

    // once a week, so a better release found later (FLAC over MP3, when the quality profile allows the upgrade) replaces what was imported
    private async Task CutoffSearchAsync(CancellationToken ct)
    {
        if (_store.LastCutoffSearchAt is { } last && _now() - last < _timing.CutoffSearchEvery)
        {
            return;
        }

        try
        {
            await _lidarr.Create().CutoffUnmetSearchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Reconcile: queueing the cutoff-unmet search failed: {Message}", ex.Message);
            return;
        }

        _store.MarkCutoffSearched();
        _logger.LogInformation("Cutoff-unmet search queued in Lidarr");
    }

    // Lidarr refuses a download of another edition than the monitored release; accepting any release lets the same files import
    private async Task RetryFailedImportAsync(
        RequestRecord request, ILidarrApi lidarr, int lidarrId, IReadOnlyList<LidarrQueueRecord> queue, CancellationToken ct)
    {
        var failed = queue.FirstOrDefault(q =>
            (q.AlbumId != 0 ? q.AlbumId : q.Album?.Id ?? 0) == lidarrId
            && q.TrackedDownloadState == "importFailed"
            && !string.IsNullOrEmpty(q.OutputPath)
            && (q.StatusMessages ?? new()).Any(m => (m.Messages ?? new()).Any(
                t => t.Contains("Album release not requested", StringComparison.OrdinalIgnoreCase))));
        if (failed is null
            || (request.LastImportRetryAt is { } last && _now() - last < _timing.ImportRetryEvery))
        {
            return;
        }

        try
        {
            await lidarr.SetAnyReleaseOkAsync(lidarrId, ct).ConfigureAwait(false);
            await lidarr.DownloadedAlbumsScanAsync(failed.OutputPath!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Reconcile: retrying the import failed request={Id}: {Message}", request.Id, ex.Message);
            return;
        }

        _store.MarkImportRetried(request.Id);
        _logger.LogInformation(
            "Retrying import with any release accepted request={Id} artist={Artist} title={Title}", request.Id, request.Artist, request.Title);
    }

    // monitors the album again and queues a new search when the last one is old and the last automatic one older still
    private async Task ReSearchAsync(RequestRecord request, ILidarrApi lidarr, int lidarrId, CancellationToken ct)
    {
        var now = _now();
        if (now - (request.LastSearchAt ?? request.CreatedAt) <= _timing.ReSearchAfter
            || (request.LastReSearchAt is { } last && now - last < _timing.ReSearchEvery))
        {
            return;
        }

        try
        {
            await lidarr.MonitorAlbumsAsync(new[] { lidarrId }, true, ct).ConfigureAwait(false);
            await lidarr.AlbumSearchAsync(new[] { lidarrId }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Reconcile: searching the album again failed request={Id}: {Message}", request.Id, ex.Message);
            return;
        }

        _store.MarkReSearched(request.Id);
        _logger.LogInformation("Re-searching album in Lidarr request={Id} artist={Artist} title={Title}", request.Id, request.Artist, request.Title);
    }

    // Lidarr refreshes a new artist after the add; a search before that sees an album of duration 0 and rejects every release
    private async Task WaitForRefreshAsync(ILidarrApi lidarr, int id, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                var album = await lidarr.GetAlbumAsync(id, ct).ConfigureAwait(false);
                if (album.Duration > 0 && (album.Statistics is null || album.Statistics.TrackCount > 0))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Reading the album while waiting for Lidarr's refresh failed album={Id}: {Message}", id, ex.Message);
            }

            if (waited >= _timing.RefreshWaitMax)
            {
                _logger.LogWarning("Lidarr did not finish refreshing the album in time, searching anyway album={Id}", id);
                return;
            }

            await Task.Delay(_timing.RefreshPollEvery, ct).ConfigureAwait(false);
            waited += _timing.RefreshPollEvery;
        }
    }

    private static (int Imported, int Total) Counts(LidarrAlbum album)
        => album.Statistics is null ? (0, 0) : (album.Statistics.TrackFileCount, album.Statistics.TrackCount);

    private static HashSet<int> QueuedAlbums(IReadOnlyList<LidarrQueueRecord> queue)
    {
        var queued = new HashSet<int>();
        foreach (var record in queue)
        {
            queued.Add(record.AlbumId != 0 ? record.AlbumId : record.Album?.Id ?? 0);
        }

        return queued;
    }

    private async Task<int> AddToLidarrAsync(RequestRecord request, CancellationToken ct)
    {
        var lidarr = _lidarr.Create();
        var config = _config();
        var existing = await lidarr.GetAlbumsByForeignIdAsync(request.ForeignAlbumId, ct).ConfigureAwait(false);
        int id;
        if (existing.Count > 0)
        {
            id = existing[0].Id;
            try
            {
                await lidarr.SetAnyReleaseOkAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Accepting any release failed request={Id}: {Message}", request.Id, ex.Message);
            }
        }
        else
        {
            var added = await lidarr.AddAlbumAsync(
                new LidarrAddAlbum
                {
                    ForeignAlbumId = request.ForeignAlbumId,
                    Monitored = true,
                    AnyReleaseOk = true,
                    Artist = new LidarrAddArtist
                    {
                        ForeignArtistId = request.ForeignArtistId,
                        QualityProfileId = config.LidarrQualityProfileId,
                        MetadataProfileId = config.LidarrMetadataProfileId,
                        RootFolderPath = config.LidarrRootFolder,
                        Monitored = false,
                        MonitorNewItems = "none",
                        AddOptions = new LidarrAddArtistOptions { Monitor = "none", SearchForMissingAlbums = false }
                    },
                    AddOptions = new LidarrAddAlbumOptions { SearchForNewAlbum = false }
                },
                ct).ConfigureAwait(false);
            id = added.Id;
        }

        if (id == 0)
        {
            throw new InvalidOperationException("Lidarr returned no album id");
        }

        await WaitForRefreshAsync(lidarr, id, ct).ConfigureAwait(false);
        if (existing.Count == 0 || existing[0].Monitored != true)
        {
            await lidarr.MonitorAlbumsAsync(new[] { id }, true, ct).ConfigureAwait(false);
        }

        await lidarr.AlbumSearchAsync(new[] { id }, ct).ConfigureAwait(false);
        return id;
    }

    private void SetDownloading(RequestRecord request)
        => _store.Transition(request.Id, new[] { RequestStatus.Requested }, RequestStatus.Downloading, string.Empty);

    private async Task<string?> ImportedAfterFailureAsync(RequestRecord request, CancellationToken ct)
    {
        if (request.LidarrAlbumId is not { } lidarrId)
        {
            return null;
        }

        var lidarr = _lidarr.Create();
        try
        {
            var album = await lidarr.GetAlbumAsync((int)lidarrId, ct).ConfigureAwait(false);
            var (imported, total) = Counts(album);
            if (imported == 0)
            {
                return null;
            }

            var queue = await lidarr.GetQueueAsync(ct).ConfigureAwait(false);
            if (QueuedAlbums(queue).Contains((int)lidarrId))
            {
                return null;
            }

            return MarkAvailable(request, await AlbumFoldersAsync(lidarr, (int)lidarrId, album, ct).ConfigureAwait(false), imported, total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Reading Lidarr after a failure failed request={Id}: {Message}", request.Id, ex.Message);
            return null;
        }
    }

    // the folders (as Lidarr sees them) holding the album's track files, else the artist's folder
    private async Task<IReadOnlyList<string>> AlbumFoldersAsync(ILidarrApi lidarr, int lidarrId, LidarrAlbum album, CancellationToken ct)
    {
        var folders = new List<string>();
        try
        {
            foreach (var file in await lidarr.GetTrackFilesAsync(lidarrId, ct).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(file.Path))
                {
                    continue;
                }

                var dir = PathMap.Dir(file.Path);
                if (!folders.Contains(dir))
                {
                    folders.Add(dir);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Reading the track files failed album={Album}: {Message}", lidarrId, ex.Message);
        }

        if (folders.Count == 0 && !string.IsNullOrEmpty(album.Artist?.Path))
        {
            folders.Add(album.Artist.Path);
        }

        return folders;
    }

    private string MarkAvailable(RequestRecord request, IReadOnlyList<string> lidarrFolders, int imported, int total)
    {
        var from = request.Status;
        if (!_store.Transition(request.Id, RequestStatus.Live, RequestStatus.Available, string.Empty))
        {
            // cancelled, failed or already imported by a concurrent event
            return $"request {request.Id}: already settled";
        }

        if (imported > 0 && total > 0)
        {
            _store.SetTracks(request.Id, imported, total);
        }

        _logger.LogInformation("Album available request={Id} artist={Artist} title={Title} tracks={Imported}/{Total}", request.Id, request.Artist, request.Title, imported, total);
        ReportFolders(lidarrFolders);
        StartFinalize(request.Id, _timing.PollMax);
        return $"request {request.Id}: {from} -> available ({imported}/{total} tracks)";
    }

    private void ReportFolders(IReadOnlyList<string> lidarrFolders)
    {
        var config = _config();
        foreach (var folder in lidarrFolders.Where(f => !string.IsNullOrEmpty(f)))
        {
            var path = PathMap.Remap(PathMap.Clean(folder), config.LidarrPathPrefix, config.JellyfinPathPrefix);
            try
            {
                _library.ReportPathChanged(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Telling Jellyfin about a new folder failed: {Message}", ex.Message);
            }
        }
    }

    private void StartFinalize(long requestId, TimeSpan budget)
    {
        lock (_lock)
        {
            if (!_finalizing.Add(requestId))
            {
                return;
            }

            _background.Add(Task.Run(async () =>
            {
                try
                {
                    await FinalizeAsync(requestId, budget).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Finishing an available request failed request={Id}: {Message}", requestId, ex.Message);
                }
                finally
                {
                    lock (_lock)
                    {
                        _finalizing.Remove(requestId);
                    }
                }
            }));
        }
    }

    // Polls the library for the album, stores its id, marks it favourite for the requester and notifies them.
    // When the library never shows the album within the budget the user is still told, without a deep link.
    private async Task FinalizeAsync(long requestId, TimeSpan budget)
    {
        var request = _store.Get(requestId);
        if (request is null || request.Status != RequestStatus.Available)
        {
            return;
        }

        var itemId = request.JellyfinItemId;
        var deadline = DateTimeOffset.UtcNow + budget;
        var folder = string.IsNullOrEmpty(itemId) ? await ResolveFolderAsync(request).ConfigureAwait(false) : null;
        while (string.IsNullOrEmpty(itemId))
        {
            try
            {
                itemId = _library.FindAlbum(request.ForeignAlbumId, string.Empty, string.Empty)
                    ?? (folder is null ? null : _library.FindAlbumByPath(folder))
                    ?? _library.FindAlbum(string.Empty, request.Title, request.Artist)
                    ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Library album lookup failed request={Id}: {Message}", requestId, ex.Message);
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogWarning("The library did not show the album in time request={Id} folder={Folder}", requestId, folder ?? "unknown");
                break;
            }

            if (folder is not null)
            {
                RefreshFolder(requestId, folder);
            }

            await Task.Delay(_timing.PollEvery, _shutdown).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(itemId))
        {
            if (request.JellyfinItemId != itemId)
            {
                _store.SetJellyfinItem(requestId, itemId);
            }

            if (request.LibraryAddedAt is null && _library.MarkFavorite(request.UserId, itemId))
            {
                _store.MarkLibraryAdded(requestId);
            }
        }

        if (request.NotifiedAt is not null)
        {
            return;
        }

        var extra = new Dictionary<string, object> { ["request_id"] = requestId };
        if (!string.IsNullOrEmpty(itemId))
        {
            extra["album_id"] = itemId;
        }

        var body = $"{request.Title} de {request.Artist} est maintenant dans la bibliothèque";
        if (request.TracksTotal > 0 && request.TracksImported < request.TracksTotal)
        {
            body += string.Create(CultureInfo.InvariantCulture, $" ({request.TracksImported}/{request.TracksTotal} titres)");
        }

        if (await _notifier.NotifyAsync(request.UserId, body, extra, _shutdown).ConfigureAwait(false))
        {
            _store.MarkNotified(requestId);
        }
    }

    // the album folder as Jellyfin sees it: stored on the request, else read from Lidarr's track files and stored
    private async Task<string?> ResolveFolderAsync(RequestRecord request)
    {
        var lidarrFolder = request.LidarrFolder;
        if (string.IsNullOrEmpty(lidarrFolder) && request.LidarrAlbumId is { } albumId)
        {
            try
            {
                lidarrFolder = await _lidarr.Create().GetAlbumFolderAsync((int)albumId, _shutdown).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Reading the album folder failed request={Id}: {Message}", request.Id, ex.Message);
            }

            if (!string.IsNullOrEmpty(lidarrFolder))
            {
                _store.SetLidarrFolder(request.Id, lidarrFolder);
            }
        }

        if (string.IsNullOrEmpty(lidarrFolder))
        {
            return null;
        }

        var config = _config();
        return PathMap.Remap(PathMap.Clean(lidarrFolder), config.LidarrPathPrefix, config.JellyfinPathPrefix);
    }

    // asks the library to scan the album folder, at most once per FolderRefreshEvery for a request
    private void RefreshFolder(long requestId, string folder)
    {
        var request = _store.Get(requestId);
        if (request is null || (request.LastFolderRefreshAt is { } last && _now() - last < _timing.FolderRefreshEvery))
        {
            return;
        }

        try
        {
            _library.RefreshPath(folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Asking Jellyfin to scan the album folder failed request={Id}: {Message}", requestId, ex.Message);
            return;
        }

        _store.MarkFolderRefreshed(requestId);
        _logger.LogInformation("Asked Jellyfin to scan the album folder request={Id} folder={Folder}", requestId, folder);
    }

    // finishes the available requests whose album never reached the requester's library or whose notification never went out
    private void ResumeAvailable()
    {
        foreach (var request in _store.ListUnsettled(_store.Now - _timing.ResumeWindow))
        {
            StartFinalize(request.Id, _timing.ResumePollMax);
        }
    }

    private async Task TimeoutAsync(RequestRecord request, int days, CancellationToken ct)
    {
        if (!_store.Transition(request.Id, RequestStatus.Live, RequestStatus.Failed, $"introuvable après {days} jours"))
        {
            return;
        }

        _logger.LogInformation("Request timed out request={Id} artist={Artist} title={Title}", request.Id, request.Artist, request.Title);
        await _notifier.NotifyAsync(
            request.UserId,
            $"{request.Title} de {request.Artist} est introuvable",
            new Dictionary<string, object> { ["request_id"] = request.Id },
            ct).ConfigureAwait(false);
    }

    // re-runs, in the background, the Lidarr add of a request that never got its album id (the client hung up, Lidarr was slow or down, the plugin stopped);
    // the add is idempotent (an album Lidarr already has is reused), the wait between attempts doubles, and the request times out like any other
    private async Task RetryAddAsync(RequestRecord request, TimeSpan age, TimeSpan limit, int days, CancellationToken ct)
    {
        if (age > limit)
        {
            await TimeoutAsync(request, days, ct).ConfigureAwait(false);
            return;
        }

        if (age < _timing.AddRetryAfter)
        {
            return;
        }

        if (request.LastAddAttemptAt is { } last && _now() - last < AddBackoff(request.AddAttempts))
        {
            return;
        }

        _logger.LogInformation("Retrying the Lidarr add request={Id} attempts={Attempts}", request.Id, request.AddAttempts);
        _ = StartAdd(request);
    }

    private TimeSpan AddBackoff(int attempts)
    {
        var factor = Math.Pow(2, Math.Clamp(attempts, 0, 20));
        var ticks = Math.Min(_timing.AddRetryAfter.Ticks * factor, _timing.AddRetryMax.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    // starts (or joins) the Lidarr add of a request on a token the HTTP caller cannot cancel: the server's lifetime plus its own timeout;
    // success stores the Lidarr id, failure records the error on the request, which stays requested for the reconcile to retry
    private Task<int> StartAdd(RequestRecord request)
    {
        lock (_lock)
        {
            if (_adding.TryGetValue(request.Id, out var running))
            {
                return running;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
                    cts.CancelAfter(_timing.AddTimeout);
                    var id = await AddToLidarrAsync(request, cts.Token).ConfigureAwait(false);
                    if (request.Status == RequestStatus.Failed)
                    {
                        _store.MarkRetried(request.Id, id);
                    }
                    else
                    {
                        _store.SetLidarrAlbum(request.Id, id);
                    }

                    return id;
                }
                catch (Exception ex)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        _logger.LogInformation("The Lidarr add stopped with the server request={Id}", request.Id);
                        throw;
                    }

                    var reason = ex is OperationCanceledException ? "timed out" : ex.Message;
                    _logger.LogWarning("Adding the album to Lidarr failed request={Id}: {Message}", request.Id, reason);
                    _store.MarkAddFailed(request.Id, LidarrAddException.Text);
                    throw;
                }
                finally
                {
                    lock (_lock)
                    {
                        _adding.Remove(request.Id);
                    }
                }
            });
            _adding[request.Id] = task;
            _background.Add(task.ContinueWith(_ => { }, TaskScheduler.Default));
            return task;
        }
    }

    // runs one reconcile shortly after a webhook, for the requests it left live, unless one is already pending
    private void RecheckLater()
    {
        if (_timing.RecheckDelay < TimeSpan.Zero || _store.ListByStatus(RequestStatus.Requested, RequestStatus.Downloading).Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_rechecking)
            {
                return;
            }

            _rechecking = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_timing.RecheckDelay, _shutdown).ConfigureAwait(false);
                lock (_lock)
                {
                    _rechecking = false;
                }

                await ReconcileAsync(_shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the server is stopping
            }
            catch (Exception ex)
            {
                _logger.LogError("Reconcile after a webhook failed: {Message}", ex.Message);
            }
        });
    }
}
