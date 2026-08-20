using System.IO;

namespace RetroBat.Api.Media;

/// <summary>
/// HP3 — a bounded, cross-publication cache of a directory's TOP-LEVEL file listing.
///
/// HP1/HP2 already list every recognised media directory once per publication; this cache
/// keeps those listings BETWEEN publications so a hot revisit (navigating back to a game or
/// system already seen) re-enumerates nothing. It is deliberately gated OFF by default: with
/// <see cref="Config.Enabled"/> false the class is a straight pass-through to the disk, so the
/// code ships dark and is turned on by the canary (see the MediaDiscovery options).
///
/// Correctness rests on two guards, not on explicit invalidation alone:
///  * every hit re-reads the directory's own mtime — NTFS bumps it on any add / remove /
///    rename of a direct child, so a file appearing or vanishing is caught immediately, the
///    exact "no stale index" the patch demands;
///  * the listing carries only file PATHS. A file whose CONTENT changed keeps its path, and
///    the projection re-stats every path through CreateAsset, so size / mtime stay live.
/// A safety TTL backstops filesystems that do not update the directory mtime, and writers may
/// still invalidate explicitly (HP4) — chiefly to turn a negatively-cached absent directory
/// positive the instant APIExpose creates it.
/// </summary>
internal sealed class MediaDirectoryListingCache
{
    internal sealed record Config(bool Enabled, int SafetyTtlSeconds, int NegativeTtlSeconds, int MaxDirectories);

    /// <summary>Off, with the plan's default budgets. Real values arrive via Configure().</summary>
    public static Config Disabled { get; } = new(false, 5, 1, 4096);

    private sealed class Entry
    {
        public required string Directory;
        public bool Exists;
        public DateTime DirMtimeUtc;
        public DateTime CapturedUtc;
        public IReadOnlyList<string> Files = Array.Empty<string>();
        public long AccessTick;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;
    private volatile Config _config;
    private long _accessClock;

    // Observability (§14): cheap counters guarded by the same lock, read as a snapshot.
    private long _hits, _misses, _enumerations, _evictions, _invalidations;

    public MediaDirectoryListingCache(Config? config = null, Func<DateTime>? now = null)
    {
        _config = config ?? Disabled;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public Config Current => _config;

    /// <summary>Applies new options. Turning the cache off (or shrinking it) drops the store so
    /// stale listings can never survive a rollback of the flag.</summary>
    public void Configure(Config config)
    {
        lock (_gate)
        {
            _config = config;
            if (!config.Enabled)
            {
                _entries.Clear();
            }
            else
            {
                TrimToCapacity(config.MaxDirectories);
            }
        }
    }

    /// <summary>
    /// The top-level file paths of <paramref name="directory"/>, empty when it does not exist.
    /// A pass-through enumeration when the cache is disabled; otherwise a validated hit or a
    /// single (re)load. <paramref name="onEnumerate"/> fires only when the disk was actually
    /// read, so callers can keep their enumeration metrics honest.
    /// </summary>
    public IReadOnlyList<string> List(string directory, Action? onEnumerate = null)
    {
        var config = _config;
        if (!config.Enabled)
        {
            // Pass-through, no lock: preserve today's lock-free behaviour when the cache is off.
            return Enumerate(directory, onEnumerate);
        }

        lock (_gate)
        {
            var key = Normalize(directory);
            if (_entries.TryGetValue(key, out var entry) && IsFresh(entry, config))
            {
                entry.AccessTick = ++_accessClock;
                _hits++;
                return entry.Files;
            }

            _misses++;
            var loaded = Load(directory, onEnumerate);
            loaded.AccessTick = ++_accessClock;
            _entries[key] = loaded;
            TrimToCapacity(config.MaxDirectories);
            return loaded.Files;
        }
    }

    /// <summary>Drops the entry for the directory that holds <paramref name="fullPath"/>. A
    /// writer calls this at the point it knows the final file, so the next publication sees the
    /// mutation without waiting for the TTL — and a newly created directory stops being
    /// negatively cached at once.</summary>
    public void InvalidatePath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            InvalidateDirectory(directory);
        }
    }

    public void InvalidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var key = Normalize(directory);
        lock (_gate)
        {
            if (_entries.Remove(key)) _invalidations++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public int EntryCount()
    {
        lock (_gate) { return _entries.Count; }
    }

    public Snapshot Metrics()
    {
        lock (_gate)
        {
            return new Snapshot(_hits, _misses, Interlocked.Read(ref _enumerations), _evictions, _invalidations, _entries.Count);
        }
    }

    internal readonly record struct Snapshot(
        long Hits, long Misses, long Enumerations, long Evictions, long Invalidations, int Entries);

    // ── internals (all called under _gate) ───────────────────────────────────────

    private bool IsFresh(Entry entry, Config config)
    {
        var age = _now() - entry.CapturedUtc;
        if (!entry.Exists)
        {
            // Absent directories carry no mtime to trust: a short TTL is the only guard, and an
            // explicit invalidation on creation covers the moment it appears.
            return age < TimeSpan.FromSeconds(Math.Max(0, config.NegativeTtlSeconds));
        }

        if (age >= TimeSpan.FromSeconds(Math.Max(0, config.SafetyTtlSeconds)))
        {
            return false;
        }

        // The decisive guard: the directory's own mtime. Any add/remove/rename of a direct
        // child moves it, so the listing can never silently miss a file that appeared.
        try
        {
            return Directory.Exists(entry.Directory) &&
                   Directory.GetLastWriteTimeUtc(entry.Directory) == entry.DirMtimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Entry Load(string directory, Action? onEnumerate)
    {
        if (!Directory.Exists(directory))
        {
            return new Entry { Directory = directory, Exists = false, CapturedUtc = _now() };
        }

        try
        {
            var mtime = Directory.GetLastWriteTimeUtc(directory);
            var files = Enumerate(directory, onEnumerate);
            return new Entry
            {
                Directory = directory,
                Exists = true,
                DirMtimeUtc = mtime,
                CapturedUtc = _now(),
                Files = files
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Treat an unreadable directory as absent for the short negative window rather than
            // caching a half-read listing.
            return new Entry { Directory = directory, Exists = false, CapturedUtc = _now() };
        }
    }

    private IReadOnlyList<string> Enumerate(string directory, Action? onEnumerate)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            onEnumerate?.Invoke();
            Interlocked.Increment(ref _enumerations);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    private void TrimToCapacity(int maxDirectories)
    {
        var max = Math.Max(1, maxDirectories);
        while (_entries.Count > max)
        {
            // LRU: drop the least-recently-accessed entry. O(n) but only when full, which the
            // budget (4096) keeps rare; the listings themselves are the memory cost, not this.
            string? oldestKey = null;
            var oldest = long.MaxValue;
            foreach (var (k, v) in _entries)
            {
                if (v.AccessTick < oldest) { oldest = v.AccessTick; oldestKey = k; }
            }

            if (oldestKey == null) break;
            _entries.Remove(oldestKey);
            _evictions++;
        }
    }

    private static string Normalize(string directory)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch { return directory; }
    }
}
