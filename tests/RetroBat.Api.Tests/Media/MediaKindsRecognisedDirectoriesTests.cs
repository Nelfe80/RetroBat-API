using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Guards the invariant behind the targeted inventory (HP1): the directory set the
/// inventory lists (<see cref="MediaKinds.RecognisedMediaDirectories"/>) stays in lock-step
/// with the qualifier <see cref="MediaKinds.FromRelativePath"/>. If the two drift, media
/// in a newly-qualified directory would silently vanish from snapshots.
///
/// Together with <see cref="MediaKindsFromRelativePathTests"/> (which proves nothing
/// qualifies OUTSIDE these directories) this closes the loop in both directions.
/// </summary>
public class MediaKindsRecognisedDirectoriesTests
{
    /// <summary>One file known to qualify inside each recognised directory.</summary>
    public static readonly IReadOnlyDictionary<string, string> Probe =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = "video.mp4",
            ["artwork"] = "artwork/fanart.jpg",
            ["artwork/box"] = "artwork/box/front.png",
            ["artwork/mix"] = "artwork/mix/mixrbv1.png",
            ["artwork/bezels"] = "artwork/bezels/bezel.png",
            ["artwork/marquee"] = "artwork/marquee/marquee.png",
            ["artwork/ic"] = "artwork/ic/ic.png",
            ["ui"] = "ui/steamgrid.png",
            ["ui/wheels"] = "ui/wheels/wheel.png",
            ["ui/logos"] = "ui/logos/logo.png",
            ["documents"] = "documents/manual.pdf",
            ["documents/maps"] = "documents/maps/map.png",
            ["themes"] = "themes/themehb.zip",
        };

    [Fact]
    public void Every_recognised_directory_qualifies_at_least_one_file()
    {
        foreach (var directory in MediaKinds.RecognisedMediaDirectories)
        {
            Assert.True(Probe.ContainsKey(directory),
                $"No probe file defined for recognised directory '{directory}'.");
            var kind = MediaKinds.FromRelativePath(Probe[directory]);
            Assert.False(string.IsNullOrEmpty(kind),
                $"Directory '{directory}' is listed as recognised but '{Probe[directory]}' qualified as nothing.");
        }
    }

    [Fact]
    public void Set_is_the_expected_directory_list()
    {
        // Change-detector: touching the recognised set (and therefore the inventory the
        // hot path enumerates) must be deliberate and paired with the qualifier switch.
        var expected = new[]
        {
            "", "artwork", "artwork/box", "artwork/mix", "artwork/bezels",
            "artwork/marquee", "artwork/ic", "ui", "ui/wheels", "ui/logos",
            "documents", "documents/maps", "themes",
        };

        Assert.Equal(
            expected.OrderBy(d => d, StringComparer.Ordinal),
            MediaKinds.RecognisedMediaDirectories.OrderBy(d => d, StringComparer.Ordinal));
    }
}
