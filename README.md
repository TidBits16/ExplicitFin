# ExplicitFin: Visually Mark Your Songs

A Jellyfin plugin that marks **explicit track titles** by searching **Deezer**, then **MusicBrainz**, using title + artist + album (90% match minimum).

**Jellyfin 10.11+** · runs as a scheduled task.

## How it works

1. For each album, for each track: strip your configured mark from the title, then search Deezer.
2. Keep hits that match title, artist, and album at ≥90% similarity.
3. If Deezer finds nothing usable, try MusicBrainz the same way.
4. If the track is treated as explicit, append or prepend your mark (default **🅴**) and add Jellyfin tags (default **Explicit**) for filtering.
5. Every rename is written to the Jellyfin log and to `ExplicitFin-changes.log` under the plugin configurations folder.

When both explicit and clean versions match, use the dashboard setting: prefer explicit, prefer clean, or don't touch.

## Install

1. **Dashboard → Plugins → Repositories** → add:
   - Name: `ExplicitFin`
   - URL: `https://raw.githubusercontent.com/TidBits16/ExplicitFin/main/manifest.json`
2. **Catalog** → refresh → install **ExplicitFin: Visually Mark Your Songs** → restart when asked.
3. Configure under **Plugins → ExplicitFin: Visually Mark Your Songs**, or run from **Scheduled Tasks**.

## With Deezer Genres

Install both if you want Deezer genres and explicit title marks. They run as separate scheduled tasks.
