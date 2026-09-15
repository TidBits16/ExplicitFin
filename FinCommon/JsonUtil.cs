using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.FinCommon;

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

    public static double Num(JsonElement el, string name)
    {
        if (!IsObject(el) || !el.TryGetProperty(name, out var p))
        {
            return 0;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String => double.TryParse(p.GetString(), out var n) ? n : 0,
            _ => 0
        };
    }

    public static string IdStr(JsonElement el, string name)
    {
        if (!IsObject(el) || !el.TryGetProperty(name, out var p))
        {
            return string.Empty;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt64(out var n)
                ? n.ToString(CultureInfo.InvariantCulture)
                : p.GetRawText(),
            JsonValueKind.String => p.GetString()?.Trim() ?? string.Empty,
            _ => p.ToString()
        };
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

    public static IEnumerable<JsonElement> Arr(JsonElement el, string name)
    {
        if (!IsObject(el) || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var x in p.EnumerateArray())
        {
            yield return x;
        }
    }

    public static JsonElement? Obj(JsonElement el, string name)
        => IsObject(el) && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
}

public static class Similarity
{
    public static double Ratio(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var matches = 0;
        var used = new bool[b.Length];
        var bi = 0;
        foreach (var ch in a)
        {
            for (var j = bi; j < b.Length; j++)
            {
                if (!used[j] && b[j] == ch)
                {
                    used[j] = true;
                    matches++;
                    bi = j + 1;
                    break;
                }
            }
        }

        return 2.0 * matches / (a.Length + b.Length);
    }
}
