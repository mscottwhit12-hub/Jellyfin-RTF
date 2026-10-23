using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.TopTen;

/// <summary>A problem talking to TMDB, with a message an admin can act on.</summary>
public sealed class TmdbException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TmdbException"/> class.</summary>
    /// <param name="message">Plain-language message.</param>
    /// <param name="kind">What went wrong.</param>
    /// <param name="inner">Underlying error.</param>
    public TmdbException(string message, TmdbProblem kind, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>Gets what went wrong.</summary>
    public TmdbProblem Kind { get; }
}

/// <summary>Kinds of TMDB problems.</summary>
public enum TmdbProblem
{
    /// <summary>No key in the settings.</summary>
    NoKey,

    /// <summary>TMDB rejected the key.</summary>
    BadKey,

    /// <summary>TMDB couldn't be reached or answered with an error.</summary>
    Unavailable,
}

/// <summary>One movie as TMDB's discover endpoint returns it.</summary>
public sealed class TmdbMovie
{
    /// <summary>Gets or sets the TMDB ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the original title.</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the release date (yyyy-MM-dd).</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the average rating (0 to 10).</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the number of votes.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the poster path, to append to TMDB's image address.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets the release year, if known.</summary>
    [JsonIgnore]
    public int? Year => ReleaseDate is { Length: >= 4 } date && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) ? year : null;
}

/// <summary>
/// Finds the TMDB key Jellyfin itself uses for metadata: the admin's own key from Jellyfin's TMDb plugin settings
/// if they've set one, otherwise the key built into Jellyfin. Read at runtime from Jellyfin's TMDb provider (by
/// reflection, since it isn't part of Jellyfin's plugin API), so nothing is copied into this plugin and a key change
/// in a Jellyfin update is picked up automatically.
/// </summary>
public static class JellyfinTmdbKey
{
    /// <summary>Where the key came from: the admin's own key.</summary>
    public const string FromAdmin = "your key from Jellyfin's TMDb settings";

    /// <summary>Where the key came from: Jellyfin's built-in key.</summary>
    public const string FromJellyfin = "Jellyfin's built-in TMDB key";

    private const string ProvidersAssembly = "MediaBrowser.Providers";
    private const string PluginType = "MediaBrowser.Providers.Plugins.Tmdb.Plugin";
    private const string UtilsType = "MediaBrowser.Providers.Plugins.Tmdb.TmdbUtils";

    /// <summary>Finds the key.</summary>
    /// <param name="logger">Logger for problems.</param>
    /// <returns>The key and where it came from, or nulls.</returns>
    public static (string? Key, string? Source) Find(ILogger logger)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == ProvidersAssembly);
            if (assembly is null)
            {
                logger.LogWarning("[RaceToFinish] Jellyfin's TMDb provider isn't loaded");
                return (null, null);
            }

            // The admin's own key, if set: Plugin.Instance.Configuration.TmdbApiKey.
            object? plugin = assembly.GetType(PluginType)?.GetProperty("Instance")?.GetValue(null);
            object? config = plugin?.GetType().GetProperty("Configuration")?.GetValue(plugin);
            if (config?.GetType().GetProperty("TmdbApiKey")?.GetValue(config) is string own && !string.IsNullOrWhiteSpace(own))
            {
                return (own.Trim(), FromAdmin);
            }

            // Jellyfin's built-in key: TmdbUtils.ApiKey.
            if (assembly.GetType(UtilsType)?.GetField("ApiKey")?.GetValue(null) is string builtIn && !string.IsNullOrWhiteSpace(builtIn))
            {
                return (builtIn, FromJellyfin);
            }

            logger.LogWarning("[RaceToFinish] Couldn't find a TMDB key in Jellyfin's TMDb provider");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RaceToFinish] Couldn't read Jellyfin's TMDB key");
        }

        return (null, null);
    }
}

/// <summary>
/// Talks to TMDB's API with the key Jellyfin uses (see <see cref="JellyfinTmdbKey"/>). Accepts either kind of TMDB credential: a v3 API key (sent as ?api_key=) or a v4 read
/// access token (a long "eyJ…" string, sent as a Bearer header). Every failure becomes a <see cref="TmdbException"/>
/// with a message the admin can act on.
/// </summary>
public class TmdbClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="TmdbClient"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory (uses the server's proxy settings).</param>
    /// <param name="logger">Logger.</param>
    public TmdbClient(IHttpClientFactory httpClientFactory, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Runs TMDB's movie discover query.</summary>
    /// <param name="parameters">Query parameters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The first page of results.</returns>
    public async Task<IReadOnlyList<TmdbMovie>> DiscoverMoviesAsync(IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        DiscoverPage page = await GetAsync<DiscoverPage>("discover/movie", parameters, cancellationToken).ConfigureAwait(false);
        return page.Results ?? [];
    }

    private async Task<T> GetAsync<T>(string path, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        (string? found, _) = JellyfinTmdbKey.Find(_logger);
        string key = found?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            throw new TmdbException("Couldn't find Jellyfin's TMDB key. Add your own key in Dashboard → Plugins → TMDb, then try again.", TmdbProblem.NoKey);
        }

        bool isToken = key.StartsWith("eyJ", StringComparison.Ordinal);
        var query = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        if (!isToken)
        {
            query["api_key"] = key;
        }

        string baseUrl = (config?.TmdbApiBaseUrl ?? "https://api.themoviedb.org/3").TrimEnd('/');
        string url = baseUrl + "/" + path + "?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (isToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            response = await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[RaceToFinish] TMDB request failed");
            throw new TmdbException("Couldn't reach TMDB. Check the server's internet connection and try again.", TmdbProblem.Unavailable, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TmdbException("TMDB rejected the key. Get a free key at themoviedb.org → Settings → API and add it in Dashboard → Plugins → TMDb.", TmdbProblem.BadKey);
            }

            if (!response.IsSuccessStatusCode)
            {
                string why = response.StatusCode == HttpStatusCode.TooManyRequests ? "TMDB is limiting requests right now" : $"TMDB answered {(int)response.StatusCode}";
                throw new TmdbException(why + ". Try again in a minute.", TmdbProblem.Unavailable);
            }

            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(body, Json, cancellationToken).ConfigureAwait(false)
                    ?? throw new TmdbException("TMDB sent an empty answer. Try again later.", TmdbProblem.Unavailable);
            }
            catch (JsonException ex)
            {
                throw new TmdbException("TMDB sent an answer Race to Finish couldn't read. Try again later.", TmdbProblem.Unavailable, ex);
            }
        }
    }

    private sealed class DiscoverPage
    {
        [JsonPropertyName("results")]
        public List<TmdbMovie>? Results { get; set; }
    }
}
