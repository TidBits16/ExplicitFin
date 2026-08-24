using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExplicitTagger.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool WriteExplicitTags { get; set; } = true;

    public string ExplicitTags { get; set; } = "Explicit";

    public bool RenameExplicitTitles { get; set; } = true;

    /// <summary>Add the title mark to album names when any track on the album is explicit.</summary>
    public bool MarkExplicitAlbums { get; set; } = true;

    public string ExplicitMark { get; set; } = "🅴";

    /// <summary>append or prepend.</summary>
    public string ExplicitMarkPlacement { get; set; } = "append";

    /// <summary>Look up explicit flag from Deezer using the stored provider ID.</summary>
    public bool UseDeezer { get; set; } = true;

    /// <summary>Look up explicit tags on the MusicBrainz recording.</summary>
    public bool UseMusicBrainz { get; set; }

    /// <summary>Look up trackExplicitness from Apple Music / iTunes using the stored provider ID.</summary>
    public bool UseAppleMusic { get; set; }

    /// <summary>Gets or sets worker count. 0 means use CPU count.</summary>
    public int Workers { get; set; }

    public IReadOnlyList<string> EffectiveExplicitTags
        => (ExplicitTags ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool PrependExplicitMark
        => string.Equals(ExplicitMarkPlacement, "prepend", StringComparison.OrdinalIgnoreCase);
}
