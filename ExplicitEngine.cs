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

    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var workers = cfg.Workers <= 0 ? Environment.ProcessorCount : cfg.Workers;
        workers = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount));
        Titles.UseStyle(cfg.ExplicitMark, cfg.PrependExplicitMark);
        try
        {
            var tracks = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            }).OfType<Audio>().Where(t => t.Id != Guid.Empty).ToList();

            var albums = GroupByAlbum(tracks);
            _logger.LogInformation(
                "ExplicitFin: {Tracks} tracks across {Albums} albums, {Workers} workers",
                tracks.Count,
                albums.Count,
                workers);

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
            Titles.ResetStyle();
        }
    }

    private async Task<int> ProcessAlbumAsync(
        AlbumGroup albumGroup,
        PluginConfiguration cfg,
        string changeLogPath,
        ConcurrentDictionary<string, (ExplicitSearchResult Result, string Source)> searchMemo,
        Action onTrackDone,
        CancellationToken cancellationToken)
    {
        var writes = 0;
        var albumName = albumGroup.AlbumName;
        var threshold = cfg.EffectiveMinTitleSimilarity;
        var providers = cfg.EffectiveMetadataProviders;
        if (providers.Count == 0)
        {
            providers = PluginConfiguration.AllProvidersInOrder;
        }

        IReadOnlyList<DeezerAlbumTrack>? deezerAlbumTracks = null;
        var deezerEnabled = providers.Contains(MetadataProvider.Deezer);
        if (deezerEnabled && FuzzyMatch.Normalize(albumName).Length > 0)
        {
            var albumArtist = PrimaryAlbumArtist(albumGroup.Tracks);
            deezerAlbumTracks = await _deezer.LoadAlbumTracksAsync(
                albumName,
                albumArtist,
                threshold,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var track in albumGroup.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var changed = await ProcessTrackAsync(
                    track,
                    albumName,
                    cfg,
                    changeLogPath,
                    deezerAlbumTracks,
                    searchMemo,
                    cancellationToken).ConfigureAwait(false);
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

        return writes;
    }

    private async Task<bool> ProcessTrackAsync(
        Audio track,
        string albumName,
        PluginConfiguration cfg,
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
            return false;
        }

        var album = Titles.StripMark(
            string.IsNullOrWhiteSpace(albumName) ? (track.Album ?? string.Empty) : albumName);

        ExplicitSearchResult result;
        string source;

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

        var decision = Decide(result, cfg.EffectiveDualVersionPreference);
        if (decision is null)
        {
            return false;
        }

        var desired = Titles.DesiredTitle(track.Name ?? string.Empty, decision.Value, artist);
        var nameChanged = desired.Length > 0 && !string.Equals(track.Name, desired, StringComparison.Ordinal);
        var tagsChanged = ApplyExplicitTags(track, decision.Value, cfg);

        if (!nameChanged && !tagsChanged)
        {
            return false;
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

        return true;
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
                var current = track.Name ?? string.Empty;
                var cleaned = Titles.StripMark(current, artist);
                if (cleaned.Length == 0 || string.Equals(current, cleaned, StringComparison.Ordinal))
                {
                    continue;
                }

                track.Name = cleaned;
                await _library.UpdateItemAsync(
                    track,
                    track.GetParent() ?? track,
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
                updated++;
                _logger.LogInformation(
                    "ExplicitFin removed symbol from {Id}: {Old} --> {New}",
                    track.Id,
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

    private static bool ApplyExplicitTags(Audio track, bool isExplicit, PluginConfiguration cfg)
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

        var tags = (track.Tags ?? []).ToList();
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
            track.Tags = tags.ToArray();
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

    private static List<AlbumGroup> GroupByAlbum(IReadOnlyList<Audio> tracks)
    {
        var map = new Dictionary<string, AlbumGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            string key;
            string albumName;
            if (track.GetParent() is MusicAlbum parent && parent.Id != Guid.Empty)
            {
                key = "id:" + parent.Id.ToString("N");
                albumName = Titles.StripMark(parent.Name ?? string.Empty);
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
                group = new AlbumGroup(albumName);
                map[key] = group;
            }

            group.Tracks.Add(track);
        }

        return map.Values.ToList();
    }

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
        public AlbumGroup(string albumName)
        {
            AlbumName = albumName;
        }

        public string AlbumName { get; }

        public List<Audio> Tracks { get; } = [];
    }
}
