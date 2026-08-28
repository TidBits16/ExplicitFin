namespace Jellyfin.Plugin.ExplicitTagger;

/// <summary>Outcome of a title/artist/album search against one provider.</summary>
public sealed class ExplicitSearchResult
{
    public static ExplicitSearchResult Empty { get; } = new(false, false);

    public ExplicitSearchResult(bool hasExplicit, bool hasClean)
    {
        HasExplicit = hasExplicit;
        HasClean = hasClean;
    }

    public bool HasExplicit { get; }

    public bool HasClean { get; }

    public bool HasAny => HasExplicit || HasClean;

    public bool HasBoth => HasExplicit && HasClean;
}
