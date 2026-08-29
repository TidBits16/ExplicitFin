# ExplicitFin: Mark Your Songs

A Jellyfin plugin that marks **explicit track titles** via **Deezer** (album tracklists first), with **MusicBrainz** as a sparse fallback. Local spelling is never corrected — only your configured mark is added or removed.

**Jellyfin 10.11+** · runs as a scheduled task.

## How it works

1. Group library tracks by album. Resolve each album once on Deezer (album search + tracklist), then match local titles against that list.
2. Unmatched tracks fall back to per-track Deezer search, then MusicBrainz. Responses are disk-cached (~7 days); identical lookups are memoized within a run.
3. Strip your mark (and a trailing ` - Artist` if present) for matching only — the local spelling is never rewritten to the catalog title.
4. If treated as explicit, append or prepend your mark (default **🅴**) on the existing title — e.g. `God is reawlly real [E] - AJR` — and optionally add Jellyfin tags (default **Explicit**).
5. Every rename is logged and appended to `ExplicitFin-changes.log` under the plugin configurations folder.

**Fast path:** leave Deezer enabled first (default). MusicBrainz is much slower (~1 req/s) and only runs when Deezer finds nothing.

When both explicit and clean versions match, use the dashboard setting: prefer explicit, prefer clean, or don't touch.

## Install

1. **Dashboard → Plugins → Repositories** → add:
   - Name: `FinPlugins`
   - URL: `https://raw.githubusercontent.com/TidBits16/FinPlugins/main/manifest.json`
2. **Catalog** → refresh → install/update **ExplicitFin: Mark Your Songs** → restart when asked.
3. Configure under **Plugins → ExplicitFin: Mark Your Songs**, or run from **Scheduled Tasks**.

(That same repository URL also lists MusicFin and LyricFin.)

## With Deezer Genres

Install both if you want Deezer genres and explicit title marks. They run as separate scheduled tasks.
