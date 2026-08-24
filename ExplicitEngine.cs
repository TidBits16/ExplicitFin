using System;
using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExplicitTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagger;

public class ExplicitEngine
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IPlaylistManager _playlists;
    private readonly DeezerExplicitClient _deezer;
    private readonly MusicBrainzExplicitClient _musicBrainz;
    private readonly AppleMusicExplicitClient _appleMusic;
    private readonly MediaBrowser.Common.Configuration.IApplicationPaths _paths;
    private readonly ILogger<ExplicitEngine> _logger;

    public ExplicitEngine(
        ILibraryManager library,
        IUserManager users,
        IPlaylistManager playlists,
        DeezerExplicitClient deezer,
        MusicBrainzExplicitClient musicBrainz,
        AppleMusicExplicitClient appleMusic,
        MediaBrowser.Common.Configuration.IApplicationPaths paths,
        ILogger<ExplicitEngine> logger)
    {
        _library = library;
        _users = users;
        _playlists = playlists;
        _deezer = deezer;
        _musicBrainz = musicBrainz;
        _appleMusic = appleMusic;
        _paths = paths;
        _logger = logger;
    }

    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var workers = cfg.Workers <= 0 ? Environment.ProcessorCount : cfg.Workers;
        workers = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount));
        Titles.UseStyle(cfg.ExplicitMark, cfg.PrependExplicitMark);
        try
        {
            using var gate = new SemaphoreSlim(workers, workers);

            var tracks = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            }).OfType<Audio>().Where(t => t.Id != Guid.Empty).ToList();

            var albums = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                Recursive = true
            }).OfType<MusicAlbum>().Where(a => a.Id != Guid.Empty).ToList();

            _logger.LogInformation("ExplicitFin: {Tracks} tracks, {Albums} albums, {Workers} workers", tracks.Count, albums.Count, workers);

            var patches = new ConcurrentDictionary<Guid, Patch>();
            void Queue(Patch p)
            {
                if (p.Empty)
                {
                    return;
                }

                patches.AddOrUpdate(p.ItemId, p, (_, existing) => existing.Merge(p));
            }

            var tracksByAlbum = GroupTracksByAlbum(tracks, albums);

            progress.Report(10);

            var deezerTrackCache = new ConcurrentDictionary<int, bool?>();
            var musicBrainzCache = new ConcurrentDictionary<string, bool?>();
            var appleMusicCache = new ConcurrentDictionary<long, bool?>();
            var trackDone = 0;
            await Task.WhenAll(tracks.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var externalExplicit = await ResolveExternalExplicitAsync(
                        item,
                        cfg,
                        deezerTrackCache,
                        musicBrainzCache,
                        appleMusicCache,
                        cancellationToken).ConfigureAwait(false);
                    var tagged = HasAnyTag(item, cfg.EffectiveExplicitTags);
                    var marked = Titles.HasExplicitMark(item.Name);
                    var explicitWrite = ResolveTagWrite(item, externalExplicit, tagged, marked, cfg);

                    var newName = TitlePatch(item.Name, externalExplicit, cfg);
                    string? albumFieldWrite = null;
                    if (cfg.RenameExplicitTitles && Titles.HasExplicitMark(item.Album ?? string.Empty))
                    {
                        var stripped = Titles.StripMark(item.Album ?? string.Empty);
                        if (stripped.Length > 0 && stripped != item.Album)
                        {
                            albumFieldWrite = stripped;
                        }
                    }

                    if (newName is null && explicitWrite is null && albumFieldWrite is null)
                    {
                        return;
                    }

                    Queue(new Patch
                    {
                        ItemId = item.Id,
                        Item = item,
                        Name = newName,
                        Album = albumFieldWrite,
                        Explicit = explicitWrite
                    });
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref trackDone);
                    progress.Report(10 + 60.0 * n / Math.Max(1, tracks.Count));
                }
            })).ConfigureAwait(false);

            progress.Report(75);
            foreach (var album in albums)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var albumTracks = tracksByAlbum.GetValueOrDefault(album.Id) ?? [];
                var albumName = album.Name ?? string.Empty;
                var albumExplicit = albumTracks.Any(track =>
                    IsTrackExplicit(track, patches.TryGetValue(track.Id, out var trackPatch) ? trackPatch : null, cfg));

                string? nameWrite = null;
                if (cfg.MarkExplicitAlbums)
                {
                    nameWrite = AlbumTitlePatch(albumName, albumExplicit);
                }

                bool? explicitWrite = null;
                if (HasAnyTag(album, cfg.EffectiveExplicitTags))
                {
                    explicitWrite = false;
                }

                if (nameWrite is not null || explicitWrite is not null)
                {
                    Queue(new Patch
                    {
                        ItemId = album.Id,
                        Item = album,
                        Name = nameWrite,
                        Explicit = explicitWrite
                    });
                }

                var canonicalAlbumName = Titles.StripMark(nameWrite ?? albumName);
                if (canonicalAlbumName.Length > 0)
                {
                    foreach (var track in albumTracks)
                    {
                        var current = track.Album ?? string.Empty;
                        if (current != canonicalAlbumName)
                        {
                            Queue(new Patch
                            {
                                ItemId = track.Id,
                                Item = track,
                                Album = canonicalAlbumName
                            });
                        }
                    }
                }
            }

            progress.Report(85);
            var list = patches.Values.ToList();
            var applyDone = 0;
            await Task.WhenAll(list.Select(async p =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ApplyPatchAsync(p, cfg, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ExplicitFin failed to update {Id}", p.ItemId);
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref applyDone);
                    progress.Report(85 + 10.0 * n / Math.Max(1, list.Count));
                }
            })).ConfigureAwait(false);

            try
            {
                var repair = new PlaylistRepair(_playlists, _users, _paths, _logger);
                var (plans, states) = await repair.PlanAsync(tracks, cancellationToken).ConfigureAwait(false);
                repair.SaveSnapshot(states);
                foreach (var plan in plans.Where(p => p.NeedsWrite))
                {
                    try
                    {
                        await repair.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("ExplicitFin rewrote playlist {Name} ({Live} → {Desired})", plan.Name, plan.LiveIds.Count, plan.DesiredIds.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ExplicitFin playlist {Name} failed", plan.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExplicitFin playlist repair skipped");
            }

            progress.Report(100);
            _logger.LogInformation(
                "ExplicitFin finished: {Patches} writes, Deezer http {Dz}/{DzC}, MusicBrainz http {Mb}/{MbC}, Apple Music http {Am}/{AmC}",
                list.Count,
                _deezer.HttpCount,
                _deezer.CacheHits,
                _musicBrainz.HttpCount,
                _musicBrainz.CacheHits,
                _appleMusic.HttpCount,
                _appleMusic.CacheHits);
        }
        finally
        {
            Titles.ResetStyle();
        }
    }

    private async Task<bool?> ResolveExternalExplicitAsync(
        Audio item,
        PluginConfiguration cfg,
        ConcurrentDictionary<int, bool?> deezerTrackCache,
        ConcurrentDictionary<string, bool?> musicBrainzCache,
        ConcurrentDictionary<long, bool?> appleMusicCache,
        CancellationToken cancellationToken)
    {
        if (cfg.UseDeezer)
        {
            var deezer = await ResolveDeezerAsync(item, deezerTrackCache, cancellationToken).ConfigureAwait(false);
            if (deezer is not null)
            {
                return deezer;
            }
        }

        if (cfg.UseMusicBrainz)
        {
            var musicBrainz = await ResolveMusicBrainzAsync(item, musicBrainzCache, cancellationToken).ConfigureAwait(false);
            if (musicBrainz is not null)
            {
                return musicBrainz;
            }
        }

        if (cfg.UseAppleMusic)
        {
            var appleMusic = await ResolveAppleMusicAsync(item, appleMusicCache, cancellationToken).ConfigureAwait(false);
            if (appleMusic is not null)
            {
                return appleMusic;
            }
        }

        return null;
    }

    private async Task<bool?> ResolveDeezerAsync(
        Audio item,
        ConcurrentDictionary<int, bool?> cache,
        CancellationToken cancellationToken)
    {
        var idText = item.GetProviderId("Deezer");
        if (!int.TryParse(idText, out var trackId) || trackId <= 0)
        {
            return null;
        }

        if (cache.TryGetValue(trackId, out var hit))
        {
            return hit;
        }

        var value = await _deezer.GetTrackExplicitAsync(trackId, cancellationToken).ConfigureAwait(false);
        cache[trackId] = value;
        return value;
    }

    private static Dictionary<Guid, List<Audio>> GroupTracksByAlbum(IReadOnlyList<Audio> tracks, IReadOnlyList<MusicAlbum> albums)
    {
        var map = albums.ToDictionary(a => a.Id, _ => new List<Audio>());
        var albumsByName = albums
            .GroupBy(a => Titles.StripMark(a.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (track.GetParent() is MusicAlbum parent && map.ContainsKey(parent.Id))
            {
                map[parent.Id].Add(track);
                continue;
            }

            var albumName = Titles.StripMark(track.Album ?? string.Empty).Trim();
            if (albumName.Length > 0 && albumsByName.TryGetValue(albumName, out var match))
            {
                map[match.Id].Add(track);
            }
        }

        return map;
    }

    private static bool IsTrackExplicit(Audio track, Patch? patch, PluginConfiguration cfg)
    {
        if (patch?.Explicit == true)
        {
            return true;
        }

        if (patch?.Explicit == false)
        {
            return false;
        }

        if (patch?.Name is not null)
        {
            return Titles.HasExplicitMark(patch.Name);
        }

        return HasAnyTag(track, cfg.EffectiveExplicitTags) || Titles.HasExplicitMark(track.Name);
    }

    private static string? AlbumTitlePatch(string current, bool explicitFlag)
    {
        var desired = Titles.DesiredTitle(current, explicitFlag);
        return current == desired ? null : desired;
    }

    private async Task<bool?> ResolveMusicBrainzAsync(
        Audio item,
        ConcurrentDictionary<string, bool?> cache,
        CancellationToken cancellationToken)
    {
        var recordingId = item.GetProviderId("MusicBrainzRecording")
            ?? item.GetProviderId("MusicBrainz");

        if (string.IsNullOrWhiteSpace(recordingId))
        {
            var trackId = item.GetProviderId("MusicBrainzTrack");
            if (!string.IsNullOrWhiteSpace(trackId))
            {
                recordingId = await _musicBrainz.ResolveRecordingIdFromTrackAsync(trackId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrWhiteSpace(recordingId))
        {
            return null;
        }

        recordingId = recordingId.Trim();
        if (cache.TryGetValue(recordingId, out var hit))
        {
            return hit;
        }

        var value = await _musicBrainz.GetRecordingExplicitAsync(recordingId, cancellationToken).ConfigureAwait(false);
        cache[recordingId] = value;
        return value;
    }

    private async Task<bool?> ResolveAppleMusicAsync(
        Audio item,
        ConcurrentDictionary<long, bool?> cache,
        CancellationToken cancellationToken)
    {
        var idText = item.GetProviderId("Apple Music")
            ?? item.GetProviderId("iTunes");
        if (!long.TryParse(idText, out var trackId) || trackId <= 0)
        {
            return null;
        }

        if (cache.TryGetValue(trackId, out var hit))
        {
            return hit;
        }

        var value = await _appleMusic.GetTrackExplicitAsync(trackId, cancellationToken).ConfigureAwait(false);
        cache[trackId] = value;
        return value;
    }

    private static bool HasExternalSource(PluginConfiguration cfg)
        => cfg.UseDeezer || cfg.UseMusicBrainz || cfg.UseAppleMusic;

    private static bool? ResolveTagWrite(Audio item, bool? externalExplicit, bool tagged, bool marked, PluginConfiguration cfg)
    {
        if (!cfg.WriteExplicitTags || cfg.EffectiveExplicitTags.Count == 0)
        {
            return null;
        }

        var hasExternalSource = HasExternalSource(cfg);

        if (hasExternalSource && externalExplicit == true && !tagged)
        {
            return true;
        }

        if (hasExternalSource && externalExplicit == false && (tagged || marked))
        {
            return false;
        }

        if (hasExternalSource && externalExplicit == true && cfg.EffectiveExplicitTags.Any(t => !HasTag(item, t)))
        {
            return true;
        }

        return null;
    }

    private static string? TitlePatch(string current, bool? explicitFlag, PluginConfiguration cfg)
    {
        if (!cfg.RenameExplicitTitles || explicitFlag is null)
        {
            return null;
        }

        var desired = Titles.DesiredTitle(current, explicitFlag.Value);
        return current == desired ? null : desired;
    }

    private async Task ApplyPatchAsync(Patch p, PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        var item = p.Item ?? _library.GetItemById(p.ItemId);
        if (item is null)
        {
            return;
        }

        var dirty = false;
        if (p.Name is not null && item.Name != p.Name)
        {
            item.Name = p.Name;
            dirty = true;
        }

        if (p.Album is not null && item is Audio audio && audio.Album != p.Album)
        {
            audio.Album = p.Album;
            dirty = true;
        }

        var tags = item.Tags.ToList();
        var tagDirty = false;
        if (p.Explicit is not null)
        {
            var names = cfg.EffectiveExplicitTags;
            tags = tags.Where(t => !names.Any(n => t.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (p.Explicit.Value)
            {
                foreach (var n in names)
                {
                    if (!tags.Any(t => t.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    {
                        tags.Add(n);
                    }
                }
            }

            tagDirty = true;
        }

        if (tagDirty)
        {
            item.Tags = tags.ToArray();
            dirty = true;
        }

        if (dirty)
        {
            await _library.UpdateItemAsync(item, item.GetParent() ?? item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasAnyTag(BaseItem item, IReadOnlyList<string> names)
        => names.Any(n => HasTag(item, n));

    private static bool HasTag(BaseItem item, string name)
        => item.Tags.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed class Patch
    {
        public Guid ItemId { get; init; }

        public BaseItem? Item { get; init; }

        public string? Name { get; init; }

        public string? Album { get; init; }

        public bool? Explicit { get; init; }

        public bool Empty => Name is null && Album is null && Explicit is null;

        public Patch Merge(Patch src) => new()
        {
            ItemId = ItemId,
            Item = Item ?? src.Item,
            Name = src.Name ?? Name,
            Album = src.Album ?? Album,
            Explicit = src.Explicit ?? Explicit
        };
    }
}
