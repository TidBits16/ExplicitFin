using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Jellyfin.Plugin.FinCommon;

/// <summary>Paced HTTP GET with disk cache. Options cover MusicFin/ExplicitFin and LyricFin/ArtistFin.</summary>
public sealed class PacedHttp
{
    private readonly HttpClient _http;
    private readonly HttpCache _cache;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private readonly SemaphoreSlim _inFlight;
    private readonly TimeSpan _minDelay;
    private readonly bool _retryOn429;
    private readonly bool _retryOn503;
    private readonly bool _skipCacheOnErrorProperty;
    private DateTime _next = DateTime.MinValue;
    private int _httpN;
    private int _hits;

    public PacedHttp(
        IHttpClientFactory factory,
        HttpCache cache,
        TimeSpan minDelay,
        int maxInFlight = 2,
        string? userAgent = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        bool retryOn429 = false,
        bool retryOn503 = false,
        bool skipCacheOnErrorProperty = false)
    {
        _http = factory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
        var agent = string.IsNullOrWhiteSpace(userAgent) ? "fincommon/1.0 (jellyfin-plugin)" : userAgent.Trim();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
        }

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
                }
            }
        }

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _cache = cache;
        _minDelay = minDelay;
        _retryOn429 = retryOn429;
        _retryOn503 = retryOn503;
        _skipCacheOnErrorProperty = skipCacheOnErrorProperty;
        var slots = Math.Clamp(maxInFlight, 1, 6);
        _inFlight = new SemaphoreSlim(slots, slots);
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
            var qs = string.Join('&', query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + qs;
        }

        var key = cacheKey + " " + url;
        if (_cache.TryGet(key, ttl, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        var attempts = _retryOn429 || _retryOn503 ? 4 : 1;
        await _inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                await PaceAsync(cancellationToken).ConfigureAwait(false);
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _httpN);

                if ((_retryOn429 && response.StatusCode == HttpStatusCode.TooManyRequests)
                    || (_retryOn503 && response.StatusCode == HttpStatusCode.ServiceUnavailable))
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                    await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Remember empty so the same URL is not re-hit until TTL expires.
                    using var missDoc = JsonDocument.Parse("{}");
                    _cache.Set(key, missDoc.RootElement.Clone());
                    return null;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                var clone = doc.RootElement.Clone();
                if (!_skipCacheOnErrorProperty || !clone.TryGetProperty("error", out _))
                {
                    _cache.Set(key, clone);
                }

                return clone;
            }

            return null;
        }
        finally
        {
            _inFlight.Release();
        }
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
