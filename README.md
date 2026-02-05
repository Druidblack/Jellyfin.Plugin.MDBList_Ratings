# MDBList Ratings

![logo](https://github.com/Druidblack/Jellyfin.Plugin.MDBList_Ratings/blob/main/images/logo.jpg)

Jellyfin plugin for getting ratings from a website mdblist.com for movies and TV series.
```
https://raw.githubusercontent.com/Druidblack/Jellyfin.Plugin.MDBList_Ratings/main/manifest.json
```
## Description

MDBList Ratings is a Jellyfin plugin that automatically fetches ratings from MDBList using a media item’s TMDb ID, writes selected ratings into Jellyfin’s built-in rating fields, and (optionally) enhances the Jellyfin Web UI with an “All ratings” panel showing every rating MDBList provides.

- Fetch ratings from MDBList by TMDb ID

  - Supports movies (movie) and TV shows (show).

- Write ratings into Jellyfin standard fields

  - Movies: ```CommunityRating (0–10)``` and ```CriticRating (0–100)```.

  - Shows: ```CommunityRating (0–10)```.

  - Configurable rating sources, with fallback sources if the preferred one is missing.

- Per-library overrides

  - Customize rating source mapping per Jellyfin library (Library Overrides).

- Caching & rate-limit handling

  - File cache + in-memory cache.

  - Cache interval: day / week / month.

- Scheduled task

  - “Update MDBList ratings” task to refresh ratings across the library (daily trigger by default).

- Web UI: rating source icon instead of the star

  - Displays which source was applied (including fallback logic).

- Web UI: “All ratings” panel on the Details page (from MDBList cache)

  - Shows all available ratings from MDBList without persisting them into Jellyfin metadata.

  - Modes: show all or show only selected sources in a custom order.

- Extra web-only visuals (no persistence)

  - RottenTomatoes: Certified Fresh badge for Tomatoes.

  - RottenTomatoes: Verified Hot badge for Audience.

  - Metacritic: Must-See badge.

  - AniList meanScore.

- Local icons

  - All rating icons are served locally by the plugin to speed up icon loading.

- Clickable rating icons

  - When enabled and when a link is available, icons link to provider pages:

    IMDb, Trakt, TMDb, Metacritic, RottenTomatoes, Letterboxd, RogerEbert, etc.
