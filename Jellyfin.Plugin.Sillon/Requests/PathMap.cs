namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>Helpers for the POSIX paths Lidarr and Jellyfin exchange.</summary>
public static class PathMap
{
    /// <summary>Turns a path as Lidarr sees it into the one Jellyfin sees.</summary>
    /// <param name="path">The path.</param>
    /// <param name="lidarrPrefix">The music folder as Lidarr sees it.</param>
    /// <param name="jellyfinPrefix">The same folder as Jellyfin sees it.</param>
    /// <returns>The remapped path; unchanged when it is outside the Lidarr folder.</returns>
    public static string Remap(string path, string lidarrPrefix, string jellyfinPrefix)
    {
        var from = (lidarrPrefix ?? string.Empty).TrimEnd('/');
        if (from.Length == 0 || (path != from && !path.StartsWith(from + "/", StringComparison.Ordinal)))
        {
            return path;
        }

        return (jellyfinPrefix ?? string.Empty).TrimEnd('/') + path[from.Length..];
    }

    /// <summary>Cleans a POSIX path: no empty, <c>.</c> or <c>..</c> segment, no trailing slash.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The cleaned path.</returns>
    public static string Clean(string path)
    {
        var rooted = path.StartsWith('/');
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!rooted)
                {
                    parts.Add(part);
                }

                continue;
            }

            parts.Add(part);
        }

        var joined = string.Join('/', parts);
        return rooted ? "/" + joined : (joined.Length == 0 ? "." : joined);
    }

    /// <summary>Returns the directory of a POSIX file path.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The directory.</returns>
    public static string Dir(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? "." : Clean(cut == 0 ? "/" : path[..cut]);
    }
}
