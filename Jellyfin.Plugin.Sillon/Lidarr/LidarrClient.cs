using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sillon.Lidarr;

/// <summary>Lidarr system status.</summary>
/// <param name="Version">Lidarr version.</param>
public sealed record LidarrStatus([property: JsonPropertyName("version")] string Version);

/// <summary>A Lidarr quality or metadata profile.</summary>
/// <param name="Id">Profile id.</param>
/// <param name="Name">Profile name.</param>
public sealed record LidarrProfile(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>A Lidarr root folder.</summary>
/// <param name="Id">Folder id.</param>
/// <param name="Path">Folder path.</param>
public sealed record LidarrRootFolder(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("path")] string Path);

/// <summary>A failure talking to Lidarr; the message is safe to show to an admin.</summary>
public sealed class LidarrException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LidarrException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public LidarrException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Minimal Lidarr API client. Never logs or echoes the API key.</summary>
public sealed class LidarrClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    /// <summary>Initializes a new instance of the <see cref="LidarrClient"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseUrl">Lidarr base URL.</param>
    /// <param name="apiKey">Lidarr API key.</param>
    public LidarrClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        _apiKey = apiKey ?? string.Empty;
    }

    /// <summary>Gets the system status.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The status.</returns>
    public Task<LidarrStatus> GetStatusAsync(CancellationToken ct = default)
        => GetAsync<LidarrStatus>("/api/v1/system/status", ct);

    /// <summary>Lists quality profiles.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The profiles.</returns>
    public Task<IReadOnlyList<LidarrProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<LidarrProfile>>("/api/v1/qualityprofile", ct);

    /// <summary>Lists metadata profiles.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The profiles.</returns>
    public Task<IReadOnlyList<LidarrProfile>> GetMetadataProfilesAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<LidarrProfile>>("/api/v1/metadataprofile", ct);

    /// <summary>Lists root folders.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folders.</returns>
    public Task<IReadOnlyList<LidarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<LidarrRootFolder>>("/api/v1/rootfolder", ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        if (!Uri.TryCreate(_baseUrl + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LidarrException("URL Lidarr invalide.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Api-Key", _apiKey);

        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new LidarrException("Clé API refusée par Lidarr.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LidarrException($"Lidarr a répondu {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<T>(cts.Token).ConfigureAwait(false);
            return body ?? throw new LidarrException("Réponse Lidarr vide.");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LidarrException("Lidarr ne répond pas (délai de 10 s dépassé).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LidarrException("Lidarr injoignable : " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new LidarrException("Réponse Lidarr illisible.", ex);
        }
    }
}
