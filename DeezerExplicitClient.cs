using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagger;

public sealed class DeezerExplicitClient
{
    private const string Base = "https://api.deezer.com";

    private readonly PacedHttp _http;
    private readonly ILogger<DeezerExplicitClient> _logger;

    public DeezerExplicitClient(IHttpClientFactory factory, ILogger<DeezerExplicitClient> logger)
    {
        _http = new PacedHttp(factory, TimeSpan.FromMilliseconds(120));
        _logger = logger;
    }

    public int HttpCount => _http.HttpCount;

    public async Task<ExplicitSearchResult> SearchAsync(
        string title,
        string artist,
        string album,
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
                Base + "/search",
                new Dictionary<string, string> { ["q"] = query },
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

            if (!FuzzyMatch.MeetsThreshold(title, artist, album, hitTitle, hitArtist, hitAlbum))
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

    private static bool? ExplicitFrom(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.TryGetProperty("explicit_content_lyrics", out var code) && code.ValueKind != JsonValueKind.Null)
        {
            var n = code.ValueKind == JsonValueKind.Number ? (int)code.GetDouble() : 0;
            return n switch
            {
                1 => true,
                0 or 3 => false,
                _ => null
            };
        }

        return JsonUtil.Bool(payload, "explicit_lyrics");
    }
}
