using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ExplicitTagger;

public static class FuzzyMatch
{
    public const double Threshold = 0.90;

    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = Titles.StripMark(value);
        s = s.Normalize(NormalizationForm.FormKC);
        s = s.ToLowerInvariant();
        s = MultiSpace.Replace(s.Trim(), " ");
        return s;
    }

    /// <summary>Normalized Levenshtein similarity in [0, 1].</summary>
    public static double Similarity(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 && right.Length == 0)
        {
            return 1.0;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        if (left == right)
        {
            return 1.0;
        }

        var distance = Levenshtein(left, right);
        var max = Math.Max(left.Length, right.Length);
        return 1.0 - ((double)distance / max);
    }

    /// <summary>
    /// True when every present expected field scores at or above the threshold.
    /// Title is required; artist/album are required only when the expected side has them.
    /// </summary>
    public static bool MeetsThreshold(
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum,
        string candidateTitle,
        string candidateArtist,
        string candidateAlbum,
        double threshold = Threshold)
    {
        var title = Normalize(expectedTitle);
        if (title.Length == 0)
        {
            return false;
        }

        if (Similarity(expectedTitle, candidateTitle) < threshold)
        {
            return false;
        }

        var artist = Normalize(expectedArtist);
        if (artist.Length > 0 && Similarity(expectedArtist, candidateArtist) < threshold)
        {
            return false;
        }

        var album = Normalize(expectedAlbum);
        if (album.Length > 0 && Similarity(expectedAlbum, candidateAlbum) < threshold)
        {
            return false;
        }

        return true;
    }

    private static int Levenshtein(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    public static string QuoteQuery(string value)
        => "\"" + Normalize(value).Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
}
