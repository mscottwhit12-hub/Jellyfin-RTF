# Race to Finish for Jellyfin

Family watch-list races. An admin picks a collection, everyone races to watch all of it, and the first three to finish win titles that show on their profile.

> **Status: v0.1 test build.** The header button and popup work. Challenges, standings, titles and Seerr request challenges come in the next versions.

## Requirements

- Jellyfin **12.1** (web client, Jellyfin Media Player, and apps that run the web client, such as Samsung and LG TVs)
- [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin
- [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) plugin (for the home screen row, in a later version)

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/mscottwhit12-hub/Jellyfin-RTF/main/manifest.json
   ```
2. Open **Catalog**, find **Race to Finish**, and install it.
3. Restart Jellyfin.
4. Open **Dashboard → Plugins → Race to Finish** and check that every line of the setup check is green.

Updates show up in the dashboard like any other plugin: click **Update**, then restart.

## Who can play

Everyone shown on the login screen. Accounts set to *Hide this user from login screens* don't see the plugin.

## Credits

Movie lists for the ready-made challenges come from TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.

## License

MIT
