<p align="center">
  <img src="logo.svg" alt="ExplicitFin" width="128" height="128">
</p>

# ExplicitFin: Mark Your Songs

<p align="center">
  <img src="backdrop.svg" alt="ExplicitFin backdrop" width="100%">
</p>

A Jellyfin plugin that marks **explicit track titles** via **Deezer** (album tracklists first), with **MusicBrainz** as a sparse fallback. Local spelling is never corrected - only your configured mark is added or removed.

**Jellyfin 10.11+** · runs as a scheduled task.

## How it works

1. Group library tracks by album. Resolve each album once on Deezer (album search + tracklist), then match local titles against that list.
2. Unmatched tracks fall back to per-track Deezer search, then MusicBrainz. Responses are disk-cached (~7 days); identical lookups are memoized within a run.
3. Strip your mark (and a trailing ` - Artist` if present) for matching only - the local spelling is never rewritten to the catalog title.
4. If treated as explicit, append or prepend your mark (default **🅴**) on the existing title - e.g. `God is reawlly real [E] - AJR` - and optionally add Jellyfin tags (default **Explicit**).
5. Every rename is logged and appended to `ExplicitFin-changes.log` under the plugin configurations folder.

**Fast path:** leave Deezer enabled first (default). MusicBrainz is much slower (~1 req/s) and only runs when Deezer finds nothing.

When both explicit and clean versions match, use the dashboard setting: prefer explicit, prefer clean, or don't touch.

## Installing
**Step 1**
<p align="center">
  <img src="repo_graphics/plugins.jpg" alt="Plugins Location" width="100%">
</p>

**Dashboard --> Plugins --> Manage Repositories** --> **+ New Repository**:
   - Name: `FinPlugins` (or whatever :P )
   - URL: `https://raw.githubusercontent.com/TidBits16/FinPlugins/main/manifest.json`
   <br>
   (p.s. this bundle includes my other FinPlugins since they are designed to work together. ***they are not required to install!***)
<br>
<center><strong>**Then Restart JellyFin!**</strong></center>

**Step 2**
<p align="center">
  <img src="repo_graphics/where_to_find.jpg" alt="Where To Find Repo" width="100%">
</p>

**Plugins** --> **All** --> **ExplicitFin: Mark Your Songs** --> **Install**

<center><strong>**Once Installed, Restart JellyFin Again!**</strong></center>

## Build Locally

For development or packaging your own build:

```bash
dotnet build Jellyfin.Plugin.ExplicitTagger.csproj -c Release
./scripts/package.sh
```

The release zip will be in `dist/`.

Designed for **Jellyfin 10.11+** (you probably have this already :D)
<p align="center">
  <a href="https://github.com/TidBits16/FinPlugins">
    <img src="repo_graphics/fin-family.svg" alt="Fin plugins" width="360">
  </a>
</p>

