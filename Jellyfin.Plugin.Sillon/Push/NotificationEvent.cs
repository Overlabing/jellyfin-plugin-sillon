using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Sillon.Storage;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>
/// What happened, as data. The relay protocol sends exactly this (section 5 of the protocol) and the app renders its own sentence;
/// direct mode turns it into the French sentence it has always sent (<see cref="DirectBody"/>).
/// </summary>
/// <param name="Type">One of <see cref="Types"/>.</param>
/// <param name="RequestId">The request id, 0 for none (the test event).</param>
/// <param name="AlbumId">The Jellyfin album id, empty for none.</param>
/// <param name="Title">The album title.</param>
/// <param name="Artist">The artist.</param>
/// <param name="TracksImported">Imported tracks of a partial album; meaningful with <paramref name="TracksTotal"/>.</param>
/// <param name="TracksTotal">Tracks of the album, 0 when unknown.</param>
public sealed partial record NotificationEvent(
    string Type,
    long RequestId,
    string AlbumId,
    string Title,
    string Artist,
    int TracksImported,
    int TracksTotal)
{
    /// <summary>The protocol's maximum size of the event JSON.</summary>
    public const int MaxJsonBytes = 1022;

    private const int MaxNameBytes = 200;
    private const int MaxTracks = 9999;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Gets the test event.</summary>
    public static NotificationEvent Test { get; } = new(Types.Test, 0, string.Empty, string.Empty, string.Empty, 0, 0);

    /// <summary>Builds the "the album is available" event.</summary>
    /// <param name="request">The request.</param>
    /// <param name="albumId">The Jellyfin album id, or empty.</param>
    /// <returns>The event.</returns>
    public static NotificationEvent Available(RequestRecord request, string albumId)
        => new(Types.Available, request.Id, albumId, request.Title, request.Artist, request.TracksImported, request.TracksTotal);

    /// <summary>The French sentence direct mode sends.</summary>
    /// <returns>The sentence.</returns>
    public string DirectBody()
    {
        if (Type != Types.Available)
        {
            return "Sillon: notifications are working";
        }

        var body = $"{Title} de {Artist} est maintenant dans la bibliothèque";
        return TracksTotal > 0 && TracksImported < TracksTotal
            ? body + string.Create(CultureInfo.InvariantCulture, $" ({TracksImported}/{TracksTotal} titres)")
            : body;
    }

    /// <summary>The keys direct mode puts next to <c>aps</c> (the app's notification tap reads them).</summary>
    /// <returns>The keys.</returns>
    public IReadOnlyDictionary<string, object> DirectExtra()
    {
        var extra = new Dictionary<string, object>();
        if (RequestId != 0)
        {
            extra["request_id"] = RequestId;
        }

        if (!string.IsNullOrEmpty(AlbumId))
        {
            extra["album_id"] = AlbumId;
        }

        return extra;
    }

    /// <summary>Builds the event JSON of the protocol: compact UTF-8, fields in a fixed order, at most 1022 bytes.</summary>
    /// <returns>The JSON bytes.</returns>
    public byte[] ToProtocolJson()
    {
        var title = Clean(Title, MaxNameBytes);
        var artist = Clean(Artist, MaxNameBytes);
        var budget = MaxNameBytes;
        while (true)
        {
            var json = Write(title, artist);
            if (json.Length <= MaxJsonBytes)
            {
                return json;
            }

            // only reachable with names full of characters JSON must escape: shorten the longer name until the event fits
            budget = Math.Max(0, budget - 10);
            if (Encoding.UTF8.GetByteCount(title) >= Encoding.UTF8.GetByteCount(artist))
            {
                title = Clean(title, budget);
            }
            else
            {
                artist = Clean(artist, budget);
            }
        }
    }

    // NFC, no control characters, cut on a character boundary to at most maxBytes of UTF-8
    private static string Clean(string? value, int maxBytes)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    private byte[] Write(string title, string artist)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("type", Type);
            if (Type != Types.Test)
            {
                writer.WriteString("request_id", RequestId.ToString(CultureInfo.InvariantCulture));
                if (Type == Types.Available && IdPattern().IsMatch(AlbumId))
                {
                    writer.WriteString("album_id", AlbumId);
                }

                writer.WriteString("title", title);
                writer.WriteString("artist", artist);
                if (Type == Types.Available && TracksTotal > 0)
                {
                    writer.WriteNumber("tracks_imported", Math.Clamp(TracksImported, 0, MaxTracks));
                    writer.WriteNumber("tracks_total", Math.Clamp(TracksTotal, 0, MaxTracks));
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Event types. <c>request.not_found</c> was sent by earlier versions and shipped apps still decode it: never reuse that name.</summary>
    public static class Types
    {
        /// <summary>The album is in the library.</summary>
        public const string Available = "request.available";

        /// <summary>A test.</summary>
        public const string Test = "test";
    }
}
