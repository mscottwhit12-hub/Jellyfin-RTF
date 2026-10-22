namespace Jellyfin.Plugin.RaceToFinish.Data;

/// <summary>Where a viewing session came from. Only <see cref="Live"/> can ever count toward a race.</summary>
public static class SessionSource
{
    /// <summary>Recorded by this plugin's tracker while the person watched.</summary>
    public const string Live = "live";

    /// <summary>Imported from Jellyfin's per-user watch data (played flag, play count, last played).</summary>
    public const string ImportedUserData = "imported-userdata";

    /// <summary>Imported from Jellyfin's activity log (playback started/stopped events).</summary>
    public const string ImportedActivityLog = "imported-activity";

    /// <summary>Imported from the Playback Reporting plugin's database.</summary>
    public const string ImportedPlaybackReporting = "imported-playbackreporting";
}

/// <summary>One person in the credits, as saved with a watched item.</summary>
public sealed class CreditRecord
{
    /// <summary>Gets or sets the person's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the character played, for cast.</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the TMDB person ID, used later to tell actors from actresses.</summary>
    public string? Tmdb { get; set; }

    /// <summary>Gets or sets the billing position (0 = top billed).</summary>
    public int? Order { get; set; }
}

/// <summary>
/// What a watched movie or episode was, saved when it is watched so that stats still work after the
/// file is deleted or the library rebuilt.
/// </summary>
public sealed class ItemSnapshot
{
    /// <summary>Gets or sets the Jellyfin item ID (N format).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets "Movie" or "Episode".</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the series ID, for episodes.</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the series title, for episodes.</summary>
    public string? SeriesTitle { get; set; }

    /// <summary>Gets or sets the season number, for episodes.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number, for episodes.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the runtime in seconds.</summary>
    public int? RuntimeSeconds { get; set; }

    /// <summary>Gets or sets the TMDB ID.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb ID.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the series' TMDB ID, for episodes.</summary>
    public string? SeriesTmdbId { get; set; }

    /// <summary>Gets or sets the original language.</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the studios or networks (episodes take their series').</summary>
    public IReadOnlyList<string> Studios { get; set; } = [];

    /// <summary>Gets or sets the production countries.</summary>
    public IReadOnlyList<string> Countries { get; set; } = [];

    /// <summary>Gets or sets the directors.</summary>
    public IReadOnlyList<CreditRecord> Directors { get; set; } = [];

    /// <summary>Gets or sets the writers.</summary>
    public IReadOnlyList<CreditRecord> Writers { get; set; } = [];

    /// <summary>Gets or sets the top-billed cast (about 10; episodes add guest stars).</summary>
    public IReadOnlyList<CreditRecord> Cast { get; set; } = [];
}

/// <summary>What happened when a live session was saved.</summary>
public enum SaveOutcome
{
    /// <summary>Stored as a new viewing.</summary>
    Added,

    /// <summary>Joined onto an unfinished viewing it continues (paused, switched device, stream restarted).</summary>
    Continued,

    /// <summary>Too short to keep, and not a continuation.</summary>
    Skipped,
}

/// <summary>One viewing session.</summary>
public sealed class SessionRecord
{
    /// <summary>Gets or sets the row ID.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the user ID (N format).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item ID (N format).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source; see <see cref="SessionSource"/>.</summary>
    public string Source { get; set; } = SessionSource.Live;

    /// <summary>Gets or sets when it started, UTC. Null when an import doesn't know.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Gets or sets when it ended, UTC.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Gets or sets the seconds actually watched (skipped-over parts excluded).</summary>
    public int WatchedSeconds { get; set; }

    /// <summary>Gets or sets where playback was when the session ended, in seconds.</summary>
    public int? PositionSeconds { get; set; }

    /// <summary>Gets or sets where playback was when the session started, in seconds (resuming starts mid-way).</summary>
    public int? StartPositionSeconds { get; set; }

    /// <summary>Gets or sets the distinct seconds of the item seen (rewatched parts count once).</summary>
    public int? CoveredSeconds { get; set; }

    /// <summary>Gets or sets the parts seen, as merged [start, end] ranges in seconds.</summary>
    public List<int[]> Segments { get; set; } = [];

    /// <summary>Gets or sets the runtime in seconds at the time of watching.</summary>
    public int? RuntimeSeconds { get; set; }

    /// <summary>Gets or sets the share of the runtime seen, 0 to 1 (based on distinct parts seen).</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets a value indicating whether at least 85% of the item has been seen.</summary>
    public bool Finished { get; set; }

    /// <summary>Gets or sets the play count, for user-data imports.</summary>
    public int? PlayCount { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the client app name.</summary>
    public string? ClientName { get; set; }

    /// <summary>Gets or sets a unique key for imported rows, so re-running an import doesn't duplicate.</summary>
    public string? ImportKey { get; set; }
}
