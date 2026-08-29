using System.Text;

namespace Jellyfin.Plugin.ExplicitTagger;

public static class Titles
{
    public const string ExplicitMark = "🅴";

    private static readonly string[] ArtistSeparators = [" - ", " – ", " - ", " -- "];

    public static string Affix { get; set; } = ExplicitMark;

    public static bool PrependMark { get; set; }

    public static void UseStyle(string? affix, bool prepend)
    {
        var text = (affix ?? string.Empty).Trim();
        Affix = text.Length > 0 ? text : ExplicitMark;
        PrependMark = prepend;
    }

    public static void ResetStyle()
    {
        Affix = ExplicitMark;
        PrependMark = false;
    }

    public static string StripMark(string name)
        => StripMark(name, artist: null);

    /// <summary>
    /// Removes the configured mark from the edges, and from just before a trailing
    /// " - Artist" suffix when <paramref name="artist"/> is known (or any dash suffix
    /// when it isn't).
    /// </summary>
    public static string StripMark(string name, string? artist)
    {
        var s = name.Trim();
        if (s.Length == 0)
        {
            return s;
        }

        if (TrySplitTrailingArtist(s, artist, out var core, out var sep, out var suffix))
        {
            core = StripEdgeMarks(core);
            return core + sep + suffix;
        }

        s = StripMarkBeforeDashSuffix(s);
        return StripEdgeMarks(s);
    }

    private static string StripEdgeMarks(string name)
    {
        var s = name.Trim();
        s = StripToken(s, Affix);
        s = StripToken(s, ExplicitMark);
        s = StripToken(s, "🅴");
        return s.Trim();
    }

    /// <summary>
    /// Removes " MARK - " (any dash sep) so marks left mid-title before an artist suffix
    /// are cleared even when the artist string is unknown.
    /// </summary>
    private static string StripMarkBeforeDashSuffix(string name)
    {
        var s = name;
                foreach (var mark in DistinctMarks())
        {
            foreach (var sep in ArtistSeparators)
            {
                foreach (var token in new[] { " " + mark + sep, mark + sep })
                {
                    var idx = s.LastIndexOf(token, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        s = s[..idx] + sep + s[(idx + token.Length)..];
                        return s.Trim();
                    }
                }
            }
        }

        return s;
    }

    private static IEnumerable<string> DistinctMarks()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in new[] { Affix, ExplicitMark, "🅴" })
        {
            var mark = candidate.Trim();
            if (mark.Length > 0 && seen.Add(mark))
            {
                yield return mark;
            }
        }
    }

    private static string StripToken(string name, string token)
    {
        var mark = token.Trim();
        if (mark.Length == 0)
        {
            return name;
        }

        var s = name;
        foreach (var edge in new[] { mark, mark + " ", " " + mark })
        {
            if (s.StartsWith(edge, StringComparison.Ordinal))
            {
                s = s[edge.Length..].TrimStart();
                break;
            }
        }

        foreach (var edge in new[] { mark, " " + mark, mark + " " })
        {
            if (s.EndsWith(edge, StringComparison.Ordinal))
            {
                s = s[..^edge.Length].TrimEnd();
                break;
            }
        }

        return s;
    }

    public static bool HasExplicitMark(string name)
        => !string.Equals(name.Trim(), StripMark(name), StringComparison.Ordinal);

    /// <summary>
    /// Strips a trailing " - Artist" (or similar dash) when the suffix matches
    /// <paramref name="artist"/>, including close typos.
    /// </summary>
    public static string StripTrailingArtist(string title, string? artist)
    {
        if (!TrySplitTrailingArtist(title, artist, out var core, out _, out _))
        {
            return title.Trim();
        }

        return core;
    }

    /// <summary>
    /// Builds the display title from the local name only - never substitutes a catalog title.
    /// When appending and a trailing " - Artist" is present, the mark goes before that suffix:
    /// <c>God is reawlly real [E] - AJR</c>.
    /// </summary>
    public static string DesiredTitle(string name, bool explicitFlag, string? artist = null)
    {
        var bas = StripMark(name, artist);
        if (bas.Length == 0)
        {
            return string.Empty;
        }

        if (!explicitFlag)
        {
            return bas;
        }

        var mark = Affix.Trim();
        if (mark.Length == 0)
        {
            mark = ExplicitMark;
        }

        if (TrySplitTrailingArtist(bas, artist, out var core, out var sep, out var suffix))
        {
            if (PrependMark)
            {
                return mark + " " + core + sep + suffix;
            }

            return core + " " + mark + sep + suffix;
        }

        return PrependMark ? mark + " " + bas : bas + " " + mark;
    }

    private static bool TrySplitTrailingArtist(
        string title,
        string? artist,
        out string core,
        out string separator,
        out string suffix)
    {
        core = title.Trim();
        separator = string.Empty;
        suffix = string.Empty;

        var a = (artist ?? string.Empty).Trim();
        if (core.Length == 0 || a.Length == 0)
        {
            return false;
        }

        foreach (var sep in ArtistSeparators)
        {
            var idx = core.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var tail = core[(idx + sep.Length)..].Trim();
            if (tail.Length == 0)
            {
                continue;
            }

            if (FuzzyMatch.Similarity(a, tail) < 0.82)
            {
                continue;
            }

            core = core[..idx].TrimEnd();
            separator = sep;
            suffix = tail;
            return core.Length > 0;
        }

        return false;
    }
}
