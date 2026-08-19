using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// The generated logo/marquee caches key on the FRONTEND id so arcade sub-systems
/// (mame/fbneo/fba/hbmame, all collapsed to systemId "arcade") stop overwriting each
/// other's single file. A 1:1 system keeps the plain name — no new files, no migration.
/// </summary>
public class FrontendScopedFileNameTests
{
    [Fact]
    public void Collapsed_arcade_subsystems_get_a_frontend_suffixed_name()
    {
        Assert.Equal("wheel.fbneo.png",
            PhysicalMediaWebSocketProjectionService.FrontendScopedFileName("wheel", ".png", "fbneo", "arcade"));
        Assert.Equal("wheel.mame.png",
            PhysicalMediaWebSocketProjectionService.FrontendScopedFileName("wheel", ".png", "mame", "arcade"));
        Assert.Equal("generated-system-marquee.fba.png",
            PhysicalMediaWebSocketProjectionService.FrontendScopedFileName("generated-system-marquee", ".png", "fba", "arcade"));
    }

    [Fact]
    public void One_to_one_systems_keep_the_plain_name()
    {
        // frontend == systemId (snes), and the jaguar-style rename where the collapse is a
        // pure alias (one frontend, one systemId) both stay on the plain file.
        Assert.Equal("wheel.png",
            PhysicalMediaWebSocketProjectionService.FrontendScopedFileName("wheel", ".png", "snes", "snes"));
        Assert.Equal("generated-system-marquee.png",
            PhysicalMediaWebSocketProjectionService.FrontendScopedFileName("generated-system-marquee", ".png", "ARCADE", "arcade"));
    }

    [Fact]
    public void Empty_frontend_id_falls_back_to_the_plain_name()
    {
        Assert.Equal("wheel.png",
            PhysicalMediaWebSocketProjectionService.FrontendScopedFileName("wheel", ".png", "", "arcade"));
    }
}
