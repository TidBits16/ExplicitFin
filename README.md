# Explicit Tagger

A Jellyfin plugin that manages **explicit tags** and **title marks** on music tracks — separate from metadata sync plugins like [Peanut Butter & Jelly](https://github.com/TidBits16/peanut-butter-jelly).

**Jellyfin 10.11+** · runs as a scheduled task.

## Sources

- **Existing Jellyfin tags** — honor manual `Explicit` (and other configured tag names)
- **Deezer** — look up explicit status via the Deezer provider ID on each track (e.g. written by PBJ)

Never tags album entities (Jellyfin copies album tags to all tracks).

## Install

1. **Dashboard → Plugins → Repositories** → add:
   - Name: `Explicit Tagger`
   - URL: `https://cdn.jsdelivr.net/gh/TidBits16/jellyfin-explicit-tagger@main/manifest.json`
2. **Catalog** → install → restart when asked.
3. Configure under **Plugins → Explicit Tagger**, or run from **Scheduled Tasks**.

## With Peanut Butter & Jelly

Install both. Run PBJ first to sync Deezer metadata (including Deezer IDs), then Explicit Tagger to apply tags and title marks.
