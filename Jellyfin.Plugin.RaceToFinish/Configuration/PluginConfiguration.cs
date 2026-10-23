using Jellyfin.Plugin.RaceToFinish.TopTen;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RaceToFinish.Configuration;

/// <summary>Admin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the popup uses the frosted-glass blur.
    /// Turn off if a TV struggles with it; a solid panel is used instead.
    /// </summary>
    public bool UseBlur { get; set; } = true;

    /// <summary>Gets or sets the TMDB API address. Not shown in settings; only changed for testing.</summary>
    public string TmdbApiBaseUrl { get; set; } = "https://api.themoviedb.org/3";

    /// <summary>
    /// Gets or sets the ready-made Top 5 challenges the admin has turned on, each with its list locked in at the moment
    /// it was turned on (so later TMDB rating changes can't swap films mid-race).
    /// </summary>
    public List<ReadyMadeChallenge> ReadyMadeChallenges { get; set; } = [];
}

/// <summary>A ready-made Top 5 challenge that's been turned on.</summary>
public class ReadyMadeChallenge
{
    /// <summary>Gets or sets the year of the list.</summary>
    public int Year { get; set; }

    /// <summary>Gets or sets when the admin turned it on, UTC.</summary>
    public DateTime TurnedOnUtc { get; set; }

    /// <summary>Gets or sets the vote minimum the list was made with.</summary>
    public int MinimumVotes { get; set; }

    /// <summary>Gets or sets the films, locked in, best first.</summary>
    public List<TmdbMovie> Films { get; set; } = [];
}
