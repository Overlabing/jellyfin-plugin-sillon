namespace Jellyfin.Plugin.Sillon.Storage;

/// <summary>The registered device tokens, in memory and in <c>devices.json</c>.</summary>
public sealed class DeviceStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<DeviceRecord> _devices;

    /// <summary>Initializes a new instance of the <see cref="DeviceStore"/> class.</summary>
    /// <param name="directory">The data folder.</param>
    /// <param name="now">The clock; the system clock when null.</param>
    public DeviceStore(string directory, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(directory, "devices.json");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _devices = JsonFile.Read<DevicesDocument>(_path)?.Devices ?? new List<DeviceRecord>();
    }

    /// <summary>
    /// Registers a token for a user, or refreshes it when that user already owns it. A token owned by another user is never moved:
    /// anyone who learns a token (it is a bearer address for that user's notifications) could otherwise take it over. The legitimate
    /// hand-over is explicit: the app deletes its token on sign-out, which frees it for the next account. After a reinstall without
    /// sign-out APNs issues a new token, and the old one is dropped when APNs answers 410/BadDeviceToken (see ApnsNotifier).
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="userId">The user.</param>
    /// <param name="platform">The platform.</param>
    /// <param name="env">The APNs environment, or empty for the default.</param>
    /// <returns><c>false</c>, and nothing changes, when another user owns the token.</returns>
    public bool Upsert(string token, string userId, string platform, string env)
    {
        lock (_lock)
        {
            var now = _now();
            var index = _devices.FindIndex(d => d.Token == token);
            if (index >= 0 && _devices[index].UserId != userId)
            {
                return false;
            }

            var created = index >= 0 ? _devices[index].CreatedAt : now;
            var device = new DeviceRecord { Token = token, UserId = userId, Platform = platform, Env = env, CreatedAt = created, UpdatedAt = now };
            if (index >= 0)
            {
                _devices[index] = device;
            }
            else
            {
                _devices.Add(device);
            }

            Persist();
            return true;
        }
    }

    /// <summary>
    /// Registers a device for the relay: no token, only its public key. Like a token, a key owned by another user is never moved.
    /// </summary>
    /// <param name="devicePublicKey">The device public key, base64url.</param>
    /// <param name="userId">The user.</param>
    /// <param name="platform">The platform.</param>
    /// <param name="env">The APNs environment, or empty for the default.</param>
    /// <returns><c>false</c>, and nothing changes, when another user owns the key.</returns>
    public bool UpsertRelay(string devicePublicKey, string userId, string platform, string env)
    {
        lock (_lock)
        {
            var now = _now();
            var index = _devices.FindIndex(d => d.DevicePublicKey == devicePublicKey && d.IsRelay);
            if (index >= 0 && _devices[index].UserId != userId)
            {
                return false;
            }

            var created = index >= 0 ? _devices[index].CreatedAt : now;
            var device = new DeviceRecord
            {
                DevicePublicKey = devicePublicKey,
                PushMode = "relay",
                UserId = userId,
                Platform = platform,
                Env = env,
                CreatedAt = created,
                UpdatedAt = now
            };
            if (index >= 0)
            {
                _devices[index] = device;
            }
            else
            {
                _devices.Add(device);
            }

            Persist();
            return true;
        }
    }

    /// <summary>Finds a relay device by its id.</summary>
    /// <param name="deviceId">The device id, base64url.</param>
    /// <returns>The device, or <c>null</c>.</returns>
    public DeviceRecord? FindByDeviceId(string deviceId)
    {
        lock (_lock)
        {
            return _devices.FirstOrDefault(d => d.IsRelay && d.DeviceId == deviceId);
        }
    }

    /// <summary>Removes a relay device by its id.</summary>
    /// <param name="deviceId">The device id, base64url.</param>
    /// <param name="userId">When not null, only that owner's device is removed.</param>
    public void DeleteByDeviceId(string deviceId, string? userId)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => d.IsRelay && d.DeviceId == deviceId && (userId is null || d.UserId == userId)) > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>Removes a token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="userId">When not null, only that owner's token is removed.</param>
    public void Delete(string token, string? userId)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => d.Token == token && (userId is null || d.UserId == userId)) > 0)
            {
                Persist();
            }
        }
    }

    /// <summary>Lists a user's devices, oldest first.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The devices.</returns>
    public IReadOnlyList<DeviceRecord> For(string userId)
    {
        lock (_lock)
        {
            return _devices.Where(d => d.UserId == userId).OrderBy(d => d.CreatedAt).ToList();
        }
    }

    /// <summary>Lists every device, oldest first.</summary>
    /// <returns>The devices.</returns>
    public IReadOnlyList<DeviceRecord> All()
    {
        lock (_lock)
        {
            return _devices.OrderBy(d => d.CreatedAt).ToList();
        }
    }

    /// <summary>Removes a device by its public id (see <see cref="DeviceRecord.PublicId"/>).</summary>
    /// <param name="publicId">The id.</param>
    /// <returns><c>true</c> when a device was removed.</returns>
    public bool DeleteByPublicId(string publicId)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => string.Equals(d.PublicId, publicId, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    private void Persist() => JsonFile.Write(_path, new DevicesDocument { Devices = _devices });

    private sealed class DevicesDocument
    {
        public List<DeviceRecord> Devices { get; init; } = new();
    }
}
