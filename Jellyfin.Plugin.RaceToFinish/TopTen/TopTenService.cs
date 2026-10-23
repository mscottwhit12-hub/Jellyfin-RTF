using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.TopTen;

/// <summary>One film in a Top 5 list, and whether the server has it.</summary>
public sealed class TopTenEntry
{
    /// <summary>Gets or sets the rank, 1 to 5.</summary>
    public int Rank { get; set; }

    /// <summary>Gets or sets the TMDB ID.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the release date.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets TMDB's average rating.</summary>
    public double Rating { get; set; }

    /// <summary>Gets or sets TMDB's vote count.</summary>
    public int Votes { get; set; }

    /// <summary>Gets or sets the poster image address on TMDB, if any.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether the server has this film.</summary>
    public bool OnServer { get; set; }

    /// <summary>Gets or sets the Jellyfin item ID when on the server.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets how it was matched: "tmdb" (exact ID) or "title" (title and year).</summary>
    public string? MatchedBy { get; set; }
}

/// <summary>A year's Top 5 and how much of it the server has.</summary>
public sealed class TopTenList
{
    /// <summary>Gets or sets the year.</summary>
    public int Year { get; set; }

    /// <summary>Gets or sets the display name, e.g. "Top 5 of 2023 (TMDB)".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets a plain-language description of how the list is made.</summary>
    public string Definition { get; set; } = string.Empty;

    /// <summary>Gets or sets the vote minimum actually used for this year.</summary>
    public int MinimumVotes { get; set; }

    /// <summary>Gets or sets when TMDB was asked, UTC.</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>Gets or sets the films, best first.</summary>
    public List<TopTenEntry> Movies { get; set; } = [];

    /// <summary>Gets how many are on the server.</summary>
    public int OnServerCount => Movies.Count(m => m.OnServer);

    /// <summary>Gets a value indicating whether every film is on the server (the challenge can start).</summary>
    public bool Complete => Movies.Count > 0 && Movies.All(m => m.OnServer);
}

/// <summary>
/// The ready-made "Top 5 movies of a year" lists.
///
/// How a list is made: movies whose primary release is in that year, sorted by TMDB rating, best first. A film
/// needs at least 3,000 votes, so the list is films lots of people have actually seen and small fan groups can't
/// rate a niche title to the top; if a year has fewer than 5 such films the minimum steps down to 2,000, then
/// 1,000. Documentaries and TV movies are left out (this keeps concert films and TV specials out), and so are
/// films under an hour. Adult titles are never included. A few films whose rating is clearly fan-boosted are
/// blocked by TMDB ID (see <see cref="Blocked"/>); the next film moves up.
/// Lists are kept for a day so previews don't hit TMDB every time; a challenge freezes its own copy when it starts.
/// </summary>
public class TopTenService
{
    /// <summary>The years offered as ready-made challenges.</summary>
    public static readonly IReadOnlyList<int> PresetYears = [2020, 2021, 2022, 2023, 2024, 2025];

    /// <summary>Plain-language description shown to admins.</summary>
    public const string DefinitionText =
        "The 5 highest-rated movies on TMDB first released that year, among films with at least 3,000 votes " +
        "(fewer if a year doesn't have 5 such films). Documentaries, TV movies and films under an hour are left out, " +
        "and so are a few films whose ratings are boosted by fan campaigns.";

    /// <summary>
    /// Films left out because their TMDB rating is inflated by a dedicated fan base rather than broad audiences.
    /// Keyed by TMDB ID so a renamed title can't slip through. Adding one needs a plugin update.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> Blocked = new Dictionary<int, string>
    {
        [1010581] = "My Fault (2023): Spanish teen romance rated far above its reviews by its fan base",
        [610150] = "Dragon Ball Super: Super Hero (2022): franchise fan ratings, well above general audiences",
    };

    private const int ListSize = 5;
    private const string DocumentaryGenre = "99";
    private const string TvMovieGenre = "10770";
    private const string PosterBase = "https://image.tmdb.org/t/p/w342";
    private static readonly int[] VoteSteps = [3000, 2000, 1000];
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    private readonly TmdbClient _tmdb;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TopTenService> _logger;
    private readonly ConcurrentDictionary<int, (DateTime FetchedUtc, int MinimumVotes, List<TmdbMovie> Movies)> _cache = new();

    /// <summary>Initializes a new instance of the <see cref="TopTenService"/> class.</summary>
    /// <param name="tmdb">TMDB client.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public TopTenService(TmdbClient tmdb, ILibraryManager libraryManager, ILogger<TopTenService> logger)
    {
        _tmdb = tmdb;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Display name for a year's list.</summary>
    /// <param name="year">Year.</param>
    /// <returns>Label.</returns>
    public static string LabelFor(int year) => string.Create(CultureInfo.InvariantCulture, $"Top 5 of {year} (TMDB)");

    /// <summary>Forgets cached lists, so the next request asks TMDB again (e.g. after changing the key).</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>Gets a year's Top 5, matched against the library.</summary>
    /// <param name="year">One of <see cref="PresetYears"/>.</param>
    /// <param name="refresh">True to ask TMDB again even if a recent copy is cached.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The list.</returns>
    public async Task<TopTenList> GetListAsync(int year, bool refresh, CancellationToken cancellationToken)
    {
        if (!PresetYears.Contains(year))
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "Not one of the ready-made years.");
        }

