using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExplicitTagger.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public static readonly MetadataProvider[] AllProvidersInOrder =
    [
        MetadataProvider.Deezer,
        MetadataProvider.MusicBrainz
    ];

    /// <summary>All providers in UI order (checked and unchecked).</summary>
    public MetadataProvider[] MetadataProviderOrder { get; set; } = [];

    /// <summary>Checked providers to try, in order.</summary>
    public MetadataProvider[] MetadataProviders { get; set; } = [];

    public string ExplicitMark { get; set; } = "🅴";

    /// <summary>append or prepend.</summary>
    public string ExplicitMarkPlacement { get; set; } = "append";

    /// <summary>Add/remove Jellyfin tags on tracks when explicit status is known.</summary>
    public bool WriteExplicitTags { get; set; } = true;

    /// <summary>Comma-separated Jellyfin tag names (default Explicit).</summary>
    public string ExplicitTags { get; set; } = "Explicit";

    /// <summary>
    /// When both explicit and clean versions match:
    /// prefer_explicit | prefer_clean | dont_touch.
    /// </summary>
    public string DualVersionPreference { get; set; } = "prefer_explicit";

    public double MinTitleSimilarity { get; set; } = 0.90;

    /// <summary>Gets or sets worker count. 0 means use CPU count.</summary>
    public int Workers { get; set; }

    public bool PrependExplicitMark
        => string.Equals(ExplicitMarkPlacement, "prepend", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> EffectiveExplicitTags
        => (ExplicitTags ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public double EffectiveMinTitleSimilarity
        => MinTitleSimilarity <= 0 ? 0.90 : Math.Clamp(MinTitleSimilarity, 0.5, 1.0);

    public DualVersionMode EffectiveDualVersionPreference
        => (DualVersionPreference ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "prefer_clean" => DualVersionMode.PreferClean,
            "dont_touch" or "don't_touch" or "donottouch" => DualVersionMode.DontTouch,
            _ => DualVersionMode.PreferExplicit
        };

    public IReadOnlyList<MetadataProvider> EffectiveMetadataProviderOrder
    {
        get
        {
            if (MetadataProviderOrder is { Length: > 0 })
            {
                return NormalizeProviderOrder(MetadataProviderOrder);
            }

            if (MetadataProviders is { Length: > 0 })
            {
                return NormalizeProviderOrder(MetadataProviders);
            }

            return AllProvidersInOrder;
        }
    }

    public IReadOnlyList<MetadataProvider> EffectiveMetadataProviders
    {
        get
        {
            if (MetadataProviders is { Length: > 0 })
            {
                var enabled = new HashSet<MetadataProvider>(MetadataProviders);
                return EffectiveMetadataProviderOrder.Where(enabled.Contains).ToList();
            }

            return EffectiveMetadataProviderOrder.ToList();
        }
    }

    private static IReadOnlyList<MetadataProvider> NormalizeProviderOrder(IEnumerable<MetadataProvider> order)
    {
        var list = new List<MetadataProvider>();
        var seen = new HashSet<MetadataProvider>();
        foreach (var provider in order)
        {
            if (seen.Add(provider))
            {
                list.Add(provider);
            }
        }

        foreach (var provider in AllProvidersInOrder)
        {
            if (seen.Add(provider))
            {
                list.Add(provider);
            }
        }

        return list;
    }
}

public enum DualVersionMode
{
    PreferExplicit,
    PreferClean,
    DontTouch
}
