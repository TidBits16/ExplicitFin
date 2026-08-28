using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExplicitTagger.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ExplicitMark { get; set; } = "🅴";

    /// <summary>append or prepend.</summary>
    public string ExplicitMarkPlacement { get; set; } = "append";

    /// <summary>
    /// When both explicit and clean versions match:
    /// prefer_explicit | prefer_clean | dont_touch.
    /// </summary>
    public string DualVersionPreference { get; set; } = "prefer_explicit";

    /// <summary>Gets or sets worker count. 0 means use CPU count.</summary>
    public int Workers { get; set; }

    public bool PrependExplicitMark
        => string.Equals(ExplicitMarkPlacement, "prepend", StringComparison.OrdinalIgnoreCase);

    public DualVersionMode EffectiveDualVersionPreference
        => (DualVersionPreference ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "prefer_clean" => DualVersionMode.PreferClean,
            "dont_touch" or "don't_touch" or "donottouch" => DualVersionMode.DontTouch,
            _ => DualVersionMode.PreferExplicit
        };
}

public enum DualVersionMode
{
    PreferExplicit,
    PreferClean,
    DontTouch
}
