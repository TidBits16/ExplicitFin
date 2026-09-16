namespace Jellyfin.Plugin.FinCommon;

/// <summary>
/// Remembers failed lookups (unknown artist/track) so scheduled runs skip them until TTL.
/// Force refresh clears the underlying <see cref="HttpCache"/> and retries everything.
/// </summary>
public static class LookupMiss
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    public static bool IsRemembered(HttpCache cache, string key)
        => cache.TryGet(Key(key), Ttl, out _);

    public static void Remember(HttpCache cache, string key)
        => cache.SetObject(Key(key), new { unknown = true });

    private static string Key(string key) => "lookup-miss/" + key.Trim();
}
