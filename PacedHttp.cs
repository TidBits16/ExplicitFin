using System.Net.Http;
using System.Text.Json;

namespace Jellyfin.Plugin.ExplicitTagger;

public class PacedHttp
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _next = DateTime.MinValue;
    private readonly TimeSpan _minDelay;
    private int _httpN;

    public PacedHttp(IHttpClientFactory factory, TimeSpan minDelay, string? userAgent = null)
    {
        _http = factory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                userAgent ?? "explicitfin/2.0 (jellyfin-plugin)");
        }

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _minDelay = minDelay;
    }

    public int HttpCount => _httpN;

    public async Task<JsonElement?> GetJsonAsync(
        string url,
        IDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        if (query is { Count: > 0 })
        {
            var qs = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + qs;
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
        return doc.RootElement.Clone();
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
