using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ExplicitTagger;

/// <summary>GUIDs ExplicitFin has already decided, so scheduled runs can skip them.</summary>
internal sealed class SeenStore
{
    private readonly string _path;
    private readonly ConcurrentDictionary<Guid, byte> _ids = new();
    private int _dirty;

    public SeenStore(string path)
    {
        _path = path;
        Load();
    }

    public bool Contains(Guid id) => _ids.ContainsKey(id);

    public void Add(Guid id)
    {
        if (id != Guid.Empty && _ids.TryAdd(id, 0))
        {
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    public void Save()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var lines = _ids.Keys.Select(id => id.ToString("N")).OrderBy(s => s, StringComparer.Ordinal);
        File.WriteAllLines(_path, lines);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        foreach (var line in File.ReadLines(_path))
        {
            if (Guid.TryParse(line.Trim(), out var id) && id != Guid.Empty)
            {
                _ids.TryAdd(id, 0);
            }
        }
    }
}
