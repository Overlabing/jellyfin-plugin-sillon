using Jellyfin.Plugin.Sillon.Storage;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>What the relay client remembers about one device.</summary>
public sealed class RelayDeviceState
{
    /// <summary>Gets or sets the last sequence number used for the device.</summary>
    public long Seq { get; set; }

    /// <summary>Gets or sets when the relay first answered "unknown device" in a row.</summary>
    public DateTimeOffset? UnknownSince { get; set; }

    /// <summary>Gets or sets the time before which the device is not tried again (its daily quota is spent).</summary>
    public DateTimeOffset? BlockedUntil { get; set; }
}

/// <summary>The settings page's view of the relay link; never content, never a key other than the public one.</summary>
/// <param name="Url">The relay URL.</param>
/// <param name="Kid">This server's key id, base64url.</param>
/// <param name="ServerPublicKey">This server's public key, base64url.</param>
/// <param name="Banned">Whether the relay banned this key.</param>
/// <param name="LastStatus">The last relay answer, short.</param>
/// <param name="LastAt">When it happened.</param>
/// <param name="Sent">Notifications the relay accepted.</param>
/// <param name="Refused">Notifications the relay refused or failed.</param>
/// <param name="QuotaHits">Times a quota stopped a notification.</param>
/// <param name="BlockedUntil">When a spent server quota ends, if one is.</param>
public sealed record RelayStatus(
    string Url,
    string Kid,
    string ServerPublicKey,
    bool Banned,
    string LastStatus,
    DateTimeOffset? LastAt,
    long Sent,
    long Refused,
    long QuotaHits,
    DateTimeOffset? BlockedUntil);

/// <summary>
/// The relay client's persistent state in <c>relay-state.json</c>: the per-device sequence number (written before every send, so
/// a crash never reuses one), quota blocks, the ban marker and the counters of the settings page.
/// </summary>
public sealed class RelayState
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Document _doc;

    /// <summary>Initializes a new instance of the <see cref="RelayState"/> class.</summary>
    /// <param name="directory">The plugin's data folder.</param>
    public RelayState(string directory)
    {
        _path = Path.Combine(directory, "relay-state.json");
        _doc = JsonFile.Read<Document>(_path) ?? new Document();
    }

    /// <summary>Takes the next sequence number for a device: <c>max(last + 1, unix milliseconds)</c>, persisted before it is used.</summary>
    /// <param name="deviceId">The device id, base64url.</param>
    /// <param name="nowMilliseconds">The clock, Unix milliseconds.</param>
    /// <returns>The number.</returns>
    public long NextSeq(string deviceId, long nowMilliseconds)
    {
        lock (_lock)
        {
            var device = Device(deviceId);
            device.Seq = Math.Max(device.Seq + 1, nowMilliseconds);
            Persist();
            return device.Seq;
        }
    }

    /// <summary>Reads a copy of a device's state.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>The state; a fresh one when unknown.</returns>
    public RelayDeviceState Get(string deviceId)
    {
        lock (_lock)
        {
            var device = Device(deviceId);
            return new RelayDeviceState { Seq = device.Seq, UnknownSince = device.UnknownSince, BlockedUntil = device.BlockedUntil };
        }
    }

    /// <summary>Changes a device's state and persists it.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="change">The change.</param>
    public void Update(string deviceId, Action<RelayDeviceState> change)
    {
        lock (_lock)
        {
            change(Device(deviceId));
            Persist();
        }
    }

    /// <summary>Forgets a device.</summary>
    /// <param name="deviceId">The device id.</param>
    public void Forget(string deviceId)
    {
        lock (_lock)
        {
            if (_doc.Devices.Remove(deviceId))
            {
                Persist();
            }
        }
    }

    /// <summary>Counts a result and remembers it for the settings page.</summary>
    /// <param name="now">The time.</param>
    /// <param name="status">A short status.</param>
    /// <param name="sent">Whether the relay accepted the notification.</param>
    /// <param name="quota">Whether a quota stopped it.</param>
    public void Note(DateTimeOffset now, string status, bool sent, bool quota)
    {
        lock (_lock)
        {
            _doc.LastStatus = status;
            _doc.LastAt = now;
            if (sent)
            {
                _doc.Sent++;
            }
            else
            {
                _doc.Refused++;
            }

            if (quota)
            {
                _doc.QuotaHits++;
            }

            Persist();
        }
    }

    /// <summary>Gets when a spent server quota ends.</summary>
    /// <returns>The time, or <c>null</c>.</returns>
    public DateTimeOffset? ServerBlockedUntil() { lock (_lock) { return _doc.ServerBlockedUntil; } }

    /// <summary>Blocks every send until a time.</summary>
    /// <param name="until">The time.</param>
    public void BlockServerUntil(DateTimeOffset until)
    {
        lock (_lock)
        {
            _doc.ServerBlockedUntil = until;
            Persist();
        }
    }

    /// <summary>Gets the key id the relay banned.</summary>
    /// <returns>The key id, or empty.</returns>
    public string BannedKid() { lock (_lock) { return _doc.BannedKid; } }

    /// <summary>Remembers that the relay banned a key id.</summary>
    /// <param name="kid">The key id, base64url.</param>
    public void Ban(string kid)
    {
        lock (_lock)
        {
            _doc.BannedKid = kid;
            Persist();
        }
    }

    /// <summary>Reads the numbers for the settings page.</summary>
    /// <returns>The last status, its time and the counters.</returns>
    public (string LastStatus, DateTimeOffset? LastAt, long Sent, long Refused, long QuotaHits) Summary()
    {
        lock (_lock)
        {
            return (_doc.LastStatus, _doc.LastAt, _doc.Sent, _doc.Refused, _doc.QuotaHits);
        }
    }

    private RelayDeviceState Device(string deviceId)
    {
        if (!_doc.Devices.TryGetValue(deviceId, out var device))
        {
            device = new RelayDeviceState();
            _doc.Devices[deviceId] = device;
        }

        return device;
    }

    private void Persist() => JsonFile.Write(_path, _doc);

    private sealed class Document
    {
        public Dictionary<string, RelayDeviceState> Devices { get; init; } = new();

        public DateTimeOffset? ServerBlockedUntil { get; set; }

        public string BannedKid { get; set; } = string.Empty;

        public string LastStatus { get; set; } = string.Empty;

        public DateTimeOffset? LastAt { get; set; }

        public long Sent { get; set; }

        public long Refused { get; set; }

        public long QuotaHits { get; set; }
    }
}
