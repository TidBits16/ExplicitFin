using System.Text;

namespace Jellyfin.Plugin.ExplicitTagger;

public static class Titles
{
    public const string ExplicitMark = "🅴";

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
    {
        var s = name.Trim();
        s = StripToken(s, Affix);
        s = StripToken(s, ExplicitMark);
        s = StripToken(s, "🅴");
        return s.Trim();
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

    public static string DesiredTitle(string name, bool explicitFlag)
    {
        var bas = StripMark(name);
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

        return PrependMark ? mark + " " + bas : bas + " " + mark;
    }
}
