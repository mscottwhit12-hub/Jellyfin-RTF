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
}
