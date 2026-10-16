using System.Diagnostics;
using Jellyfin.Plugin.Sillon.Storage;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

public sealed class MigrationTests : IDisposable
{
    private const string Alice = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string Bob = "f0e1d2c3b4a5968778695a4b3c2d1e0f";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sillon-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void MigratedFiles_LoadThroughTheStores()
    {
        // no python3, no test: the script is a one-off tool for hosts that have it
        var scripts = FindScripts();
        if (scripts is null || !Python(null, "--version"))
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "gateway.db");
        var data = Path.Combine(_dir, "data");
        Assert.True(Python(scripts, "test_migrate.py", "--make-fixture", db));
        Assert.True(Python(scripts, "migrate-from-gateway.py", "--db", db, "--out", data));

        var requests = new RequestStore(data);
        Assert.Equal(new long[] { 3, 2, 1 }, requests.List(null).Select(r => r.Id));
        var available = requests.Get(1)!;
        Assert.Equal(Alice, available.UserId);
        Assert.Equal("alice", available.UserName);
        Assert.Equal(RequestStatus.Available, available.Status);
        Assert.Equal(11, available.LidarrAlbumId);
        Assert.Equal("jf-item-1", available.JellyfinItemId);
        Assert.Equal((9, 10), (available.TracksImported, available.TracksTotal));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 5, 0, TimeSpan.Zero), available.LibraryAddedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1234560), available.NotifiedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero), available.CreatedAt);

        var requested = requests.Get(2)!;
        Assert.Equal(Bob, requested.UserId);
        Assert.Equal(RequestStatus.Requested, requested.Status);
        Assert.Null(requested.LidarrAlbumId);
        Assert.Null(requested.LibraryAddedAt);

        var cancelled = requests.Get(3)!;
        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);
        Assert.Equal("no longer wanted", cancelled.Error);

        // the next id continues after the migrated ones
        Assert.Equal(4, requests.Create(new RequestRecord { UserId = Bob, ForeignAlbumId = "new" }).Id);

        var devices = new DeviceStore(data);
        var aliceDevice = Assert.Single(devices.For(Alice));
        Assert.Equal("token-alice-1", aliceDevice.Token);
        Assert.Equal("ios", aliceDevice.Platform);
        Assert.Equal("development", aliceDevice.Env);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero), aliceDevice.CreatedAt);
        Assert.Equal("production", Assert.Single(devices.For(Bob)).Env);
    }

    private static string? FindScripts()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts");
            if (File.Exists(Path.Combine(candidate, "migrate-from-gateway.py")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Python(string? scripts, params string[] args)
    {
        var info = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        if (scripts is not null)
        {
            info.ArgumentList.Add("-I");
            info.ArgumentList.Add(Path.Combine(scripts, args[0]));
            foreach (var a in args.Skip(1))
            {
                info.ArgumentList.Add(a);
            }
        }
        else
        {
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }
        }

        try
        {
            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
