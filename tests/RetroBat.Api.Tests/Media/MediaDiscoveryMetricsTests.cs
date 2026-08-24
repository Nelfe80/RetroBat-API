using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// The measured payoff of HP1, as a test. Before the refactor, a system-scope
/// <c>BuildAssetTable</c> recursively walked the ENTIRE subtree - including every game's
/// media under <c>games/</c> - so its cost grew with the library (the arcade "10 s" scan).
/// The targeted inventory reads only the handful of recognised directories, so the cost
/// is now CONSTANT regardless of how many games exist. These assertions pin that.
/// </summary>
public class MediaDiscoveryMetricsTests
{
    private static IReadOnlySet<string> Allow(params string[] kinds)
        => new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(500)]
    public void System_scope_cost_is_independent_of_the_number_of_games(int gameCount)
    {
        using var systemRoot = new MediaTree()
            .With("artwork/marquee/marquee.png"); // the one asset a system scope serves

        for (var i = 0; i < gameCount; i++)
            systemRoot.With($"games/game{i}/artwork/marquee/marquee.png");

        var metrics = PhysicalMediaWebSocketProjectionService.BeginMetricsScope();
        var table = PhysicalMediaWebSocketProjectionService.BuildAssetTable(
            new[] { systemRoot.Root }, Allow(MediaKinds.Marquee));

        // Still lossless: only the real system marquee is served, never a game's.
        Assert.Single(table);

        // No recursive walk anymore, and games/ is never entered: exactly ONE media file
        // is read (the system marquee), whatever the number of games. This is the whole
        // point of the patch - cost decoupled from library size.
        Assert.Equal(0, metrics.RecursiveScans);
        Assert.Equal(0, metrics.RecursiveFilesVisited);
        Assert.Equal(1, metrics.PatternFilesVisited);
    }

    [Fact]
    public void Inventory_scope_enumerates_each_directory_once_across_surfaces()
    {
        using var tree = new MediaTree()
            .With("artwork/marquee/marquee.png")
            .With("ui/wheels/wheel.png");

        var metrics = PhysicalMediaWebSocketProjectionService.BeginMetricsScope();
        using (PhysicalMediaWebSocketProjectionService.BeginInventoryScope())
        {
            // Four surfaces of one publication asking the same roots.
            PhysicalMediaWebSocketProjectionService.BuildAssetTable(new[] { tree.Root }, Allow(MediaKinds.Marquee));
            PhysicalMediaWebSocketProjectionService.BuildAssetTable(new[] { tree.Root }, Allow(MediaKinds.Wheel));
            PhysicalMediaWebSocketProjectionService.BuildAssetTable(new[] { tree.Root }, Allow(MediaKinds.Marquee));
            PhysicalMediaWebSocketProjectionService.BuildAssetTable(new[] { tree.Root }, Allow(MediaKinds.Logo));
        }

        // The two media files were read ONCE for the whole scope, not once per surface.
        Assert.Equal(2, metrics.PatternFilesVisited);
    }
}
