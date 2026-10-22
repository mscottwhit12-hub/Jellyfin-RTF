using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RaceToFinish.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.RaceToFinish.Tracking;

/// <summary>
/// Reads what a movie or episode is (credits, studios, genres, IDs) into an <see cref="ItemSnapshot"/>.
/// Episodes borrow from their series where Jellyfin keeps the data on the series (studios, main cast).
/// </summary>
public class ItemSnapshotBuilder
{
    /// <summary>How many top-billed cast members are saved.</summary>
    public const int TopCast = 10;

    /// <summary>Shortest runtime that counts as a real movie or episode (shorter is treated as a clip).</summary>
    public static readonly TimeSpan MinimumRuntime = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="ItemSnapshotBuilder"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    public ItemSnapshotBuilder(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Whether an item is something we track: a movie or episode, not an extra, trailer or theme, and at least five minutes long.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <returns>True to track.</returns>
    public static bool IsTrackable(BaseItem? item)
    {
        if (item is not (Movie or Episode))
        {
            return false;
        }

        if (item.ExtraType is not null || item.IsThemeMedia)
        {
            return false;
        }

        return item.RunTimeTicks is long ticks && TimeSpan.FromTicks(ticks) >= MinimumRuntime;
    }

    /// <summary>Builds the snapshot.</summary>
    /// <param name="item">Movie or episode.</param>
    /// <returns>Snapshot.</returns>
    public ItemSnapshot Build(BaseItem item)
    {
        var snapshot = new ItemSnapshot
        {
            ItemId = Id(item.Id),
            ItemType = item is Episode ? "Episode" : "Movie",
            Title = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            RuntimeSeconds = item.RunTimeTicks is long ticks ? (int)TimeSpan.FromTicks(ticks).TotalSeconds : null,
            TmdbId = Provider(item, "Tmdb"),
            ImdbId = Provider(item, "Imdb"),
            OriginalLanguage = NullIfEmpty(item.OriginalLanguage),
            Genres = Clean(item.Genres),
            Studios = Clean(item.Studios),
            Countries = Clean(item.ProductionLocations),
        };

        IReadOnlyList<PersonInfo> people = SafePeople(item);
        IReadOnlyList<PersonInfo> seriesPeople = [];

        if (item is Episode episode)
        {
            Series? series = episode.Series;
            snapshot.SeriesId = series is null ? (episode.SeriesId == Guid.Empty ? null : Id(episode.SeriesId)) : Id(series.Id);
            snapshot.SeriesTitle = series?.Name ?? episode.SeriesName;
            snapshot.SeasonNumber = episode.ParentIndexNumber;
            snapshot.EpisodeNumber = episode.IndexNumber;

            if (series is not null)
            {
                snapshot.SeriesTmdbId = Provider(series, "Tmdb");
                snapshot.Year ??= series.ProductionYear;
                snapshot.OriginalLanguage ??= NullIfEmpty(series.OriginalLanguage);

                // Jellyfin keeps studios, and usually genres and countries, on the series rather than each episode.
                if (snapshot.Studios.Count == 0)
                {
                    snapshot.Studios = Clean(series.Studios);
                }

                if (snapshot.Genres.Count == 0)
                {
                    snapshot.Genres = Clean(series.Genres);
                }

                if (snapshot.Countries.Count == 0)
                {
                    snapshot.Countries = Clean(series.ProductionLocations);
                }

                seriesPeople = SafePeople(series);
            }
        }

        snapshot.Directors = Credits(people, PersonKind.Director, int.MaxValue);
        snapshot.Writers = Credits(people, PersonKind.Writer, int.MaxValue);

        // Main cast: the item's own actors, or for episodes the series' regulars, plus that episode's guest stars.
        var cast = Credits(people, PersonKind.Actor, TopCast).ToList();
        if (cast.Count == 0 && seriesPeople.Count > 0)
        {
            cast = Credits(seriesPeople, PersonKind.Actor, TopCast).ToList();
        }

        if (item is Episode)
        {
            cast.AddRange(Credits(people, PersonKind.GuestStar, 5).Where(g => cast.All(c => !string.Equals(c.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
        }

        snapshot.Cast = cast;
        FillPersonIds(snapshot.Directors);
        FillPersonIds(snapshot.Writers);
        FillPersonIds(snapshot.Cast);
        return snapshot;
    }

    // An item's credits don't carry provider IDs; they live on each person's own library record.
    private void FillPersonIds(IReadOnlyList<CreditRecord> credits)
    {
        foreach (CreditRecord credit in credits)
        {
            if (credit.Tmdb is not null)
            {
                continue;
            }

            try
            {
                var person = _libraryManager.GetPerson(credit.Name);
                if (person?.ProviderIds is not null && person.ProviderIds.TryGetValue("Tmdb", out string? tmdb) && !string.IsNullOrWhiteSpace(tmdb))
                {
                    credit.Tmdb = tmdb.Trim();
                }
            }
            catch (Exception)
            {
                // Optional detail; skip on any lookup problem.
            }
        }
    }

    /// <summary>Formats an ID the way the database stores it.</summary>
    /// <param name="id">ID.</param>
    /// <returns>32 hex characters.</returns>
    public static string Id(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private IReadOnlyList<PersonInfo> SafePeople(BaseItem item)
    {
        try
        {
            return _libraryManager.GetPeople(item);
        }
        catch (Exception)
        {
            // Credits are a nice-to-have; never let them break tracking.
            return [];
        }
    }

    private static List<CreditRecord> Credits(IReadOnlyList<PersonInfo> people, PersonKind kind, int take)
    {
        return people
            .Where(p => p.Type == kind && !string.IsNullOrWhiteSpace(p.Name))
            .OrderBy(p => p.SortOrder ?? int.MaxValue)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select((p, index) => new CreditRecord
            {
                Name = p.Name.Trim(),
                Role = NullIfEmpty(p.Role),
                Tmdb = p.ProviderIds is not null && p.ProviderIds.TryGetValue("Tmdb", out string? tmdb) ? NullIfEmpty(tmdb) : null,
                Order = p.SortOrder ?? index,
            })
            .ToList();
    }

    private static string? Provider(BaseItem item, string name)
    {
        return item.ProviderIds is not null && item.ProviderIds.TryGetValue(name, out string? value) ? NullIfEmpty(value) : null;
    }

    private static List<string> Clean(string[]? values)
    {
        return values is null
            ? []
            : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
