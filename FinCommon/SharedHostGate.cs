using System.Collections.Concurrent;

namespace Jellyfin.Plugin.FinCommon;

/// <summary>
/// Cross-plugin pace for hosts multiple Fin plugins share (Deezer, MusicBrainz).
/// Uses a temp-dir file lock so vendored FinCommon copies in separate assemblies still serialize.
/// </summary>
public static class SharedHostGate
{
    private static readonly ConcurrentDictionary<string, object> LocalFallback = new(StringComparer.Ordinal);

    public static bool IsSharedHost(string url)
        => Classify(url) is not null;

    public static async Task WaitAsync(string url, TimeSpan minDelay, CancellationToken cancellationToken)
    {
        var key = Classify(url);
        if (key is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "jellyfin-finfamily-pace");
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, key + ".lock");
        var nextPath = Path.Combine(dir, key + ".next");

        // Prefer cross-assembly file lock; fall back to in-process lock if the FS rejects exclusive opens.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var fs = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                await PaceUnderLockAsync(nextPath, minDelay, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                var gate = LocalFallback.GetOrAdd(key, static _ => new object());
                lock (gate)
                {
                    PaceUnderLockAsync(nextPath, minDelay, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }

                return;
            }
        }
    }

    private static async Task PaceUnderLockAsync(
        string nextPath,
        TimeSpan minDelay,
        CancellationToken cancellationToken)
    {
        var next = DateTime.MinValue;
        if (File.Exists(nextPath))
        {
            try
            {
                var text = (await File.ReadAllTextAsync(nextPath, cancellationToken).ConfigureAwait(false)).Trim();
                if (long.TryParse(text, out var ticks))
                {
                    next = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
            catch (IOException)
            {
                // Treat as ready.
            }
        }

        var wait = next - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        var stamp = (DateTime.UtcNow + minDelay).Ticks.ToString();
        await File.WriteAllTextAsync(nextPath, stamp, cancellationToken).ConfigureAwait(false);
    }

    private static string? Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        if (host.Equals("api.deezer.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase))
        {
            return "deezer";
        }

        if (host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".musicbrainz.org", StringComparison.OrdinalIgnoreCase))
        {
            return "musicbrainz";
        }

        return null;
    }
}
