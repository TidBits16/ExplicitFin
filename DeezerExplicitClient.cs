using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagger;

public sealed class DeezerExplicitClient
{
    private const string Base = "https://api.deezer.com";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    private readonly PacedHttp _http;
    private readonly ILogger<DeezerExplicitClient> _logger;

    public DeezerExplicitClient(IHttpClientFactory factory, HttpCache cache, ILogger<DeezerExplicitClient> logger)
    {
        _http = new PacedHttp(factory, cache, TimeSpan.FromMilliseconds(120));
        _logger = logger;
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    public async Task<bool?> GetTrackExplicitAsync(int trackId, CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            return null;
        }

        var payload = await _http.GetJsonAsync(
            "deezer/track/" + trackId,
            Base + "/track/" + trackId.ToString(CultureInfo.InvariantCulture),
            null,
            Ttl,
            cancellationToken).ConfigureAwait(false);

        if (payload is null || payload.Value.TryGetProperty("error", out _))
        {
            return null;
        }

        return ExplicitFrom(payload.Value);
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
