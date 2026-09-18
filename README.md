<div align="center">
<p align="center">
  <img src="backdrop.svg" alt="ExplicitTagShelf backdrop" width="100%">
</p>

# ExplicitTagShelf: Mark Your Songs

> <strong>LLM disclosure:</strong> This plugin is <strong>primarily developed with LLM assistance</strong> (Cursor / coding agents). Review and test before relying on it in production.

> Formerly <strong>ExplicitFin</strong>. Same plugin GUID — settings carry over when you update.

Don't you hate when your blasting your favorite song on your stereo just to remember "oh yeah, this song has a bunch of obscenities"...

Most media players do not have a standardized way to stream "explicit" tags (or atleast don't respect them).

This plugin detects if a song is considered "Explicit" against a database and appends a little `🅴` symbol at the end of a song's title.

It can also mark the <strong>album</strong> when Deezer lists it as explicit, or when enough tracks already have the symbol.

(Because no plugin is perfect), it even respects your manual edits.

<p align="center">
  <img src="repo_graphics/example.jpg" alt="Plugins Location" width="100%">
</p>

## Installing
<strong>Step 1</strong>
<p align="center">
  <img src="repo_graphics/plugins.jpg" alt="Plugins Location" width="100%">
</p>

<strong>Dashboard --> Plugins --> Manage Repositories</strong> --> <strong>+ New Repository</strong>:<br>
Name: <code>TagShelfPlugins</code> (or whatever :P )<br>
URL: <code>https://raw.githubusercontent.com/TidBits16/TagShelfPlugins/main/manifest.json</code><br>
<br>
(p.s. this bundle includes my other TagShelfPlugins since they are designed to work together. <strong><em>they are not required to install!</em></strong>)<br>
For just <strong>ExplicitTagShelf</strong> you can use this URL: <code>https://raw.githubusercontent.com/TidBits16/ExplicitTagShelf/main/manifest.json</code>
<br>
<br>
<strong>Then Restart Jellyfin!</strong>

<strong>Step 2</strong>
<p align="center">
  <img src="repo_graphics/where_to_find.jpg" alt="Where To Find Repo" width="100%">
</p>

<strong>Plugins</strong> --> <strong>All</strong> --> <strong>ExplicitTagShelf: Mark Your Songs</strong> --> <strong>Install</strong><br>
<br>
<strong>Once Installed, Restart Jellyfin Again!</strong></center>

## Build Locally

For development or packaging your own build:

```bash
dotnet build Jellyfin.Plugin.ExplicitTagShelf.csproj -c Release
./scripts/package.sh
```

The release zip will be in `dist/`.

Designed for <strong>Jellyfin 10.11+</strong> (you probably have this already :D)
<br>
Licensed under the <a href="LICENSE">GNU General Public License v3.0</a>
<p align="center">
  <a href="https://github.com/TidBits16/MusicTagShelf"><img src="repo_graphics/musictagshelf.svg" alt="MusicTagShelf" width="72" height="72"></a>
  &nbsp;
  <a href="https://github.com/TidBits16/ExplicitTagShelf"><img src="repo_graphics/explicittagshelf.svg" alt="ExplicitTagShelf" width="72" height="72"></a>
  &nbsp;
  <a href="https://github.com/TidBits16/LyricTagShelf"><img src="repo_graphics/lyrictagshelf.svg" alt="LyricTagShelf" width="72" height="72"></a>
  &nbsp;
  <a href="https://github.com/TidBits16/ArtistTagShelf"><img src="repo_graphics/artisttagshelf.svg" alt="ArtistTagShelf" width="72" height="72"></a>
</p>
</div>
