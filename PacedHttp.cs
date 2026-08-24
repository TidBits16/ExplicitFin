using System.Net.Http;
using System.Text.Json;

namespace Jellyfin.Plugin.ExplicitTagger;

public class PacedHttp
{
    private readonly HttpClient _http;
    private readonly HttpCache _cache;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _next = DateTime.MinValue;
    private readonly TimeSpan _minDelay;
    private int _httpN;
    private int _hits;

    public PacedHttp(IHttpClientFactory factory, HttpCache cache, TimeSpan minDelay)
    {
        _http = factory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "explicit-tagger/1.0 (jellyfin-plugin)");
        }

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _cache = cache;
        _minDelay = minDelay;
    }

    public int HttpCount => _httpN;

    public int CacheHits => _hits;

    public async Task<JsonElement?> GetJsonAsync(
        string cacheKey,
        string url,
        IDictionary<string, string>? query,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (query is { Count: > 0 })
        {
            var qs = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + qs;
        }

        var key = cacheKey + " " + url;
        if (_cache.TryGet(key, ttl, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        await PaceAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _httpN);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        var clone = doc.RootElement.Clone();
        _cache.Set(key, clone);
        return clone;
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        await _pace.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _next = DateTime.UtcNow + _minDelay;
        }
        finally
        {
            _pace.Release();
        }
    }
}

public static class JsonUtil
{
    public static bool IsObject(JsonElement el)
        => el.ValueKind == JsonValueKind.Object;

    public static string Str(JsonElement el, string name)
    {
        if (!IsObject(el) || !el.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : p.ToString();
    }

    public static bool? Bool(JsonElement el, string name)
    {
        if (!IsObject(el) || !el.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
