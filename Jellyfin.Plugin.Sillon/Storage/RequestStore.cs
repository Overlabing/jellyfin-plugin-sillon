namespace Jellyfin.Plugin.Sillon.Storage;

/// <summary>The album already has a live request.</summary>
public sealed class ActiveRequestException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ActiveRequestException"/> class.</summary>
    public ActiveRequestException()
        : base("an active request exists for this album")
    {
    }
}

/// <summary>The requests, in memory and in <c>requests.json</c>. Every change is atomic and persisted.</summary>
public sealed class RequestStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<RequestRecord> _requests;

    /// <summary>Initializes a new instance of the <see cref="RequestStore"/> class.</summary>
    /// <param name="directory">The data folder.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    public RequestStore(string directory, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(directory, "requests.json");
        _statePath = Path.Combine(directory, "state.json");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _requests = JsonFile.Read<RequestsDocument>(_path)?.Requests ?? new List<RequestRecord>();
    }

    /// <summary>Gets the current time of the store.</summary>
    public DateTimeOffset Now => _now();

    /// <summary>Gets when the reconcile last queued the Lidarr cutoff-unmet search, from <c>state.json</c>; null when it never did.</summary>
    public DateTimeOffset? LastCutoffSearchAt
    {
        get
        {
            lock (_lock)
            {
                return JsonFile.Read<StateDocument>(_statePath)?.LastCutoffSearchAt;
            }
        }
    }

    /// <summary>Records that the cutoff-unmet search was just queued.</summary>
    public void MarkCutoffSearched()
    {
        lock (_lock)
        {
            JsonFile.Write(_statePath, new StateDocument { LastCutoffSearchAt = _now() });
        }
    }

    /// <summary>Records a new request (status requested).</summary>
    /// <param name="seed">The request; id, status and times are set here.</param>
    /// <returns>The stored request.</returns>
    /// <exception cref="ActiveRequestException">The album already has a request that is neither cancelled nor failed.</exception>
    public RequestRecord Create(RequestRecord seed)
    {
        lock (_lock)
        {
            if (_requests.Any(r => r.ForeignAlbumId == seed.ForeignAlbumId
                && r.Status is not (RequestStatus.Cancelled or RequestStatus.Failed)))
            {
                throw new ActiveRequestException();
            }

            var now = _now();
            var created = seed with
            {
                Id = (_requests.Count == 0 ? 0 : _requests.Max(r => r.Id)) + 1,
                Status = RequestStatus.Requested,
                Error = string.Empty,
                CreatedAt = now,
                UpdatedAt = now
            };
            _requests.Add(created);
            Persist();
            return created;
        }
    }

    /// <summary>Returns one request.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The request, or null.</returns>
    public RequestRecord? Get(long id)
    {
        lock (_lock)
        {
            return _requests.FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>Moves a request to a new state only if it is still in one of the from states, replacing its error text.</summary>
    /// <param name="id">The id.</param>
    /// <param name="from">The accepted current states.</param>
    /// <param name="to">The new state.</param>
    /// <param name="error">The new error text.</param>
    /// <returns><c>true</c> when the request moved; false when a concurrent change got there first.</returns>
    public bool Transition(long id, IReadOnlyCollection<string> from, string to, string error)
        => Update(id, r => from.Contains(r.Status), r => r with { Status = to, Error = error });

    /// <summary>Records an error on a live request without changing its state.</summary>
    /// <param name="id">The id.</param>
    /// <param name="error">The error text.</param>
    public void SetError(long id, string error)
        => Update(id, r => r.IsActive, r => r with { Error = error });

    /// <summary>Stores how many tracks of an available request were imported.</summary>
    /// <param name="id">The id.</param>
    /// <param name="imported">The imported tracks.</param>
    /// <param name="total">The tracks of the album.</param>
    public void SetTracks(long id, int imported, int total)
        => Update(id, r => r.Status == RequestStatus.Available, r => r with { TracksImported = imported, TracksTotal = total });

    /// <summary>Stores the Lidarr album id of a live request, which Lidarr has just been asked to search for.</summary>
    /// <param name="id">The id.</param>
    /// <param name="lidarrAlbumId">The Lidarr album id.</param>
    public void SetLidarrAlbum(long id, long lidarrAlbumId)
    {
        var now = _now();
        Update(id, r => r.IsActive, r => r with { LidarrAlbumId = lidarrAlbumId, LastSearchAt = now });
    }

    /// <summary>Records a new Lidarr search for a request the user asked to retry; a failed request goes back to requested.</summary>
    /// <param name="id">The id.</param>
    /// <param name="lidarrAlbumId">The Lidarr album id.</param>
    /// <returns><c>false</c> when the request is no longer requested, downloading or failed.</returns>
    public bool MarkRetried(long id, long lidarrAlbumId)
    {
        var now = _now();
        return Update(
            id,
            r => r.IsActive || r.Status == RequestStatus.Failed,
            r => r with
            {
                Status = r.Status == RequestStatus.Failed ? RequestStatus.Requested : r.Status,
                Error = r.Status == RequestStatus.Failed ? string.Empty : r.Error,
                LidarrAlbumId = lidarrAlbumId,
                LastSearchAt = now
            });
    }

    /// <summary>Records that the reconcile searched the album again for a live request.</summary>
    /// <param name="id">The id.</param>
    public void MarkReSearched(long id)
    {
        var now = _now();
        Update(id, r => r.IsActive, r => r with { LastSearchAt = now, LastReSearchAt = now });
    }

    /// <summary>Stores the Jellyfin album of an available request.</summary>
    /// <param name="id">The id.</param>
    /// <param name="itemId">The Jellyfin item id.</param>
    public void SetJellyfinItem(long id, string itemId)
        => Update(id, r => r.Status == RequestStatus.Available, r => r with { JellyfinItemId = itemId });

    /// <summary>Stores the album folder, as Lidarr sees it, of an available request.</summary>
    /// <param name="id">The id.</param>
    /// <param name="folder">The folder.</param>
    public void SetLidarrFolder(long id, string folder)
        => UpdateKeepingTime(id, r => r.Status == RequestStatus.Available, r => r with { LidarrFolder = folder });

    /// <summary>Records that the library was asked to scan the album folder of an available request.</summary>
    /// <param name="id">The id.</param>
    public void MarkFolderRefreshed(long id)
    {
        var now = _now();
        UpdateKeepingTime(id, r => r.Status == RequestStatus.Available, r => r with { LastFolderRefreshAt = now });
    }

    /// <summary>Records that the album was marked favourite for the requester.</summary>
    /// <param name="id">The id.</param>
    public void MarkLibraryAdded(long id)
    {
        var now = _now();
        UpdateKeepingTime(id, r => r.LibraryAddedAt is null, r => r with { LibraryAddedAt = now });
    }

    /// <summary>Records that the requester was told the album is available.</summary>
    /// <param name="id">The id.</param>
    public void MarkNotified(long id)
    {
        var now = _now();
        UpdateKeepingTime(id, r => r.NotifiedAt is null, r => r with { NotifiedAt = now });
    }

    /// <summary>Lists a user's requests, or everyone's when the user is null, newest first.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The requests.</returns>
    public IReadOnlyList<RequestRecord> List(string? userId)
    {
        lock (_lock)
        {
            return _requests.Where(r => userId is null || r.UserId == userId).OrderByDescending(r => r.Id).ToList();
        }
    }

    /// <summary>Lists the requests in any of the given states, oldest first.</summary>
    /// <param name="statuses">The states.</param>
    /// <returns>The requests.</returns>
    public IReadOnlyList<RequestRecord> ListByStatus(params string[] statuses)
    {
        lock (_lock)
        {
            return _requests.Where(r => statuses.Contains(r.Status)).OrderBy(r => r.Id).ToList();
        }
    }

    /// <summary>Lists the available requests the requester was never told about or whose album never reached the requester's library, touched since the given time.</summary>
    /// <param name="since">Older requests are given up on.</param>
    /// <returns>The requests.</returns>
    public IReadOnlyList<RequestRecord> ListUnsettled(DateTimeOffset since)
    {
        lock (_lock)
        {
            return _requests
                .Where(r => r.Status == RequestStatus.Available && (r.NotifiedAt is null || r.LibraryAddedAt is null) && r.UpdatedAt >= since)
                .OrderBy(r => r.Id)
                .ToList();
        }
    }

    /// <summary>Returns the requested or downloading request of an album.</summary>
    /// <param name="foreignAlbumId">The MusicBrainz album id.</param>
    /// <returns>The newest such request, or null.</returns>
    public RequestRecord? ActiveByForeignAlbum(string foreignAlbumId)
    {
        lock (_lock)
        {
            return _requests.Where(r => r.ForeignAlbumId == foreignAlbumId && r.IsActive).OrderByDescending(r => r.Id).FirstOrDefault();
        }
    }

    /// <summary>Returns, per album, its newest request; a cancelled one hides the album.</summary>
    /// <param name="foreignAlbumIds">The MusicBrainz album ids.</param>
    /// <returns>The requests by album id.</returns>
    public IReadOnlyDictionary<string, RequestRecord> Newest(IEnumerable<string> foreignAlbumIds)
    {
        var wanted = foreignAlbumIds.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, RequestRecord>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var r in _requests.Where(r => wanted.Contains(r.ForeignAlbumId)).OrderBy(r => r.Id))
            {
                if (r.Status == RequestStatus.Cancelled)
                {
                    result.Remove(r.ForeignAlbumId);
                }
                else
                {
                    result[r.ForeignAlbumId] = r;
                }
            }
        }

        return result;
    }

    private bool Update(long id, Func<RequestRecord, bool> guard, Func<RequestRecord, RequestRecord> change)
    {
        var now = _now();
        return UpdateKeepingTime(id, guard, r => change(r) with { UpdatedAt = now });
    }

    private bool UpdateKeepingTime(long id, Func<RequestRecord, bool> guard, Func<RequestRecord, RequestRecord> change)
    {
        lock (_lock)
        {
            var index = _requests.FindIndex(r => r.Id == id);
            if (index < 0 || !guard(_requests[index]))
            {
                return false;
            }

            _requests[index] = change(_requests[index]);
            Persist();
            return true;
        }
    }

    private void Persist() => JsonFile.Write(_path, new RequestsDocument { Requests = _requests });

    private sealed class StateDocument
    {
        public DateTimeOffset? LastCutoffSearchAt { get; init; }
    }

    private sealed class RequestsDocument
    {
        public List<RequestRecord> Requests { get; init; } = new();
    }
}
