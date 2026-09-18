using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TagShelfCommon;

public sealed class HttpCache
{
    private readonly string _dir;
    private readonly object _gate = new();

    public HttpCache(IApplicationPaths paths, string cacheFolderName, string? legacyCacheFolderName = null)
        : this(Path.Combine(paths.CachePath, cacheFolderName))
    {
        if (!string.IsNullOrEmpty(legacyCacheFolderName))
        {
            TryMigrateLegacyCache(Path.Combine(paths.CachePath, legacyCacheFolderName), _dir);
        }
    }

    public HttpCache(string cacheDirectory)
    {
        _dir = cacheDirectory;
        Directory.CreateDirectory(_dir);
    }

    private static void TryMigrateLegacyCache(string legacyDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir))
            {
                return;
            }

            Directory.CreateDirectory(newDir);
            if (Directory.EnumerateFileSystemEntries(newDir).Any())
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(legacyDir))
            {
                var name = Path.GetFileName(entry);
                var dest = Path.Combine(newDir, name);
                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, dest);
                }
                else
                {
                    File.Move(entry, dest);
                }
            }

            try
            {
                Directory.Delete(legacyDir, recursive: false);
            }
            catch
            {
                // Leave empty legacy dir if not removable.
            }
        }
        catch
        {
            // Best-effort; cold cache is fine.
        }
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
                try
                {
                    File.Delete(fp);
                }
                catch
                {
                    // best-effort; treat as miss either way
                }

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
