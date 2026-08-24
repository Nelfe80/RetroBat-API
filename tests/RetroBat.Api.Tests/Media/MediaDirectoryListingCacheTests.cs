using System;
using System.IO;
using System.Linq;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// HP3 - the cross-publication directory-listing cache. These pin the two guards the patch
/// leans on (a directory's own mtime, plus a safety TTL), the negative-cache window and its
/// explicit invalidation, LRU bounding, and the default-off pass-through - so a regression in
/// any of them fails CI rather than the cabinet.
/// </summary>
public sealed class MediaDirectoryListingCacheTests : IDisposable
{
    private readonly string _root;
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public MediaDirectoryListingCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hp3-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private MediaDirectoryListingCache NewCache(bool enabled = true, int safetyTtl = 5, int negativeTtl = 1, int max = 4096)
        => new(new MediaDirectoryListingCache.Config(enabled, safetyTtl, negativeTtl, max), () => _now);

    private string Dir(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "x");
        return dir;
    }

    [Fact]
    public void Disabled_reEnumeratesEveryCall()
    {
        var dir = Dir("d", "a.png");
        var cache = NewCache(enabled: false);

        cache.List(dir);
        cache.List(dir);
        cache.List(dir);

        Assert.Equal(3, cache.Metrics().Enumerations);
        Assert.Equal(0, cache.EntryCount()); // nothing stored when off
    }

    [Fact]
    public void Enabled_secondCallHits_noSecondEnumeration()
    {
        var dir = Dir("d", "a.png", "b.png");
        var cache = NewCache();

        var first = cache.List(dir);
        var second = cache.List(dir);

        Assert.Equal(2, first.Count);
        Assert.Equal(1, cache.Metrics().Enumerations);
        Assert.Equal(1, cache.Metrics().Hits);
        Assert.Equal(first.OrderBy(x => x), second.OrderBy(x => x));
    }

    [Fact]
    public void Enabled_directoryMtimeMoves_reEnumerates()
    {
        var dir = Dir("d", "a.png");
        var cache = NewCache();
        cache.List(dir); // cold, mtime captured

        File.WriteAllText(Path.Combine(dir, "b.png"), "x");
        // Force a distinct mtime so the test never rides on filesystem tick granularity.
        Directory.SetLastWriteTimeUtc(dir, Directory.GetLastWriteTimeUtc(dir).AddSeconds(5));

        var after = cache.List(dir);

        Assert.Equal(2, after.Count);
        Assert.Equal(2, cache.Metrics().Enumerations); // the moved mtime forced a fresh read
    }

    [Fact]
    public void Enabled_contentChangeWithSameMtime_staysHit()
    {
        // The listing is names only; a file whose CONTENT changed keeps its path, so the cache
        // rightly does not re-enumerate - the projection re-stats each file for size/mtime.
        var dir = Dir("d", "a.png");
        var cache = NewCache();
        cache.List(dir);

        var mtime = Directory.GetLastWriteTimeUtc(dir);
        File.WriteAllText(Path.Combine(dir, "a.png"), "much longer content");
        Directory.SetLastWriteTimeUtc(dir, mtime); // content changed, directory mtime did not

        cache.List(dir);
        Assert.Equal(1, cache.Metrics().Enumerations);
    }

    [Fact]
    public void Enabled_positiveEntry_expiresAfterSafetyTtl()
    {
        var dir = Dir("d", "a.png");
        var cache = NewCache(safetyTtl: 5);
        cache.List(dir);

        _now = _now.AddSeconds(6); // past the TTL, mtime untouched
        cache.List(dir);

        Assert.Equal(2, cache.Metrics().Enumerations);
    }

    [Fact]
    public void Enabled_absentDirectory_isNegativelyCached_untilTtl()
    {
        var dir = Path.Combine(_root, "later");
        var cache = NewCache(negativeTtl: 1);

        Assert.Empty(cache.List(dir)); // absent -> negative entry

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.png"), "x");

        Assert.Empty(cache.List(dir)); // still within negative TTL: not re-checked

        _now = _now.AddSeconds(2);
        Assert.Single(cache.List(dir)); // negative entry expired -> the new directory is seen
    }

    [Fact]
    public void Enabled_invalidate_makesNewDirectoryVisibleAtOnce()
    {
        var dir = Path.Combine(_root, "later");
        var cache = NewCache(negativeTtl: 60);
        Assert.Empty(cache.List(dir));

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.png"), "x");
        cache.InvalidateDirectory(dir); // HP4 seam: writer created the directory

        Assert.Single(cache.List(dir)); // no need to wait out the negative TTL
        Assert.Equal(1, cache.Metrics().Invalidations);
    }

    [Fact]
    public void InvalidatePath_dropsTheHoldingDirectory()
    {
        var dir = Dir("d", "a.png");
        var cache = NewCache();
        cache.List(dir);

        cache.InvalidatePath(Path.Combine(dir, "a.png"));
        File.WriteAllText(Path.Combine(dir, "b.png"), "x");
        Directory.SetLastWriteTimeUtc(dir, Directory.GetLastWriteTimeUtc(dir)); // mtime irrelevant: entry is gone

        Assert.Equal(2, cache.List(dir).Count);
        Assert.Equal(2, cache.Metrics().Enumerations);
    }

    [Fact]
    public void Enabled_lruEvicts_whenOverCapacity()
    {
        var cache = NewCache(max: 2);
        cache.List(Dir("a", "x.png"));
        cache.List(Dir("b", "x.png"));
        cache.List(Dir("c", "x.png"));

        Assert.Equal(2, cache.EntryCount());
        Assert.True(cache.Metrics().Evictions >= 1);
    }

    [Fact]
    public void Configure_disabling_clearsStore()
    {
        var dir = Dir("d", "a.png");
        var cache = NewCache();
        cache.List(dir);
        Assert.Equal(1, cache.EntryCount());

        cache.Configure(MediaDirectoryListingCache.Disabled);
        Assert.Equal(0, cache.EntryCount());
    }
}
