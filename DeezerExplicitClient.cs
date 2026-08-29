using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagger;

public sealed class DeezerAlbumTrack
{
    public string Title { get; init; } = string.Empty;

    public bool? Explicit { get; init; }
}

public sealed class DeezerExplicitClient
{
    private const string Base = "https://api.deezer.com";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private readonly PacedHttp _http;
    private readonly ILogger<DeezerExplicitClient> _logger;

    public DeezerExplicitClient(IHttpClientFactory factory, HttpCache cache, ILogger<DeezerExplicitClient> logger)
    {
        _http = new PacedHttp(factory, cache, TimeSpan.FromMilliseconds(120), maxInFlight: 4);
        _logger = logger;
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    /// <summary>
    /// Loads Deezer tracks for the best-matching album (one search + tracklist pages).
    /// Empty when the album cannot be resolved.
    /// </summary>
    public async Task<IReadOnlyList<DeezerAlbumTrack>> LoadAlbumTracksAsync(
        string album,
        string artist,
        double threshold,
        CancellationToken cancellationToken)
    {
        var albumNorm = FuzzyMatch.Normalize(album);
        if (albumNorm.Length == 0)
        {
            return [];
        }

        var albumId = await FindAlbumIdAsync(album, artist, threshold, cancellationToken).ConfigureAwait(false);
        if (albumId.Length == 0)
        {
            return [];
        }

        return await FetchAlbumTracksAsync(albumId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches a local title against a preloaded Deezer album tracklist.
    /// </summary>
    public static ExplicitSearchResult? MatchOnAlbum(
        string title,
        IReadOnlyList<DeezerAlbumTrack> albumTracks,
        double threshold)
    {
        if (FuzzyMatch.Normalize(title).Length == 0 || albumTracks.Count == 0)
        {
            return null;
        }

        var hasExplicit = false;
        var hasClean = false;
        var any = false;
        foreach (var track in albumTracks)
        {
            if (FuzzyMatch.Similarity(title, track.Title) < threshold)
            {
                continue;
            }

            any = true;
            if (track.Explicit == true)
            {
                hasExplicit = true;
            }
            else if (track.Explicit == false)
            {
                hasClean = true;
            }

            if (hasExplicit && hasClean)
            {
                break;
            }
        }

        if (!any)
        {
            return null;
        }

        // Matched title but no usable explicit flag → treat as unknown (don't mark).
        if (!hasExplicit && !hasClean)
        {
            return null;
        }

        return new ExplicitSearchResult(hasExplicit, hasClean);
    }

    public async Task<ExplicitSearchResult> SearchAsync(
        string title,
        string artist,
        string album,
        double threshold,
        CancellationToken cancellationToken)
    {
        var qTitle = FuzzyMatch.Normalize(title);
        if (qTitle.Length == 0)
        {
            return ExplicitSearchResult.Empty;
        }

        var parts = new List<string> { "track:" + FuzzyMatch.QuoteQuery(title) };
        if (FuzzyMatch.Normalize(artist).Length > 0)
        {
            parts.Add("artist:" + FuzzyMatch.QuoteQuery(artist));
        }

        if (FuzzyMatch.Normalize(album).Length > 0)
        {
            parts.Add("album:" + FuzzyMatch.QuoteQuery(album));
        }

        var query = string.Join(' ', parts);
        JsonElement? payload;
        try
        {
            payload = await _http.GetJsonAsync(
                "deezer/search",
                Base + "/search",
                new Dictionary<string, string> { ["q"] = query },
                Ttl,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer search failed for {Title}", title);
            return ExplicitSearchResult.Empty;
        }

        if (payload is null || payload.Value.TryGetProperty("error", out _))
        {
            return ExplicitSearchResult.Empty;
        }

        if (!payload.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return ExplicitSearchResult.Empty;
        }

        var hasExplicit = false;
        var hasClean = false;
        foreach (var hit in data.EnumerateArray())
        {
            var hitTitle = JsonUtil.Str(hit, "title");
            var hitArtist = string.Empty;
            if (hit.TryGetProperty("artist", out var artistObj) && artistObj.ValueKind == JsonValueKind.Object)
            {
                hitArtist = JsonUtil.Str(artistObj, "name");
            }

            var hitAlbum = string.Empty;
            if (hit.TryGetProperty("album", out var albumObj) && albumObj.ValueKind == JsonValueKind.Object)
            {
                hitAlbum = JsonUtil.Str(albumObj, "title");
            }

            if (!FuzzyMatch.MeetsThreshold(title, artist, album, hitTitle, hitArtist, hitAlbum, threshold))
            {
                continue;
            }

            var flag = ExplicitFrom(hit);
            if (flag == true)
            {
                hasExplicit = true;
            }
            else if (flag == false)
            {
                hasClean = true;
            }

            if (hasExplicit && hasClean)
            {
                break;
            }
        }

        return new ExplicitSearchResult(hasExplicit, hasClean);
    }

    private async Task<string> FindAlbumIdAsync(
        string album,
        string artist,
        double threshold,
        CancellationToken cancellationToken)
    {
        var parts = new List<string> { "album:" + FuzzyMatch.QuoteQuery(album) };
        if (FuzzyMatch.Normalize(artist).Length > 0)
        {
            parts.Add("artist:" + FuzzyMatch.QuoteQuery(artist));
        }

        JsonElement? payload;
        try
        {
            payload = await _http.GetJsonAsync(
                "deezer/search/album",
                Base + "/search/album",
                new Dictionary<string, string>
                {
                    ["q"] = string.Join(' ', parts),
                    ["limit"] = "25"
                },
                Ttl,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer album search failed for {Album}", album);
            return string.Empty;
        }

        if (payload is null || !payload.Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string bestId = string.Empty;
        var bestScore = -1.0;
        var bestFans = -1;
        foreach (var hit in data.EnumerateArray())
        {
            var hitTitle = JsonUtil.Str(hit, "title");
            var hitArtist = string.Empty;
            if (hit.TryGetProperty("artist", out var artistObj) && artistObj.ValueKind == JsonValueKind.Object)
            {
                hitArtist = JsonUtil.Str(artistObj, "name");
            }

            var titleScore = FuzzyMatch.Similarity(album, hitTitle);
            if (titleScore < threshold)
            {
                continue;
            }

            if (FuzzyMatch.Normalize(artist).Length > 0
                && FuzzyMatch.Similarity(artist, hitArtist) < Math.Min(threshold, 0.82))
            {
                continue;
            }

            var fans = (int)JsonUtil.Num(hit, "nb_fan");
            var id = JsonUtil.IdStr(hit, "id");
            if (id.Length == 0 || id == "0")
            {
                continue;
            }

            if (titleScore > bestScore + 0.0001
                || (Math.Abs(titleScore - bestScore) < 0.0001 && fans > bestFans))
            {
                bestScore = titleScore;
                bestFans = fans;
                bestId = id;
            }
        }

        return bestId;
    }

    private async Task<IReadOnlyList<DeezerAlbumTrack>> FetchAlbumTracksAsync(
        string albumId,
        CancellationToken cancellationToken)
    {
        var items = new List<DeezerAlbumTrack>();
        var path = "album/" + albumId + "/tracks";
        var query = new Dictionary<string, string> { ["limit"] = "100" };

        while (path.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement? page;
            try
            {
                page = await _http.GetJsonAsync(
                    "deezer/" + path,
                    Base + "/" + path,
                    query,
                    Ttl,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deezer album tracks failed for {AlbumId}", albumId);
                break;
            }

            if (page is null)
            {
                break;
            }

            foreach (var raw in JsonUtil.Arr(page.Value, "data"))
            {
                var title = JsonUtil.Str(raw, "title").Trim();
                if (title.Length == 0)
                {
                    continue;
                }

                items.Add(new DeezerAlbumTrack
                {
                    Title = title,
                    Explicit = ExplicitFrom(raw)
                });
            }

            var next = JsonUtil.Str(page.Value, "next").Trim();
            if (next.Length == 0 || !Uri.TryCreate(next, UriKind.Absolute, out var u))
            {
                break;
            }

            path = u.AbsolutePath.TrimStart('/');
            if (path.StartsWith("2.0/", StringComparison.Ordinal))
            {
                path = path[4..];
            }

            query = [];
            foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                query[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return items;
    }

    private static bool? ExplicitFrom(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.TryGetProperty("explicit_content_lyrics", out var code) && code.ValueKind != JsonValueKind.Null)
        {
            var n = code.ValueKind == JsonValueKind.Number ? (int)code.GetDouble() : 0;
            if (n == 1)
            {
                return true;
            }

            if (n is 0 or 3)
            {
                return false;
            }

            if (n == 2)
            {
                return null;
            }
        }

        return JsonUtil.Bool(payload, "explicit_lyrics");
    }
}
