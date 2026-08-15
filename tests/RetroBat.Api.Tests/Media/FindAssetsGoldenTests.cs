using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// HP0 golden/parity tests for the legacy pattern searches
/// <c>PhysicalMediaWebSocketProjectionService.FindAssets</c> / <c>FindFirstAsset</c>,
/// used by <c>BuildGameMarqueeMedia</c> and friends. Today each does a
/// <c>Directory.EnumerateFiles(pattern, TopDirectoryOnly)</c> per root.
///
/// HP2 re-points these at the shared per-directory inventory; these tests pin their
/// observable contract (pattern match, ordinal-ignore-case ordering, top-directory-only,
/// first-root-first) so the migration is proven behaviour-preserving.
/// </summary>
public class FindAssetsGoldenTests
{
    [Fact]
    public void FindFirstAsset_matches_by_pattern_in_the_named_directory()
    {
        using var tree = new MediaTree().With("artwork/marquee/marquee.png");

        var asset = PhysicalMediaWebSocketProjectionService.FindFirstAsset(
            new[] { tree.Root }, "artwork", "marquee", "marquee.*");

        Assert.NotNull(asset);
        Assert.Equal("marquee.png", Path.GetFileName(asset!.Path));
    }

    [Fact]
    public void FindAssets_returns_all_matches_ordered_ordinal_ignore_case()
    {
        using var tree = new MediaTree()
            .With("artwork/marquee/dmd.png")
            .With("artwork/marquee/dmd-b.gif")
            .With("artwork/marquee/dmd-a.gif");

        var names = PhysicalMediaWebSocketProjectionService
            .FindAssets(new[] { tree.Root }, "artwork/marquee", "dmd*")
            .Select(a => Path.GetFileName(a.Path))
            .ToArray();

        // '-' (0x2D) sorts before '.' (0x2E), so the variants come before the base file.
        Assert.Equal(new[] { "dmd-a.gif", "dmd-b.gif", "dmd.png" }, names);
    }

    [Fact]
    public void FindAssets_is_top_directory_only_and_ignores_subdirectories()
    {
        using var tree = new MediaTree()
            .With("artwork/marquee/marquee.png")
            .With("artwork/marquee/sub/marquee.png"); // one level deeper -> must not match

        var list = PhysicalMediaWebSocketProjectionService
            .FindAssets(new[] { tree.Root }, "artwork/marquee", "marquee.*")
            .ToList();

        Assert.Single(list);
    }

    [Fact]
    public void FindAssets_yields_first_root_before_second()
    {
        using var userRoot = new MediaTree().With("artwork/marquee/marquee.png", "AA");   // 2 bytes
        using var systemRoot = new MediaTree().With("artwork/marquee/marquee.png", "BBB"); // 3 bytes

        var list = PhysicalMediaWebSocketProjectionService
            .FindAssets(new[] { userRoot.Root, systemRoot.Root }, "artwork/marquee", "marquee.*")
            .ToList();

        Assert.Equal(2, list.Count);       // distinct full paths, both kept
        Assert.Equal(2, list[0].Length);   // the first (user) root is yielded first
    }

    [Fact]
    public void FindFirstAsset_returns_null_when_the_directory_is_absent()
    {
        using var tree = new MediaTree(); // empty

        var asset = PhysicalMediaWebSocketProjectionService.FindFirstAsset(
            new[] { tree.Root }, "artwork", "marquee", "marquee.*");

        Assert.Null(asset);
    }
}
