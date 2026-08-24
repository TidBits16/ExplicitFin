using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.ExplicitTagger;

public sealed class AppleMusicExplicitClient
{
    private const string Base = "https://itunes.apple.com/lookup";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    private readonly PacedHttp _http;

    public AppleMusicExplicitClient(IHttpClientFactory factory, HttpCache cache)
    {
        _http = new PacedHttp(factory, cache, TimeSpan.FromMilliseconds(120));
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    public async Task<bool?> GetTrackExplicitAsync(long trackId, CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            return null;
        }

        var payload = await _http.GetJsonAsync(
            "applemusic/track/" + trackId.ToString(CultureInfo.InvariantCulture),
            Base,
            new Dictionary<string, string> { ["id"] = trackId.ToString(CultureInfo.InvariantCulture) },
            Ttl,
            cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            return null;
        }

        return ExplicitFrom(payload.Value);
    }

    private static bool? ExplicitFrom(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            var rating = JsonUtil.Str(result, "trackExplicitness");
            if (rating.Length == 0)
            {
                continue;
            }

            return rating.ToLowerInvariant() switch
            {
                "explicit" => true,
                "notexplicit" => false,
                "cleaned" => false,
                _ => null
            };
        }

        return null;
    }
}