        bool haveCopy = _cache.TryGetValue(year, out var cached);
        if (refresh || !haveCopy || DateTime.UtcNow - cached.FetchedUtc > CacheFor)
        {
            try
            {
                cached = await FetchAsync(year, cancellationToken).ConfigureAwait(false);
                _cache[year] = cached;
            }
            catch (TmdbException ex) when (haveCopy && !refresh && ex.Kind == TmdbProblem.Unavailable)
            {
                // TMDB is having trouble: an older copy beats an error. (An explicit refresh still reports it.)
                _logger.LogWarning("[RaceToFinish] TMDB unavailable, using the copy from {Fetched:u} for {Year}", cached.FetchedUtc, year);
            }
        }

        var library = LibraryIndex.Build(_libraryManager);
        var list = new TopTenList
        {
            Year = year,
            Label = LabelFor(year),
            Definition = DefinitionText,
            MinimumVotes = cached.MinimumVotes,
            FetchedUtc = cached.FetchedUtc,
        };

        int rank = 1;
        foreach (TmdbMovie movie in cached.Movies)
        {
            (BaseItem? item, string? how) = library.Find(movie);
            list.Movies.Add(new TopTenEntry
            {
                Rank = rank++,
                TmdbId = movie.Id,
                Title = movie.Title,
                Year = movie.Year,
                ReleaseDate = movie.ReleaseDate,
                Rating = Math.Round(movie.VoteAverage, 1),
                Votes = movie.VoteCount,
                PosterUrl = string.IsNullOrEmpty(movie.PosterPath) ? null : PosterBase + movie.PosterPath,
                OnServer = item is not null,
                ItemId = item?.Id.ToString("N", CultureInfo.InvariantCulture),
                MatchedBy = how,
            });
        }

        return list;
    }

    private async Task<(DateTime, int, List<TmdbMovie>)> FetchAsync(int year, CancellationToken cancellationToken)
    {
        List<TmdbMovie> best = [];
        int used = VoteSteps[^1];
        foreach (int minimumVotes in VoteSteps)
        {
            var parameters = new Dictionary<string, string>
            {
                ["primary_release_year"] = year.ToString(CultureInfo.InvariantCulture),
                ["sort_by"] = "vote_average.desc",
                ["vote_count.gte"] = minimumVotes.ToString(CultureInfo.InvariantCulture),
                ["without_genres"] = DocumentaryGenre + "," + TvMovieGenre,
                ["with_runtime.gte"] = "60",
                ["include_adult"] = "false",
                ["include_video"] = "false",
                ["language"] = "en-US",
                ["page"] = "1",
            };

            IReadOnlyList<TmdbMovie> results = await _tmdb.DiscoverMoviesAsync(parameters, cancellationToken).ConfigureAwait(false);

            // TMDB already sorts by rating; ties are broken by more votes, then title, so the order is stable.
            best = results
                .Where(m => m.Id > 0 && !string.IsNullOrWhiteSpace(m.Title) && !Blocked.ContainsKey(m.Id))
                .OrderByDescending(m => m.VoteAverage)
                .ThenByDescending(m => m.VoteCount)
                .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .Take(ListSize)
                .ToList();
            used = minimumVotes;
            if (best.Count >= ListSize)
            {
                break;
            }
        }

        _logger.LogInformation("[RaceToFinish] Fetched {Label}: {Count} films (min {Votes} votes)", LabelFor(year), best.Count, used);
        return (DateTime.UtcNow, used, best);
    }

    /// <summary>The server's movies, indexed for matching.</summary>
    private sealed class LibraryIndex
    {
        private readonly Dictionary<string, BaseItem> _byTmdb = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BaseItem> _byTitleYear = new(StringComparer.Ordinal);

        public static LibraryIndex Build(ILibraryManager libraryManager)
        {
            var index = new LibraryIndex();
            var query = new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, IsVirtualItem = false };
            foreach (BaseItem item in libraryManager.GetItemList(query))
            {
                if (item is not Movie)
                {
                    continue;
                }

                if (item.ProviderIds is not null && item.ProviderIds.TryGetValue("Tmdb", out string? tmdb) && !string.IsNullOrWhiteSpace(tmdb))
                {
                    // A file Jellyfin matched with TMDB is only ever that film. It's never offered for title
                    // matching, so a different film with the same title can't claim it.
                    index._byTmdb.TryAdd(tmdb.Trim(), item);
                    continue;
                }

                int? year = item.ProductionYear ?? item.PremiereDate?.Year;
                if (year is int y)
                {
                    foreach (string? title in new[] { item.Name, item.OriginalTitle })
                    {
                        if (Key(title, y) is string key)
                        {
                            index._byTitleYear.TryAdd(key, item);
                        }
                    }
                }
            }

            return index;
        }

        public (BaseItem? Item, string? How) Find(TmdbMovie movie)
        {
            if (_byTmdb.TryGetValue(movie.Id.ToString(CultureInfo.InvariantCulture), out BaseItem? exact))
            {
                return (exact, "tmdb");
            }

            // Fallback for files Jellyfin never matched with TMDB: same title and year (a year either side,
            // since release years differ by country).
            if (movie.Year is int year)
            {
                foreach (string? title in new[] { movie.Title, movie.OriginalTitle })
                {
                    foreach (int y in new[] { year, year - 1, year + 1 })
                    {
                        if (Key(title, y) is string key && _byTitleYear.TryGetValue(key, out BaseItem? byTitle))
                        {
                            return (byTitle, "title");
                        }
                    }
                }
            }

            return (null, null);
        }

        // Case, accents and punctuation don't matter: "Spider-Man: Across the Spider-Verse" = "spider man across the spider verse".
        // Null when the title has nothing to compare (empty, or only punctuation), so such titles never match.
        private static string? Key(string? title, int year)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string decomposed = title.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.Length == 0 ? null : builder.Append('|').Append(year.ToString(CultureInfo.InvariantCulture)).ToString();
        }
    }
}
