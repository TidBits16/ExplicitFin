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
            var totalWrites = 0;
            var albumIndex = 0;

            foreach (var albumGroup in albums)
            {
                cancellationToken.ThrowIfCancellationRequested();
                albumIndex++;
                using var gate = new SemaphoreSlim(workers, workers);
                var albumWrites = 0;

                await Task.WhenAll(albumGroup.Tracks.Select(async track =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var changed = await ProcessTrackAsync(
                            track,
                            albumGroup.AlbumName,
                            cfg,
                            changeLogPath,
                            cancellationToken).ConfigureAwait(false);
                        if (changed)
                        {
                            Interlocked.Increment(ref albumWrites);
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
                        gate.Release();
                    }
                })).ConfigureAwait(false);

                totalWrites += albumWrites;
                progress.Report(100.0 * albumIndex / Math.Max(1, albums.Count));
            }

            progress.Report(100);
            _logger.LogInformation(
                "ExplicitFin finished: {Writes} title updates, Deezer http {Dz}, MusicBrainz http {Mb}",
                totalWrites,
                _deezer.HttpCount,
                _musicBrainz.HttpCount);
        }
        finally
        {
            Titles.ResetStyle();
        }
    }

    private async Task<bool> ProcessTrackAsync(
        Audio track,
        string albumName,
        PluginConfiguration cfg,
        string changeLogPath,
        CancellationToken cancellationToken)
    {
        var title = Titles.StripMark(track.Name ?? string.Empty);
        if (title.Length == 0)
        {
            return false;
        }

        var artist = PrimaryArtist(track);
        var album = Titles.StripMark(
            string.IsNullOrWhiteSpace(albumName) ? (track.Album ?? string.Empty) : albumName);

        var (result, source) = await ResolveAsync(title, artist, album, cfg, cancellationToken).ConfigureAwait(false);
        var decision = Decide(result, cfg.EffectiveDualVersionPreference);
        if (decision is null)
        {
            return false;
        }

        var desired = Titles.DesiredTitle(track.Name ?? string.Empty, decision.Value);
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
                "ExplicitFin renamed {Id}: {Old} → {New} ({Source}, {Decision})",
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
        CancellationToken cancellationToken)
    {
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
                return (result, source);
            }
        }

        return (ExplicitSearchResult.Empty, "none");
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
