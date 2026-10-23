# Race to Finish for Jellyfin

Family watch-list races. An admin picks a collection, everyone races to watch all of it, and the first three to finish win titles that show on their profile.

> **Status: v0.2 test build.** The header button and popup work, and viewing history is recorded. Challenges, standings, titles and Seerr request challenges come in the next versions.

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

Everyone shown on the login screen. Accounts set to *Hide this user from login screens* don't see the plugin and aren't tracked. Jellyfin 12 hides new accounts by default, so check **Dashboard → Users** for each person. Use one account per person.

## Viewing history and privacy

From v0.2, Race to Finish records what each person watches: when, for how long (time actually watched, not skipped), on which device, and what the title is (genres, studio, director, writers, top-billed cast).

- A movie or episode counts as *finished* once 85% of it has been seen. Skipping ahead, rewinding to rewatch a part, and "mark as played" don't add to that.
- Picking a viewing up again within 12 hours, near where you stopped, continues the same viewing, even on another device or after a server restart. Pausing for dinner or moving from the TV to bed doesn't split a movie.

- **Private by default.** Each person sees their own history in the **My Activity** tab. The admin sees nothing unless that person turns on *Share my viewing stats with the server admin*.
- **Your data, your call.** Anyone can download everything stored about them, or delete it. Deleting also stops older history from being imported again.
- **Existing history** from before the plugin was installed is imported once, automatically: Jellyfin's watched items, its activity log (kept 30 days by default), and the Playback Reporting plugin if you use it. Imported history never counts toward races.
- Whoever runs the server can technically read the data files, as with Jellyfin's own history. The database lives at `<jellyfin data>/data/race-to-finish/history.db`.

## Credits

The ready-made "Top 5 of 2020–2025" lists come from TMDB, using the TMDB key Jellyfin already has (no setup). This product uses the TMDB API but is not endorsed or certified by TMDB.

## License

MIT
