using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// HP0 golden/parity tests for <c>PhysicalMediaWebSocketProjectionService.BuildAssetTable</c>,
/// today implemented as a recursive <c>EnumerateFiles(root, "*", AllDirectories)</c> scan
/// filtered by <see cref="MediaKinds.FromRelativePath"/>.
///
/// These pin the OBSERVABLE result (kind → file, filtering, root priority, and that a
/// system scope never serves game media) so the HP1/HP2 refactor - which replaces the
/// recursive scan with a targeted per-directory inventory - can be proven behaviour-
/// preserving: these same tests must stay green.
/// </summary>
public class BuildAssetTableGoldenTests
{
    private static IReadOnlySet<string> Allow(params string[] kinds)
        => new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Qualifies_recognised_media_and_ignores_the_rest()
    {
        using var tree = new MediaTree()
            .With("video.mp4")
            .With("artwork/fanart.jpg")
            .With("artwork/box/front.png")
            .With("artwork/box/3d.png")
            .With("artwork/marquee/marquee.png")
            .With("artwork/marquee/dmd.png")
            .With("ui/wheels/wheel.png")
            .With("ui/logos/logo.png")
            .With("documents/manual.pdf")
            .With("artwork/marquee/random.png")   // known dir, unknown stem -> ignored
            .With("artwork/unknown/thing.png")     // unknown dir            -> ignored
            .With("artwork/box/sub/front.png");    // one level too deep     -> ignored

        var allowed = Allow(
            MediaKinds.Video, MediaKinds.Fanart, MediaKinds.BoxFront, MediaKinds.Box3d,
            MediaKinds.Marquee, MediaKinds.Dmd, MediaKinds.Wheel, MediaKinds.Logo, MediaKinds.Manual);

        var table = PhysicalMediaWebSocketProjectionService.BuildAssetTable(new[] { tree.Root }, allowed);

        var byKind = table.ToDictionary(
            kv => kv.Key,
            kv => Path.GetFileName(kv.Value.Path),
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal("video.mp4", byKind[MediaKinds.Video]);
        Assert.Equal("fanart.jpg", byKind[MediaKinds.Fanart]);
        Assert.Equal("front.png", byKind[MediaKinds.BoxFront]);
        Assert.Equal("3d.png", byKind[MediaKinds.Box3d]);
        Assert.Equal("marquee.png", byKind[MediaKinds.Marquee]);
        Assert.Equal("dmd.png", byKind[MediaKinds.Dmd]);
        Assert.Equal("wheel.png", byKind[MediaKinds.Wheel]);
        Assert.Equal("logo.png", byKind[MediaKinds.Logo]);
        Assert.Equal("manual.pdf", byKind[MediaKinds.Manual]);
        Assert.Equal(9, table.Count); // nothing unrecognised leaked in
    }

    [Fact]
    public void Allowed_set_restricts_the_table_to_its_surface()
    {
        using var tree = new MediaTree()
            .With("artwork/marquee/marquee.png")
            .With("ui/wheels/wheel.png")
            .With("ui/logos/logo.png");

        var table = PhysicalMediaWebSocketProjectionService.BuildAssetTable(
            new[] { tree.Root }, Allow(MediaKinds.Marquee));

        Assert.True(table.ContainsKey(MediaKinds.Marquee));
        Assert.False(table.ContainsKey(MediaKinds.Wheel));
        Assert.False(table.ContainsKey(MediaKinds.Logo));
        Assert.Single(table);
    }

    [Fact]
    public void First_root_wins_so_media_user_keeps_priority()
    {
        using var userRoot = new MediaTree().With("artwork/marquee/marquee.png", "USER");       // 4 bytes
        using var systemRoot = new MediaTree().With("artwork/marquee/marquee.png", "SYSTEMSYS"); // 9 bytes

        var table = PhysicalMediaWebSocketProjectionService.BuildAssetTable(
            new[] { userRoot.Root, systemRoot.Root }, Allow(MediaKinds.Marquee));

        Assert.Equal(4, table[MediaKinds.Marquee].Length); // the first (user) root won
    }

    [Fact]
    public void System_scope_never_serves_media_nested_under_games()
    {
        using var systemRoot = new MediaTree()
            .With("artwork/marquee/marquee.png", "SYS")                   // the real system marquee (3 bytes)
            .With("games/1942/artwork/marquee/marquee.png", "GAME1942");  // must be invisible to the system scope

        var table = PhysicalMediaWebSocketProjectionService.BuildAssetTable(
            new[] { systemRoot.Root }, Allow(MediaKinds.Marquee));

        Assert.Single(table);
        Assert.Equal(3, table[MediaKinds.Marquee].Length); // system file, never the one under games/
    }
}
