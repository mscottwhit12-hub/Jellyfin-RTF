using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.TopTen;

/// <summary>One film in a Top 10 list, and whether the server has it.</summary>
public sealed class TopTenEntry
{
    /// <summary>Gets or sets the rank, 1 to 10.</summary>
    public int Rank { get; set; }

    /// <summary>Gets or sets the IMDb ID, e.g. tt1234567.</summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int Year { get; set; }

    /// <summary>Gets or sets IMDb's rating when the list was made.</summary>
    public double Rating { get; set; }

    /// <summary>Gets or sets IMDb's vote count when the list was made.</summary>
    public int Votes { get; set; }

    /// <summary>Gets or sets the running time in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether the server has this film.</summary>
    public bool OnServer { get; set; }

    /// <summary>Gets or sets the Jellyfin item ID when on the server.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets how it was matched: "imdb" (exact ID) or "title" (title and year).</summary>
    public string? MatchedBy { get; set; }
}

/// <summary>A year's Top 10 and how much of it the server has.</summary>
public sealed class TopTenList
{
    /// <summary>Gets or sets the year.</summary>
    public int Year { get; set; }

    /// <summary>Gets or sets the display name, e.g. "Top 10 of 2023 (IMDb)".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the vote minimum actually used for this year.</summary>
    public int MinimumVotes { get; set; }

    /// <summary>Gets or sets the films, best first.</summary>
    public List<TopTenEntry> Movies { get; set; } = [];

    /// <summary>Gets how many are on the server.</summary>
    public int OnServerCount => Movies.Count(m => m.OnServer);

    /// <summary>Gets a value indicating whether every film is on the server (the challenge can start).</summary>
    public bool Complete => Movies.Count > 0 && Movies.All(m => m.OnServer);
}

/// <summary>
/// The ready-made "Top 10 movies of a year" lists. They're built from IMDb's official data files by
/// build/build_imdb_lists.py (run by the "Update IMDb Top 10 lists" GitHub workflow) and shipped inside the
/// plugin, so they need no API key and no internet access. This class only reads them and checks them against
/// the library.
/// </summary>
public class TopTenService
{
    /// <summary>The years offered as ready-made challenges.</summary>
    public static readonly IReadOnlyList<int> PresetYears = [2020, 2021, 2022, 2023, 2024, 2025];

    /// <summary>Plain-language description shown to admins. Keep in step with build_imdb_lists.py.</summary>
    public const string DefinitionText =
        "The 10 highest-rated feature films on IMDb first released that year, among films with at least 100,000 votes " +
        "(fewer if a year doesn't have 10 such films). Documentaries, TV movies and films under an hour are left out.";

    /// <summary>IMDb's required credit line.</summary>
    public const string Attribution = "Information courtesy of IMDb (https://www.imdb.com). Used with permission.";

    private const string ResourceName = "Jellyfin.Plugin.RaceToFinish.TopTen.imdb-top10.json";

    private readonly ILibraryManager _libraryManager;
    private readonly ShippedLists _lists;

    /// <summary>Initializes a new instance of the <see cref="TopTenService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public TopTenService(ILibraryManager libraryManager, ILogger<TopTenService> logger)
    {
        _libraryManager = libraryManager;
        _lists = Load(logger);
    }

    /// <summary>Gets when the shipped lists were made from IMDb's data, UTC, if known.</summary>
    public DateTime? GeneratedUtc => _lists.GeneratedUtc;

    /// <summary>Display name for a year's list.</summary>
    /// <param name="year">Year.</param>
    /// <returns>Label.</returns>
    public static string LabelFor(int year) => string.Create(CultureInfo.InvariantCulture, $"Top 10 of {year} (IMDb)");

    /// <summary>Whether this build of the plugin has a list for the year.</summary>
    /// <param name="year">Year.</param>
    /// <returns>True if available.</returns>
    public bool Has(int year) => _lists.Years.TryGetValue(year.ToString(CultureInfo.InvariantCulture), out var y) && y.Movies.Count > 0;

    /// <summary>Gets a year's Top 10, matched against the library. Null if this build has no list for that year.</summary>
    /// <param name="year">One of <see cref="PresetYears"/>.</param>
    /// <returns>The list, or null.</returns>
    public TopTenList? GetList(int year) => GetLists([year]).FirstOrDefault();

