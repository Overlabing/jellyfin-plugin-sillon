using System.Text.Json;

namespace Jellyfin.Plugin.Sillon.Storage;

/// <summary>Reads and atomically writes a JSON file.</summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Reads the file; null when it does not exist or is unreadable (then moved aside to <c>.corrupt</c>).</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>The document.</returns>
    public static T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException)
        {
            // keep the unreadable file for the admin and start from an empty store
            File.Move(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }

    /// <summary>Writes the file through a temporary file and a rename.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="value">The document.</param>
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
