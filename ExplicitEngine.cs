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
    private readonly MediaBrowser.Common.Configuration.IApplicationPaths _paths;
    private readonly ILogger<ExplicitEngine> _logger;

    public ExplicitEngine(
        ILibraryManager library,
        IUserManager users,
        IPlaylistManager playlists,
        DeezerExplicitClient deezer,
        MediaBrowser.Common.Configuration.IApplicationPaths paths,
        ILogger<ExplicitEngine> logger)
    {
        _library = library;
        _users = users;
        _playlists = playlists;
        _deezer = deezer;
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

            _logger.LogInformation("Explicit Tagger: {Tracks} tracks, {Albums} albums, {Workers} workers", tracks.Count, albums.Count, workers);

            var patches = new ConcurrentDictionary<Guid, Patch>();
            void Queue(Patch p)
            {
                if (p.Empty)
                {
                    return;
                }

                patches.AddOrUpdate(p.ItemId, p, (_, existing) => existing.Merge(p));
            }

            foreach (var album in albums)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var albumName = album.Name ?? string.Empty;
                var tagged = HasAnyTag(album, cfg.EffectiveExplicitTags);
                string? nameWrite = null;
                if (cfg.RenameExplicitTitles && Titles.HasExplicitMark(albumName))
                {
                    var stripped = Titles.StripMark(albumName);
                    if (stripped.Length > 0 && stripped != albumName)
                    {
                        nameWrite = stripped;
                    }
                }

                if (nameWrite is null && !tagged)
                {
                    continue;
                }

                Queue(new Patch
                {
                    ItemId = album.Id,
                    Item = album,
                    Name = nameWrite,
                    Explicit = tagged ? false : null
                });
            }

            progress.Report(10);

            var deezerCache = new ConcurrentDictionary<int, bool?>();
            var trackDone = 0;
            await Task.WhenAll(tracks.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var deezerExplicit = await ResolveDeezerAsync(item, cfg, deezerCache, cancellationToken).ConfigureAwait(false);
                    var tagged = cfg.UseExistingTags && HasAnyTag(item, cfg.EffectiveExplicitTags);
                    var marked = Titles.HasExplicitMark(item.Name);
                    var explicitForTitle = ResolveTitleExplicit(deezerExplicit, tagged, cfg);
                    var explicitWrite = ResolveTagWrite(item, deezerExplicit, tagged, marked, cfg);

                    var newName = TitlePatch(item.Name, explicitForTitle, cfg);
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
                    progress.Report(10 + 70.0 * n / Math.Max(1, tracks.Count));
                }
            })).ConfigureAwait(false);

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
                    _logger.LogWarning(ex, "Explicit Tagger failed to update {Id}", p.ItemId);
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
                        _logger.LogInformation("Explicit Tagger rewrote playlist {Name} ({Live} → {Desired})", plan.Name, plan.LiveIds.Count, plan.DesiredIds.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Explicit Tagger playlist {Name} failed", plan.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Explicit Tagger playlist repair skipped");
            }

            progress.Report(100);
            _logger.LogInformation(
                "Explicit Tagger finished: {Patches} writes, Deezer http {Dz}/{DzC} cache",
                list.Count,
                _deezer.HttpCount,
                _deezer.CacheHits);
        }
        finally
        {
            Titles.ResetStyle();
        }
    }

    private async Task<bool?> ResolveDeezerAsync(
        Audio item,
        PluginConfiguration cfg,
        ConcurrentDictionary<int, bool?> cache,
        CancellationToken cancellationToken)
    {
        if (!cfg.UseDeezer)
        {
            return null;
        }

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

    private static bool? ResolveTitleExplicit(bool? deezerExplicit, bool tagged, PluginConfiguration cfg)
    {
        if (deezerExplicit is not null)
        {
            return deezerExplicit;
        }

        if (cfg.UseExistingTags && tagged)
        {
            return true;
        }

        return null;
    }

    private static bool? ResolveTagWrite(Audio item, bool? deezerExplicit, bool tagged, bool marked, PluginConfiguration cfg)
    {
        if (!cfg.WriteExplicitTags || cfg.EffectiveExplicitTags.Count == 0)
        {
            return null;
        }

        if (cfg.UseDeezer && deezerExplicit == true && !tagged)
        {
            return true;
        }

        if (cfg.UseDeezer && deezerExplicit == false && (tagged || marked))
        {
            return false;
        }

        if (cfg.UseDeezer && deezerExplicit == true && cfg.EffectiveExplicitTags.Any(t => !HasTag(item, t)))
        {
            return true;
        }

        if (!cfg.UseDeezer && cfg.UseExistingTags && tagged)
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
