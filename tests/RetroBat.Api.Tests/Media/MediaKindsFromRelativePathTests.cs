using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// HP0 characterisation of the media qualifier <see cref="MediaKinds.FromRelativePath"/>.
///
/// This qualifier is the linchpin of the Hotpath patch's "same snapshots" contract:
/// the discovery refactor (HP1) changes HOW directories are listed, never WHAT a path
/// qualifies as. Pinning it here means any drift in the recognised set is caught by CI.
///
/// It also encodes, as executable facts, the proof that a system-scope inventory may
/// skip <c>games/</c> losslessly (H2): a file nested under an unrecognised directory
/// (anything deeper than the fixed set) qualifies as nothing, so it could never have
/// become a system asset in the first place.
/// </summary>
public class MediaKindsFromRelativePathTests
{
    [Theory]
    // root (directory == "")
    [InlineData("video.mp4", MediaKinds.Video)]
    [InlineData("video-normalized.mp4", MediaKinds.VideoNormalized)]
    // artwork/
    [InlineData("artwork/fanart.jpg", MediaKinds.Fanart)]
    [InlineData("artwork/flyer.png", MediaKinds.Flyer)]
    [InlineData("artwork/screenshot.png", MediaKinds.Thumbnail)]
    [InlineData("artwork/screentitle.png", MediaKinds.Image)]
    [InlineData("artwork/steamgrid.png", MediaKinds.SteamGrid)]
    [InlineData("artwork/cartridge.png", MediaKinds.Cartridge)]
    [InlineData("artwork/label.png", MediaKinds.Label)]
    [InlineData("artwork/figurine.png", MediaKinds.Figurine)]
    // artwork/box/
    [InlineData("artwork/box/3d.png", MediaKinds.Box3d)]
    [InlineData("artwork/box/front.png", MediaKinds.BoxFront)]
    [InlineData("artwork/box/back.png", MediaKinds.BoxBack)]
    [InlineData("artwork/box/side.png", MediaKinds.BoxSide)]
    [InlineData("artwork/box/texture.png", MediaKinds.BoxTexture)]
    // artwork/mix/
    [InlineData("artwork/mix/mixrbv1.png", MediaKinds.MixRbv1)]
    [InlineData("artwork/mix/mixrbv2.png", MediaKinds.MixRbv2)]
    // artwork/bezels/
    [InlineData("artwork/bezels/bezel.png", MediaKinds.Bezel)]
    // artwork/marquee/
    [InlineData("artwork/marquee/marquee.png", MediaKinds.Marquee)]
    [InlineData("artwork/marquee/screenmarquee.png", MediaKinds.ScreenMarquee)]
    [InlineData("artwork/marquee/screenmarquee-small.png", MediaKinds.ScreenMarqueeSmall)]
    [InlineData("artwork/marquee/topper.png", MediaKinds.Topper)]
    [InlineData("artwork/marquee/dmd.png", MediaKinds.Dmd)]
    [InlineData("artwork/marquee/generated-marquee.png", MediaKinds.GeneratedMarquee)]
    [InlineData("artwork/marquee/generated-system-marquee.png", MediaKinds.GeneratedMarquee)]
    [InlineData("artwork/marquee/generated-dmd.png", MediaKinds.GeneratedDmd)]
    [InlineData("artwork/marquee/generated-system-dmd.png", MediaKinds.GeneratedDmd)]
    [InlineData("artwork/marquee/dmd-scene1.gif", MediaKinds.DmdAnimation)] // stem starts with "dmd"
    // artwork/ic/
    [InlineData("artwork/ic/ic.png", MediaKinds.InstructionCard)]
    [InlineData("artwork/ic/ic-1.png", MediaKinds.InstructionCard)]
    // ui/
    [InlineData("ui/steamgrid.png", MediaKinds.SteamGrid)]
    [InlineData("ui/wheels/wheel.png", MediaKinds.Wheel)]
    [InlineData("ui/wheels/wheel-carbon.png", MediaKinds.WheelCarbon)]
    [InlineData("ui/wheels/wheel-steel.png", MediaKinds.WheelSteel)]
    [InlineData("ui/logos/logo.png", MediaKinds.Logo)]
    // documents/ , documents/maps/ , themes/
    [InlineData("documents/manual.pdf", MediaKinds.Manual)]
    [InlineData("documents/maps/map.png", MediaKinds.Map)]
    [InlineData("themes/themehb.zip", MediaKinds.ThemeHb)]
    public void Qualifies_recognised_paths(string relative, string expectedKind)
        => Assert.Equal(expectedKind, MediaKinds.FromRelativePath(relative));

    [Theory]
    [InlineData("ARTWORK\\MARQUEE\\MARQUEE.PNG", MediaKinds.Marquee)] // upper-case + backslashes
    [InlineData("ui\\wheels\\wheel.png", MediaKinds.Wheel)]
    public void Normalises_separators_and_case(string relative, string expectedKind)
        => Assert.Equal(expectedKind, MediaKinds.FromRelativePath(relative));

    [Theory]
    // A regional/style variant keeps its suffix as its own kind, so a consumer that
    // only knows the base kind never silently shows the wrong region.
    [InlineData("artwork/box/front-us.png", MediaKinds.BoxFront + "-us")]
    [InlineData("ui/wheels/wheel-jp.png", MediaKinds.Wheel + "-jp")]
    [InlineData("artwork/screentitle-eu.png", MediaKinds.Image + "-eu")]
    public void Keeps_regional_and_style_variants(string relative, string expectedKind)
        => Assert.Equal(expectedKind, MediaKinds.FromRelativePath(relative));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1942.zip")]                                  // root, unknown stem
    [InlineData("readme.txt")]                                // root, unknown stem
    [InlineData("artwork/marquee/random.png")]                // known dir, unknown stem, not dmd*
    [InlineData("artwork/unknown/thing.png")]                 // unknown sub-directory
    [InlineData("artwork/box/front/extra.png")]               // one level too deep under a known dir
    public void Rejects_unrecognised_paths(string? relative)
        => Assert.Null(MediaKinds.FromRelativePath(relative));

    /// <summary>
    /// H2 proof (executable): a game's media, addressed RELATIVE TO THE SYSTEM ROOT,
    /// lives under <c>games/&lt;slug&gt;/…</c>. Its directory is therefore never one of
    /// the recognised system directories, so <see cref="MediaKinds.FromRelativePath"/>
    /// returns null for it. A system-scope inventory that never descends into
    /// <c>games/</c> thus loses zero assets — it only skips wasted I/O.
    /// </summary>
    [Theory]
    [InlineData("games/1942/artwork/marquee/marquee.png")]
    [InlineData("games/sonic/ui/wheels/wheel.png")]
    [InlineData("games/1942/video.mp4")]
    public void System_scope_may_skip_games_losslessly(string relativeToSystemRoot)
        => Assert.Null(MediaKinds.FromRelativePath(relativeToSystemRoot));
}
