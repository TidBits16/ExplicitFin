using System.Collections.Concurrent;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExplicitTagger.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagger;

public class ExplicitEngine
{
    private readonly ILibraryManager _library;
    private readonly DeezerExplicitClient _deezer;
    private readonly MusicBrainzExplicitClient _musicBrainz;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ExplicitEngine> _logger;
    private int _forceNext;

    public ExplicitEngine(
        ILibraryManager library,
        DeezerExplicitClient deezer,
        MusicBrainzExplicitClient musicBrainz,
        IApplicationPaths paths,
        ILogger<ExplicitEngine> logger)
    {
        _library = library;
        _deezer = deezer;
        _musicBrainz = musicBrainz;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Next scheduled run overwrites every track from catalogs.</summary>
    public void RequestForce() => Interlocked.Exchange(ref _forceNext, 1);

    public Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var force = Interlocked.Exchange(ref _forceNext, 0) == 1;
        return RunAsync(force, progress, cancellationToken);
    }

    public async Task RunAsync(bool force, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var workers = cfg.Workers <= 0 ? Environment.ProcessorCount : cfg.Workers;
        workers = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount));
        Titles.UseStyle(cfg.ExplicitMark, cfg.PrependExplicitMark);
        var seen = new SeenStore(Path.Combine(_paths.PluginConfigurationsPath, "ExplicitFin-seen.txt"));
        try
        {
            var tracks = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            }).OfType<Audio>().Where(t => t.Id != Guid.Empty).ToList();

            var albums = GroupByAlbum(tracks);
            _logger.LogInformation(
                "ExplicitFin: {Tracks} tracks across {Albums} albums, {Workers} workers ({Mode})",
                tracks.Count,
                albums.Count,
                workers,
                force ? "force all" : "new only");

            var changeLogPath = Path.Combine(_paths.PluginConfigurationsPath, "ExplicitFin-changes.log");
            var searchMemo = new ConcurrentDictionary<string, (ExplicitSearchResult Result, string Source)>(
                StringComparer.Ordinal);
            var totalWrites = 0;
            var completed = 0;
            var totalTracks = Math.Max(1, tracks.Count);

            using var gate = new SemaphoreSlim(workers, workers);
            await Task.WhenAll(albums.Select(async albumGroup =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var writes = await ProcessAlbumAsync(
                        albumGroup,
                        cfg,
                        force,
                        seen,
                        changeLogPath,
                        searchMemo,
                        () =>
                        {
                            var n = Interlocked.Increment(ref completed);
                            progress.Report(100.0 * n / totalTracks);
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (writes > 0)
                    {
                        Interlocked.Add(ref totalWrites, writes);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ExplicitFin failed on album {Album}", albumGroup.AlbumName);
                    foreach (var _ in albumGroup.Tracks)
                    {
                        var n = Interlocked.Increment(ref completed);
                        progress.Report(100.0 * n / totalTracks);
                    }
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            progress.Report(100);
            _logger.LogInformation(
                "ExplicitFin finished: {Writes} title updates, Deezer http {Dz}/{DzCache} cache, MusicBrainz http {Mb}/{MbCache} cache",
                totalWrites,
                _deezer.HttpCount,
                _deezer.CacheHits,
                _musicBrainz.HttpCount,
                _musicBrainz.CacheHits);
        }
        finally
        {
            seen.Save();
            Titles.ResetStyle();
        }
    }

    private async Task<int> ProcessAlbumAsync(
        AlbumGroup albumGroup,
        PluginConfiguration cfg,
        bool force,
        SeenStore seen,
        string changeLogPath,
        ConcurrentDictionary<string, (ExplicitSearchResult Result, string Source)> searchMemo,
        Action onTrackDone,
        CancellationToken cancellationToken)
    {
        var writes = 0;
        var pending = force
            ? albumGroup.Tracks
            : albumGroup.Tracks.Where(t => !seen.Contains(t.Id)).ToList();

        if (pending.Count == 0)
        {
            foreach (var _ in albumGroup.Tracks)
            {
                onTrackDone();
            }

            return 0;
        }

        var albumName = albumGroup.AlbumName;
        var threshold = cfg.EffectiveMinTitleSimilarity;
        var providers = cfg.EffectiveMetadataProviders;
        if (providers.Count == 0)
        {
            providers = PluginConfiguration.AllProvidersInOrder;
        }

        IReadOnlyList<DeezerAlbumTrack>? deezerAlbumTracks = null;
        bool? deezerAlbumExplicit = null;
        var deezerEnabled = providers.Contains(MetadataProvider.Deezer);
        if (deezerEnabled && FuzzyMatch.Normalize(albumName).Length > 0)
        {
            var albumArtist = PrimaryAlbumArtist(albumGroup.Tracks);
            var deezerAlbum = await _deezer.LoadAlbumAsync(
                albumName,
                albumArtist,
                threshold,
                cancellationToken).ConfigureAwait(false);
            deezerAlbumTracks = deezerAlbum.Tracks;
            deezerAlbumExplicit = deezerAlbum.Explicit;
        }

        var pendingSet = pending.Count == albumGroup.Tracks.Count
            ? null
            : pending.ToHashSet();
        foreach (var track in albumGroup.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingSet is not null && !pendingSet.Contains(track))
            {
                onTrackDone();
                continue;
            }

            try
            {
                var (changed, decided) = await ProcessTrackAsync(
                    track,
                    albumName,
                    cfg,
                    force,
                    changeLogPath,
                    deezerAlbumTracks,
                    searchMemo,
                    cancellationToken).ConfigureAwait(false);
                if (decided)
                {
                    seen.Add(track.Id);
                }

                if (changed)
                {
                    writes++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExplicitFin failed on track {Id} ({Name})", track.Id, track.Name);
            }
            finally
            {
                onTrackDone();
            }
        }

        try
        {
            if (await ProcessAlbumItemAsync(
                    albumGroup,
                    cfg,
                    force,
                    deezerAlbumExplicit,
                    changeLogPath,
                    cancellationToken).ConfigureAwait(false))
            {
                writes++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExplicitFin failed marking album {Album}", albumGroup.AlbumName);
        }

        return writes;
    }

    private async Task<(bool Wrote, bool Decided)> ProcessTrackAsync(
        Audio track,
        string albumName,
        PluginConfiguration cfg,
        bool force,
        string changeLogPath,
        IReadOnlyList<DeezerAlbumTrack>? deezerAlbumTracks,
        ConcurrentDictionary<string, (ExplicitSearchResult Result, string Source)> searchMemo,
        CancellationToken cancellationToken)
    {
        var artist = PrimaryArtist(track);
        var searchTitle = Titles.StripTrailingArtist(
            Titles.StripMark(track.Name ?? string.Empty, artist),
            artist);
        if (searchTitle.Length == 0)
        {
            return (false, false);
        }

        var album = Titles.StripMark(
            string.IsNullOrWhiteSpace(albumName) ? (track.Album ?? string.Empty) : albumName);

        bool? decision;
        string source;
        if (!force && cfg.KeepExistingMarks && IsManuallyExplicit(track, track.Name, cfg))
        {
            decision = true;
            source = "manual";
        }
        else
        {
            ExplicitSearchResult result;
            var albumHit = deezerAlbumTracks is { Count: > 0 }
                ? DeezerExplicitClient.MatchOnAlbum(searchTitle, deezerAlbumTracks, cfg.EffectiveMinTitleSimilarity)
                : null;
            if (albumHit is not null)
            {
                result = albumHit;
                source = "deezer-album";
            }
            else
            {
                (result, source) = await ResolveAsync(searchTitle, artist, album, cfg, searchMemo, cancellationToken)
                    .ConfigureAwait(false);
            }

            decision = Decide(result, cfg.EffectiveDualVersionPreference);
        }

        if (decision is null)
        {
            return (false, false);
        }

        var desired = Titles.DesiredTitle(track.Name ?? string.Empty, decision.Value, artist);
        var nameChanged = desired.Length > 0 && !string.Equals(track.Name, desired, StringComparison.Ordinal);
        var tagsChanged = ApplyExplicitTags(track, decision.Value, cfg);

        if (!nameChanged && !tagsChanged)
        {
            return (false, true);
        }

        var oldName = track.Name ?? string.Empty;
        if (nameChanged)
        {
            track.Name = desired;
        }

        await _library.UpdateItemAsync(
            track,
            track.GetParent() ?? track,
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);

        var decisionLabel = decision.Value ? "explicit" : "clean";
        if (nameChanged)
        {
            _logger.LogInformation(
                "ExplicitFin renamed {Id}: {Old} --> {New} ({Source}, {Decision})",
                track.Id,
                oldName,
                desired,
                source,
                decisionLabel);

            AppendChangeLog(changeLogPath, track.Id, oldName, desired, source, decisionLabel);
        }
        else
        {
            _logger.LogInformation(
                "ExplicitFin updated tags on {Id} ({Name}) ({Source}, {Decision})",
                track.Id,
                track.Name,
                source,
                decisionLabel);
        }

        return (true, true);
    }

    private async Task<bool> ProcessAlbumItemAsync(
        AlbumGroup albumGroup,
        PluginConfiguration cfg,
        bool force,
        bool? deezerAlbumExplicit,
        string changeLogPath,
        CancellationToken cancellationToken)
    {
        if (!cfg.MarkAlbums)
        {
            return false;
        }

        var album = albumGroup.Album;
        var sourceName = album?.Name ?? albumGroup.AlbumName;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = albumGroup.Tracks
                .Select(t => t.Album)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? string.Empty;
        }

        if (Titles.StripMark(sourceName).Length == 0)
        {
            return false;
        }

        var explicitTracks = albumGroup.Tracks.Count(t => Titles.HasExplicitMark(t.Name ?? string.Empty));
        var isExplicit = DecideAlbum(deezerAlbumExplicit, explicitTracks, cfg.EffectiveAlbumMinExplicitTracks);
        if (!force
            && cfg.KeepExistingMarks
            && (Titles.HasExplicitMark(sourceName) || (album is not null && HasExplicitTag(album, cfg))))
        {
            isExplicit = true;
        }
        var desired = Titles.DesiredTitle(sourceName, isExplicit);
        if (desired.Length == 0)
        {
            return false;
        }

        var changed = false;
        if (album is not null)
        {
            var nameChanged = !string.Equals(album.Name, desired, StringComparison.Ordinal);
            var tagsChanged = ApplyExplicitTags(album, isExplicit, cfg);
            if (nameChanged || tagsChanged)
            {
                var oldName = album.Name ?? string.Empty;
                if (nameChanged)
                {
                    album.Name = desired;
                }

                await _library.UpdateItemAsync(
                    album,
                    album.GetParent() ?? album,
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
                changed = true;

                var decisionLabel = isExplicit ? "explicit" : "clean";
                if (nameChanged)
                {
                    _logger.LogInformation(
                        "ExplicitFin renamed album {Id}: {Old} --> {New} (deezer-album={Deezer}, tracks={Tracks}, {Decision})",
                        album.Id,
                        oldName,
                        desired,
                        deezerAlbumExplicit,
                        explicitTracks,
                        decisionLabel);
                    AppendChangeLog(changeLogPath, album.Id, oldName, desired, "album", decisionLabel);
                }
                else
                {
                    _logger.LogInformation(
                        "ExplicitFin updated tags on album {Id} ({Name}) (deezer-album={Deezer}, tracks={Tracks}, {Decision})",
                        album.Id,
                        album.Name,
                        deezerAlbumExplicit,
                        explicitTracks,
                        decisionLabel);
                }
            }
        }

        var retargeted = 0;
        foreach (var track in albumGroup.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(track.Album, desired, StringComparison.Ordinal))
            {
                continue;
            }

            var oldAlbum = track.Album ?? string.Empty;
            track.Album = desired;
            await _library.UpdateItemAsync(
                track,
                track.GetParent() ?? track,
                ItemUpdateType.MetadataEdit,
                cancellationToken).ConfigureAwait(false);
            retargeted++;
            _logger.LogInformation(
                "ExplicitFin retargeted track {Id} ({Name}) album {Old} --> {New}",
                track.Id,
                track.Name,
                oldAlbum,
                desired);
        }

        if (retargeted > 0)
        {
            changed = true;
        }

        return changed;
    }

    private static bool IsManuallyExplicit(BaseItem item, string? name, PluginConfiguration cfg)
        => Titles.HasExplicitMark(name ?? item.Name ?? string.Empty) || HasExplicitTag(item, cfg);

    private static bool HasExplicitTag(BaseItem item, PluginConfiguration cfg)
    {
        var names = cfg.EffectiveExplicitTags;
        if (names.Count == 0)
        {
            return false;
        }

        return (item.Tags ?? []).Any(t => names.Any(n => t.Equals(n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Strips <paramref name="symbol"/> from every audio title that contains it.
    /// Returns how many titles were updated.
    /// </summary>
    public async Task<int> RemoveSymbolAsync(string? symbol, CancellationToken cancellationToken)
    {
        var mark = (symbol ?? string.Empty).Trim();
        if (mark.Length == 0)
        {
            mark = Titles.ExplicitMark;
        }

        Titles.UseStyle(mark, prepend: false);
        try
        {
            var tracks = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            }).OfType<Audio>().Where(t => t.Id != Guid.Empty).ToList();

            var updated = 0;
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artist = PrimaryArtist(track);
                var currentName = track.Name ?? string.Empty;
                var cleanedName = Titles.StripMark(currentName, artist);
                var currentAlbum = track.Album ?? string.Empty;
                var cleanedAlbum = Titles.StripMark(currentAlbum);
                var nameChanged = cleanedName.Length > 0
                    && !string.Equals(currentName, cleanedName, StringComparison.Ordinal);
                var albumChanged = cleanedAlbum.Length > 0
                    && !string.Equals(currentAlbum, cleanedAlbum, StringComparison.Ordinal);
                if (!nameChanged && !albumChanged)
                {
                    continue;
                }

                if (nameChanged)
                {
                    track.Name = cleanedName;
                }

                if (albumChanged)
                {
                    track.Album = cleanedAlbum;
                }

                await _library.UpdateItemAsync(
                    track,
                    track.GetParent() ?? track,
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
                updated++;
                if (nameChanged)
                {
                    _logger.LogInformation(
                        "ExplicitFin removed symbol from {Id}: {Old} --> {New}",
                        track.Id,
                        currentName,
                        cleanedName);
                }

                if (albumChanged)
                {
                    _logger.LogInformation(
                        "ExplicitFin removed symbol from {Id} album: {Old} --> {New}",
                        track.Id,
                        currentAlbum,
                        cleanedAlbum);
                }
            }

            var albums = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                Recursive = true
            }).OfType<MusicAlbum>().Where(a => a.Id != Guid.Empty).ToList();

            foreach (var album in albums)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = album.Name ?? string.Empty;
                var cleaned = Titles.StripMark(current);
                if (cleaned.Length == 0 || string.Equals(current, cleaned, StringComparison.Ordinal))
                {
                    continue;
                }

                album.Name = cleaned;
                await _library.UpdateItemAsync(
                    album,
                    album.GetParent() ?? album,
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
                updated++;
                _logger.LogInformation(
                    "ExplicitFin removed symbol from album {Id}: {Old} --> {New}",
                    album.Id,
                    current,
                    cleaned);
            }

            _logger.LogInformation("ExplicitFin RemoveSymbol finished: {Count} titles updated", updated);
            return updated;
        }
        finally
        {
            Titles.ResetStyle();
        }
    }

    private static bool ApplyExplicitTags(BaseItem item, bool isExplicit, PluginConfiguration cfg)
    {
        if (!cfg.WriteExplicitTags)
        {
            return false;
        }

        var names = cfg.EffectiveExplicitTags;
        if (names.Count == 0)
        {
            return false;
        }

        var tags = (item.Tags ?? []).ToList();
        var changed = false;

        if (isExplicit)
        {
            foreach (var name in names)
            {
                if (!tags.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    tags.Add(name);
                    changed = true;
                }
            }
        }
        else
        {
            var filtered = tags
                .Where(t => !names.Any(n => t.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (filtered.Count != tags.Count)
            {
                tags = filtered;
                changed = true;
            }
        }

        if (changed)
        {
            item.Tags = tags.ToArray();
        }

        return changed;
    }

    private async Task<(ExplicitSearchResult Result, string Source)> ResolveAsync(
        string title,
        string artist,
        string album,
        PluginConfiguration cfg,
        ConcurrentDictionary<string, (ExplicitSearchResult Result, string Source)> searchMemo,
        CancellationToken cancellationToken)
    {
        var memoKey = string.Join('\u001f',
            FuzzyMatch.Normalize(title),
            FuzzyMatch.Normalize(artist),
            FuzzyMatch.Normalize(album));
        if (searchMemo.TryGetValue(memoKey, out var cached))
        {
            return cached;
        }

        var threshold = cfg.EffectiveMinTitleSimilarity;
        var providers = cfg.EffectiveMetadataProviders;
        if (providers.Count == 0)
        {
            providers = PluginConfiguration.AllProvidersInOrder;
        }

        foreach (var provider in providers)
        {
            ExplicitSearchResult result;
            string source;
            switch (provider)
            {
                case MetadataProvider.Deezer:
                    result = await _deezer.SearchAsync(title, artist, album, threshold, cancellationToken)
                        .ConfigureAwait(false);
                    source = "deezer";
                    break;
                case MetadataProvider.MusicBrainz:
                    result = await _musicBrainz.SearchAsync(title, artist, album, threshold, cancellationToken)
                        .ConfigureAwait(false);
                    source = "musicbrainz";
                    break;
                default:
                    continue;
            }

            if (result.HasAny)
            {
                var hit = (result, source);
                searchMemo[memoKey] = hit;
                return hit;
            }
        }

        var empty = (ExplicitSearchResult.Empty, "none");
        searchMemo[memoKey] = empty;
        return empty;
    }

    /// <summary>
    /// Returns true = mark explicit, false = ensure clean (no mark), null = don't touch.
    /// </summary>
    internal static bool? Decide(ExplicitSearchResult result, DualVersionMode preference)
    {
        if (!result.HasAny)
        {
            return null;
        }

        if (result.HasBoth)
        {
            return preference switch
            {
                DualVersionMode.PreferExplicit => true,
                DualVersionMode.PreferClean => false,
                _ => null
            };
        }

        if (result.HasExplicit)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Album is explicit when Deezer says so, or enough local tracks already have the symbol.
    /// </summary>
    internal static bool DecideAlbum(bool? deezerAlbumExplicit, int explicitTrackCount, int minExplicitTracks)
    {
        var needed = Math.Max(1, minExplicitTracks);
        return deezerAlbumExplicit == true || explicitTrackCount >= needed;
    }

    private static string PrimaryArtist(Audio track)
    {
        var artists = track.Artists;
        if (artists is { Count: > 0 } && !string.IsNullOrWhiteSpace(artists[0]))
        {
            return artists[0].Trim();
        }

        var albumArtists = track.AlbumArtists;
        if (albumArtists is { Count: > 0 } && !string.IsNullOrWhiteSpace(albumArtists[0]))
        {
            return albumArtists[0].Trim();
        }

        return string.Empty;
    }

    private static string PrimaryAlbumArtist(IReadOnlyList<Audio> tracks)
    {
        foreach (var track in tracks)
        {
            var albumArtists = track.AlbumArtists;
            if (albumArtists is { Count: > 0 } && !string.IsNullOrWhiteSpace(albumArtists[0]))
            {
                return albumArtists[0].Trim();
            }
        }

        foreach (var track in tracks)
        {
            var artist = PrimaryArtist(track);
            if (artist.Length > 0)
            {
                return artist;
            }
        }

        return string.Empty;
    }

    private List<AlbumGroup> GroupByAlbum(IReadOnlyList<Audio> tracks)
    {
        var map = new Dictionary<string, AlbumGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            string key;
            string albumName;
            MusicAlbum? albumItem = ResolveAlbum(track);
            if (albumItem is not null)
            {
                key = "id:" + albumItem.Id.ToString("N");
                albumName = Titles.StripMark(albumItem.Name ?? string.Empty);
            }
            else
            {
                albumName = Titles.StripMark(track.Album ?? string.Empty);
                key = albumName.Length > 0
                    ? "name:" + albumName.ToLowerInvariant()
                    : "track:" + track.Id.ToString("N");
            }

            if (!map.TryGetValue(key, out var group))
            {
                group = new AlbumGroup(albumName, albumItem);
                map[key] = group;
            }

            group.Tracks.Add(track);
        }

        return map.Values.ToList();
    }

    private static MusicAlbum? ResolveAlbum(Audio track)
        => track.GetParent() is MusicAlbum parent && parent.Id != Guid.Empty ? parent : null;

    private void AppendChangeLog(
        string path,
        Guid itemId,
        string oldName,
        string newName,
        string source,
        string decision)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = string.Join('\t',
                DateTime.UtcNow.ToString("o"),
                itemId.ToString("N"),
                Escape(oldName),
                Escape(newName),
                source,
                decision);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExplicitFin could not write change log");
        }
    }

    private static string Escape(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private sealed class AlbumGroup
    {
        public AlbumGroup(string albumName, MusicAlbum? album)
        {
            AlbumName = albumName;
            Album = album;
        }

        public string AlbumName { get; }

        public MusicAlbum? Album { get; }

        public List<Audio> Tracks { get; } = [];
    }
}
