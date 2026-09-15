using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.FinCommon;

public sealed class HttpCache
{
    private readonly string _dir;
    private readonly object _gate = new();

    public HttpCache(IApplicationPaths paths, string cacheFolderName)
        : this(Path.Combine(paths.CachePath, cacheFolderName))
    {
    }

    public HttpCache(string cacheDirectory)
    {
        _dir = cacheDirectory;
        Directory.CreateDirectory(_dir);
    }

    public bool TryGet(string key, TimeSpan ttl, out JsonElement element)
    {
        element = default;
        var fp = Path.Combine(_dir, Hash(key) + ".json");
        lock (_gate)
        {
            if (!File.Exists(fp))
            {
                return false;
            }

            if (ttl > TimeSpan.Zero && DateTime.UtcNow - File.GetLastWriteTimeUtc(fp) > ttl)
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(fp));
                element = doc.RootElement.Clone();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Set(string key, JsonElement payload)
    {
        var fp = Path.Combine(_dir, Hash(key) + ".json");
        lock (_gate)
        {
            File.WriteAllText(fp, payload.GetRawText());
        }
    }

    public void SetObject(string key, object payload)
    {
        var fp = Path.Combine(_dir, Hash(key) + ".json");
        lock (_gate)
        {
            File.WriteAllText(fp, JsonSerializer.Serialize(payload));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_dir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // best-effort clear
                }
            }
        }
    }

    private static string Hash(string key)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
