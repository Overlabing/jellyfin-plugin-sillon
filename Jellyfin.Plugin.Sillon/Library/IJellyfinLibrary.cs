namespace Jellyfin.Plugin.Sillon.Library;

/// <summary>A Jellyfin user, as far as the plugin cares.</summary>
/// <param name="Id">The user id, 32 hexadecimal digits.</param>
/// <param name="Name">The user name.</param>
public sealed record JellyfinUserInfo(string Id, string Name);

/// <summary>What the plugin needs of Jellyfin's library; a seam so the request logic runs without a server.</summary>
public interface IJellyfinLibrary
{
    /// <summary>Looks an album up: by MusicBrainz id when an item carries it, else by exact title and artist.</summary>
    /// <param name="foreignAlbumId">The MusicBrainz album id.</param>
    /// <param name="title">The album title.</param>
    /// <param name="artist">The album artist.</param>
    /// <returns>The Jellyfin item id (32 hexadecimal digits), or null when the library does not have the album (yet).</returns>
    string? FindAlbum(string foreignAlbumId, string title, string artist);

    /// <summary>Tells Jellyfin that a path (as Jellyfin sees it) changed, so the library picks it up.</summary>
    /// <param name="path">The path.</param>
    void ReportPathChanged(string path);

    /// <summary>Marks an item favourite for a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item was marked; false when the user or the item no longer exists.</returns>
    bool MarkFavorite(string userId, string itemId);

    /// <summary>Returns a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The user, or null.</returns>
    JellyfinUserInfo? GetUser(string userId);
}
