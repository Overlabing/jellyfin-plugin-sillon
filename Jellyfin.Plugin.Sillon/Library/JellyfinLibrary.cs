using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Sillon.Library;

/// <summary><see cref="IJellyfinLibrary"/> over Jellyfin's own managers.</summary>
public sealed class JellyfinLibrary : IJellyfinLibrary
{
    private static readonly string[] MusicBrainzKeys = { "MusicBrainzAlbum", "MusicBrainzReleaseGroup" };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly LibraryAlbumCache _albums;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLibrary"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="libraryMonitor">The library monitor.</param>
    public JellyfinLibrary(
        ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, ILibraryMonitor libraryMonitor)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryMonitor = libraryMonitor;
        _albums = new LibraryAlbumCache(ReadAlbums);

        // a singleton, for as long as the server: these never need detaching
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
    }

    /// <inheritdoc />
    public string? FindAlbum(string foreignAlbumId, string title, string artist)
    {
        if (!string.IsNullOrWhiteSpace(foreignAlbumId))
        {
            var byId = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
                Recursive = true,
                HasAnyProviderId = MusicBrainzKeys.ToDictionary(k => k, _ => foreignAlbumId)
            });
            var match = byId.FirstOrDefault(i => MusicBrainzKeys.Any(k =>
                i.TryGetProviderId(k, out var value) && string.Equals(value, foreignAlbumId, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                return match.Id.ToString("N");
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var byTitle = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            Recursive = true,
            SearchTerm = title
        });
        return byTitle
            .OfType<MusicAlbum>()
            .FirstOrDefault(a => string.Equals(a.Name, title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase))
            ?.Id.ToString("N");
    }

    /// <inheritdoc />
    public IReadOnlySet<string> FindAlbums(IReadOnlyList<AlbumKey> albums) => _albums.Held(albums);

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is MusicAlbum)
        {
            _albums.Invalidate();
        }
    }

    /// <summary>One read of the albums instead of up to two queries per album, which a discography of hundreds would feel.</summary>
    private IReadOnlyList<LibraryAlbum> ReadAlbums()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            Recursive = true
        })
        .OfType<MusicAlbum>()
        .Select(a => new LibraryAlbum(
            MusicBrainzKeys.Select(k => a.TryGetProviderId(k, out var value) ? value : null).OfType<string>().ToList(),
            a.Name,
            a.AlbumArtist))
        .ToList();

    /// <inheritdoc />
    public string? FindAlbumByPath(string jellyfinFolder)
    {
        var folder = jellyfinFolder.TrimEnd('/');
        if (folder.Length == 0)
        {
            return null;
        }

        var byPath = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            Recursive = true,
            Path = folder
        });
        var match = byPath.OfType<MusicAlbum>().FirstOrDefault(a => SamePath(a.Path, folder))
            ?? _libraryManager.FindByPath(folder, true) as MusicAlbum;
        return match?.Id.ToString("N");
    }

    /// <inheritdoc />
    public void RefreshPath(string jellyfinFolder) => _libraryMonitor.ReportFileSystemChanged(jellyfinFolder);

    /// <inheritdoc />
    public void ReportPathChanged(string path) => _libraryMonitor.ReportFileSystemChanged(path);

    private static bool SamePath(string? itemPath, string folder)
        => itemPath is not null && string.Equals(itemPath.TrimEnd('/'), folder, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool MarkFavorite(string userId, string itemId)
    {
        if (!Guid.TryParse(userId, out var user) || !Guid.TryParse(itemId, out var item))
        {
            return false;
        }

        var jellyfinUser = _userManager.GetUserById(user);
        var album = _libraryManager.GetItemById(item);
        if (jellyfinUser is null || album is null)
        {
            return false;
        }

        // what UserLibraryController.MarkFavoriteItem does: read the data, set the flag, save with the rating reason
        var data = _userDataManager.GetUserData(jellyfinUser, album);
        if (data is null)
        {
            return false;
        }

        data.IsFavorite = true;
        _userDataManager.SaveUserData(jellyfinUser, album, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        return true;
    }

    /// <inheritdoc />
    public JellyfinUserInfo? GetUser(string userId)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return null;
        }

        var user = _userManager.GetUserById(id);
        return user is null ? null : new JellyfinUserInfo(user.Id.ToString("N"), user.Username);
    }
}
