<div align="center">

<p align="center">
  <img src="logo.svg" alt="ExplicitFin" width="128" height="128">
</p>

# ExplicitFin: Mark Your Songs

<p align="center">
  <img src="backdrop.svg" alt="ExplicitFin backdrop" width="100%">
</p>

A Jellyfin plugin that marks <strong>explicit track titles</strong> via <strong>Deezer</strong> (album tracklists first), with <strong>MusicBrainz</strong> as a sparse fallback. Local spelling is never corrected - only your configured mark is added or removed.

<strong>Jellyfin 10.11+</strong> · runs as a scheduled task.

## How it works

Group library tracks by album. Resolve each album once on Deezer (album search + tracklist), then match local titles against that list.
Unmatched tracks fall back to per-track Deezer search, then MusicBrainz. Responses are disk-cached (~7 days); identical lookups are memoized within a run.
Strip your mark (and a trailing ` - Artist` if present) for matching only - the local spelling is never rewritten to the catalog title.
If treated as explicit, append or prepend your mark (default <strong>🅴</strong>) on the existing title - e.g. `God is reawlly real [E] - AJR` - and optionally add Jellyfin tags (default <strong>Explicit</strong>).
Every rename is logged and appended to `ExplicitFin-changes.log` under the plugin configurations folder.

<strong>Fast path:</strong> leave Deezer enabled first (default). MusicBrainz is much slower (~1 req/s) and only runs when Deezer finds nothing.

When both explicit and clean versions match, use the dashboard setting: prefer explicit, prefer clean, or don't touch.

## Installing
<strong>Step 1</strong>
<p align="center">
  <img src="repo_graphics/plugins.jpg" alt="Plugins Location" width="100%">
</p>

<strong>Dashboard --> Plugins --> Manage Repositories</strong> --> <strong>+ New Repository</strong>:<br>
Name: <code>FinPlugins</code> (or whatever :P )<br>
URL: <code>https://raw.githubusercontent.com/TidBits16/FinPlugins/main/manifest.json</code><br>
<br>
(p.s. this bundle includes my other FinPlugins since they are designed to work together. <strong><em>they are not required to install!</em></strong>)
<br>
<br>
<strong>Then Restart JellyFin!</strong>

<strong>Step 2</strong>
<p align="center">
  <img src="repo_graphics/where_to_find.jpg" alt="Where To Find Repo" width="100%">
</p>

<strong>Plugins</strong> --> <strong>All</strong> --> <strong>ExplicitFin: Mark Your Songs</strong> --> <strong>Install</strong><br>
<br>
<strong>Once Installed, Restart JellyFin Again!</strong></center>

## Build Locally

For development or packaging your own build:

```bash
dotnet build Jellyfin.Plugin.ExplicitTagger.csproj -c Release
./scripts/package.sh
```

The release zip will be in `dist/`.

Designed for <strong>Jellyfin 10.11+</strong> (you probably have this already :D)
<p align="center">
  <a href="https://github.com/TidBits16/FinPlugins">
    <img src="repo_graphics/fin-family.svg" alt="Fin plugins" width="360">
  </a>
</p>
</div>
