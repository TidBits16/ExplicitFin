using System.Text.Json;

namespace Jellyfin.Plugin.ExplicitTagger;

public sealed class MusicBrainzExplicitClient
{
    private const string Base = "https://musicbrainz.org/ws/2";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(14);
    private static readonly string[] ExplicitTags = ["explicit", "[explicit]", "nsfw", "not safe for work"];

    private readonly PacedHttp _http;

    public MusicBrainzExplicitClient(IHttpClientFactory factory, HttpCache cache)
    {
        _http = new PacedHttp(
            factory,
            cache,
            TimeSpan.FromMilliseconds(1100),
            "ExplicitFin/1.2 ( https://github.com/TidBits16/ExplicitFin )");
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    public async Task<bool?> GetRecordingExplicitAsync(string recordingId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
        {
            return null;
        }

        var payload = await _http.GetJsonAsync(
            "musicbrainz/recording/" + recordingId,
            Base + "/recording/" + Uri.EscapeDataString(recordingId.Trim()) + "?inc=tags&fmt=json",
            null,
            Ttl,
            cancellationToken).ConfigureAwait(false);

        if (payload is null || payload.Value.TryGetProperty("error", out _))
        {
            return null;
        }

        return ExplicitFrom(payload.Value);
    }

    public async Task<string?> ResolveRecordingIdFromTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var payload = await _http.GetJsonAsync(
            "musicbrainz/track/" + trackId,
            Base + "/track/" + Uri.EscapeDataString(trackId.Trim()) + "?inc=recordings&fmt=json",
            null,
            Ttl,
            cancellationToken).ConfigureAwait(false);

        if (payload is null || payload.Value.TryGetProperty("error", out _))
        {
            return null;
        }

        if (!payload.Value.TryGetProperty("recordings", out var recordings)
            || recordings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var recording in recordings.EnumerateArray())
        {
            var id = JsonUtil.Str(recording, "id");
            if (id.Length > 0)
            {
                return id;
            }
        }

        return null;
    }

    private static bool? ExplicitFrom(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var disambiguation = JsonUtil.Str(payload, "disambiguation");
        if (disambiguation.Contains("explicit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!payload.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            var name = JsonUtil.Str(tag, "name");
            if (ExplicitTags.Any(t => name.Equals(t, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return null;
    }
}
