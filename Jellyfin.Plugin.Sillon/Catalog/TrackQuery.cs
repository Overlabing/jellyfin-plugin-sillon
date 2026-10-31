using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Sillon.Catalog;

/// <summary>A catalogue search read as a track lookup: a title, and the artist when the text names one.</summary>
/// <param name="Title">The track title as typed.</param>
/// <param name="Artist">The artist as typed, null when the text does not name one.</param>
/// <param name="AnySpelling">Whether a recording credited under another spelling counts too: the artist comes from a hint, spelled the hint's way.</param>
public sealed record TrackQuery(string Title, string? Artist, bool AnySpelling = false)
{
    private static readonly string[] Separators = { " - ", " – ", " — ", " by ", " par " };

    /// <summary>Reads a search text. "Artist - Title" and "Title by Artist" (also "par") name an artist; anything else is a bare title.</summary>
    /// <param name="text">The search text.</param>
    /// <returns>The query, null when the text is too short to look up a track.</returns>
    public static TrackQuery? Parse(string text)
    {
        var query = (text ?? string.Empty).Trim();
        if (query.Length < 3)
        {
            return null;
        }

        foreach (var separator in Separators)
        {
            var at = query.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (at <= 0 || at + separator.Length >= query.Length)
            {
                continue;
            }

            var left = query[..at].Trim();
            var right = query[(at + separator.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            // "Title by Artist" reads right to left
            return separator is " by " or " par " ? new TrackQuery(left, right) : new TrackQuery(right, left);
        }

        return new TrackQuery(query, null);
    }

    /// <summary>Builds the Lucene query of MusicBrainz's recording search.</summary>
    /// <returns>The query text.</returns>
    public string ToLucene()
    {
        if (Artist is null)
        {
            return $"recording:\"{Escape(Title)}\"";
        }

        // artist is the name as credited on the recording, artistname the artist's main name: "Fool's Garden" is credited on the
        // studio album of the artist MusicBrainz calls "Fools Garden", and only one of the two fields finds it under the other spelling
        var artist = Escape(Artist);
        return AnySpelling
            ? $"recording:\"{Escape(Title)}\" AND (artist:\"{artist}\" OR artistname:\"{artist}\")"
            : $"recording:\"{Escape(Title)}\" AND artist:\"{artist}\"";
    }

    /// <summary>Folds a name for comparison: case, diacritics and punctuation are ignored, spaces collapsed.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The folded text.</returns>
    public static string Fold(string? text)
    {
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var c in (text ?? string.Empty).Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Folds a name and drops its spaces too, so spellings that only differ by punctuation compare equal (<c>Fools Garden</c>, <c>Fool's Garden</c>).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The folded text, without spaces.</returns>
    public static string FoldTight(string? text) => Fold(text).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
