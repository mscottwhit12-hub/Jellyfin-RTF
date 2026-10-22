using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RaceToFinish.Configuration;

/// <summary>
/// Admin settings. v0.1 has only display options; challenges, Seerr and TMDB settings arrive in later versions.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the popup uses the frosted-glass blur.
    /// Turn off if a TV struggles with it; a solid panel is used instead.
    /// </summary>
    public bool UseBlur { get; set; } = true;
}
