# ExplicitFin

A Jellyfin plugin that manages **explicit tags** and **title marks** on music tracks — separate from [Deezer Genres](https://github.com/TidBits16/deezer-genres).

**Jellyfin 10.11+** · runs as a scheduled task.

## Sources

- **Deezer** — look up explicit status via the Deezer provider ID on each track (when present; on by default)
- **MusicBrainz** — check recording tags and disambiguation via MusicBrainz recording or track ID
- **Apple Music** — look up `trackExplicitness` via Apple Music or iTunes provider ID

Default title mark is **🅴** (appended after the track name). Albums get the same mark when any track on them is explicit.

Never tags album entities with Jellyfin tags (only track tags).

## Install

1. **Dashboard → Plugins → Repositories** → add:
   - Name: `ExplicitFin`
   - URL: `https://cdn.jsdelivr.net/gh/TidBits16/ExplicitFin@main/manifest.json`
2. **Catalog** → install → restart when asked.
3. Configure under **Plugins → ExplicitFin**, or run from **Scheduled Tasks**.

## With Deezer Genres

Install both if you want Deezer genres and explicit tagging. They run as separate scheduled tasks.
