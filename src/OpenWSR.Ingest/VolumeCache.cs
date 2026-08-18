namespace OpenWSR.Ingest;

/// <summary>
/// Disk cache for downloaded volumes under %LOCALAPPDATA%\OpenWSR\volumes, with a size
/// cap enforced by last-access LRU eviction.
/// </summary>
public sealed class VolumeCache(string? root = null, long maxBytes = 2L * 1024 * 1024 * 1024)
{
    public string Root { get; } = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenWSR", "volumes");

    /// <summary>Local path for an S3 key; null when not cached.</summary>
    public string? TryGet(string s3Key)
    {
        var path = PathFor(s3Key);
        if (!File.Exists(path)) return null;
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        return path;
    }

    /// <summary>Atomically store a downloaded file, then evict past the size cap.</summary>
    public string Store(string s3Key, string tempFile)
    {
        var path = PathFor(s3Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Move(tempFile, path, overwrite: true);
        Evict();
        return path;
    }

    public string PathFor(string s3Key) =>
        Path.Combine(Root, s3Key.Replace('/', Path.DirectorySeparatorChar));

    private void Evict()
    {
        if (!Directory.Exists(Root)) return;
        var files = new DirectoryInfo(Root)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .ToList();
        long total = files.Sum(f => f.Length);
        if (total <= maxBytes) return;

        foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
        {
            try
            {
                total -= file.Length;
                file.Delete();
            }
            catch (IOException)
            {
                // In use — skip; it will be revisited on the next eviction pass.
            }
            if (total <= maxBytes) break;
        }
    }
}
