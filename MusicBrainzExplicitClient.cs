using System.Text.Json;

namespace Jellyfin.Plugin.ExplicitTagger;

public sealed class MusicBrainzExplicitClient
{
    private const string Base = "https://musicbrainz.org/ws/2";
    private const int MaxDetailLookups = 5;
    private static readonly string[] ExplicitTags = ["explicit", "[explicit]", "nsfw", "not safe for work"];

    private readonly PacedHttp _http;

    public MusicBrainzExplicitClient(IHttpClientFactory factory)
    {
        _http = new PacedHttp(
            factory,
            TimeSpan.FromMilliseconds(1100),
            "ExplicitFin/2.0 ( https://github.com/TidBits16/ExplicitFin )");
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

        var parts = new List<string> { "recording:" + FuzzyMatch.QuoteQuery(title) };
        if (FuzzyMatch.Normalize(artist).Length > 0)
        {
            parts.Add("artist:" + FuzzyMatch.QuoteQuery(artist));
        }

        if (FuzzyMatch.Normalize(album).Length > 0)
        {
            parts.Add("release:" + FuzzyMatch.QuoteQuery(album));
        }

        var query = string.Join(" AND ", parts);
        var payload = await _http.GetJsonAsync(
            Base + "/recording",
            new Dictionary<string, string>
            {
                ["query"] = query,
                ["fmt"] = "json",
                ["limit"] = "25"
            },
            cancellationToken).ConfigureAwait(false);

        if (payload is null || payload.Value.TryGetProperty("error", out _))
        {
            return ExplicitSearchResult.Empty;
        }

        if (!payload.Value.TryGetProperty("recordings", out var recordings)
            || recordings.ValueKind != JsonValueKind.Array)
        {
            return ExplicitSearchResult.Empty;
        }

        var hasExplicit = false;
        var hasClean = false;
        var details = 0;
        foreach (var recording in recordings.EnumerateArray())
        {
            var hitTitle = JsonUtil.Str(recording, "title");
            var hitArtist = FirstArtistName(recording);
            if (FuzzyMatch.Similarity(title, hitTitle) < FuzzyMatch.Threshold)
            {
                continue;
            }

            if (FuzzyMatch.Normalize(artist).Length > 0
                && FuzzyMatch.Similarity(artist, hitArtist) < FuzzyMatch.Threshold)
            {
                continue;
            }

            var detail = recording;
            var needDetail = FuzzyMatch.Normalize(album).Length > 0
                || !recording.TryGetProperty("tags", out _);
            if (needDetail && details < MaxDetailLookups)
            {
                var id = JsonUtil.Str(recording, "id");
                if (id.Length > 0)
                {
                    var fetched = await GetRecordingAsync(id, cancellationToken).ConfigureAwait(false);
                    details++;
                    if (fetched is not null)
                    {
                        detail = fetched.Value;
                    }
                }
            }

            var hitAlbum = FirstReleaseTitle(detail);
            if (!FuzzyMatch.MeetsThreshold(title, artist, album, hitTitle, hitArtist, hitAlbum))
            {
                continue;
            }

            if (IsExplicitRecording(detail))
            {
                hasExplicit = true;
            }
            else
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

    private async Task<JsonElement?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken)
    {
        var payload = await _http.GetJsonAsync(
            Base + "/recording/" + Uri.EscapeDataString(recordingId.Trim()),
            new Dictionary<string, string>
            {
                ["inc"] = "artists+releases+tags",
                ["fmt"] = "json"
            },
            cancellationToken).ConfigureAwait(false);

        if (payload is null || payload.Value.TryGetProperty("error", out _))
        {
            return null;
        }

        return payload;
    }

    private static string FirstArtistName(JsonElement recording)
    {
        if (!recording.TryGetProperty("artist-credit", out var credits)
            || credits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var text = name.GetString() ?? string.Empty;
                if (text.Length > 0)
                {
                    return text;
                }
            }

            if (credit.TryGetProperty("artist", out var artist)
                && artist.ValueKind == JsonValueKind.Object)
            {
                var text = JsonUtil.Str(artist, "name");
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static string FirstReleaseTitle(JsonElement recording)
    {
        if (!recording.TryGetProperty("releases", out var releases)
            || releases.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var release in releases.EnumerateArray())
        {
            var title = JsonUtil.Str(release, "title");
            if (title.Length > 0)
            {
                return title;
            }
        }

        return string.Empty;
    }

    private static bool IsExplicitRecording(JsonElement payload)
    {
        var disambiguation = JsonUtil.Str(payload, "disambiguation");
        if (disambiguation.Contains("explicit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!payload.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            var name = JsonUtil.Str(tag, "name");
            if (ExplicitTags.Any(t => name.Equals(t, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