    /// <summary>Gets several years' lists, matching against the library once.</summary>
    /// <param name="years">Years.</param>
    /// <returns>The lists this build has, in the order asked.</returns>
    public IReadOnlyList<TopTenList> GetLists(IEnumerable<int> years)
    {
        var wanted = years.Where(y => PresetYears.Contains(y) && Has(y)).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var library = LibraryIndex.Build(_libraryManager);
        return wanted.Select(year =>
        {
            ShippedYear shipped = _lists.Years[year.ToString(CultureInfo.InvariantCulture)];
            var list = new TopTenList { Year = year, Label = LabelFor(year), MinimumVotes = shipped.MinimumVotes };
            foreach (ShippedMovie movie in shipped.Movies.OrderBy(m => m.Rank))
            {
                (BaseItem? item, string? how) = library.Find(movie);
                list.Movies.Add(new TopTenEntry
                {
                    Rank = movie.Rank,
                    ImdbId = movie.ImdbId,
                    Title = movie.Title,
                    Year = movie.Year,
                    Rating = movie.Rating,
                    Votes = movie.Votes,
                    RuntimeMinutes = movie.RuntimeMinutes,
                    OnServer = item is not null,
                    ItemId = item?.Id.ToString("N", CultureInfo.InvariantCulture),
                    MatchedBy = how,
                });
            }

            return list;
        }).ToList();
    }

    private static ShippedLists Load(ILogger logger)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                ShippedLists? lists = JsonSerializer.Deserialize<ShippedLists>(stream);
                if (lists is not null)
                {
                    lists.Years ??= [];
                    foreach (ShippedYear year in lists.Years.Values)
                    {
                        year.Movies ??= [];
                    }

                    return lists;
                }
            }

            logger.LogWarning("[RaceToFinish] The built-in Top 10 lists are missing from this build");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[RaceToFinish] Could not read the built-in Top 10 lists");
        }

        return new ShippedLists();
    }

    // The shape written by build/build_imdb_lists.py.
    private sealed class ShippedLists
    {
        [JsonPropertyName("generatedUtc")]
        public DateTime? GeneratedUtc { get; set; }

        [JsonPropertyName("years")]
        public Dictionary<string, ShippedYear> Years { get; set; } = [];
    }

    private sealed class ShippedYear
    {
        [JsonPropertyName("minimumVotes")]
        public int MinimumVotes { get; set; }

        [JsonPropertyName("movies")]
        public List<ShippedMovie> Movies { get; set; } = [];
    }

    private sealed class ShippedMovie
    {
        [JsonPropertyName("rank")]
        public int Rank { get; set; }

        [JsonPropertyName("imdbId")]
        public string ImdbId { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("originalTitle")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("year")]
        public int Year { get; set; }

        [JsonPropertyName("runtimeMinutes")]
        public int? RuntimeMinutes { get; set; }

        [JsonPropertyName("rating")]
        public double Rating { get; set; }

        [JsonPropertyName("votes")]
        public int Votes { get; set; }
    }

    /// <summary>The server's movies, indexed for matching.</summary>
    private sealed class LibraryIndex
    {
        private readonly Dictionary<string, BaseItem> _byImdb = new(StringComparer.OrdinalIgnoreCase);
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

                if (item.ProviderIds is not null && item.ProviderIds.TryGetValue("Imdb", out string? imdb) && !string.IsNullOrWhiteSpace(imdb))
                {
                    index._byImdb.TryAdd(imdb.Trim(), item);
                }

                if (item.ProductionYear is int year)
                {
                    index._byTitleYear.TryAdd(Key(item.Name, year), item);
                    if (!string.IsNullOrWhiteSpace(item.OriginalTitle))
                    {
                        index._byTitleYear.TryAdd(Key(item.OriginalTitle, year), item);
                    }
                }
            }

            return index;
        }

        public (BaseItem? Item, string? How) Find(ShippedMovie movie)
        {
            if (!string.IsNullOrEmpty(movie.ImdbId) && _byImdb.TryGetValue(movie.ImdbId, out BaseItem? exact))
            {
                return (exact, "imdb");
            }

            // Fallback for files Jellyfin never matched with IMDb: same title and year (a year either side,
            // since release years differ by country and between sites).
            foreach (string? title in new[] { movie.Title, movie.OriginalTitle })
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                foreach (int y in new[] { movie.Year, movie.Year - 1, movie.Year + 1 })
                {
                    if (_byTitleYear.TryGetValue(Key(title, y), out BaseItem? byTitle))
                    {
                        return (byTitle, "title");
                    }
                }
            }

            return (null, null);
        }

        // Case, accents and punctuation don't matter: "Spider-Man: Across the Spider-Verse" = "spider man across the spider verse".
        private static string Key(string title, int year)
        {
            string decomposed = title.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.Append('|').Append(year.ToString(CultureInfo.InvariantCulture)).ToString();
        }
    }
}
