using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Resolution logic for the carbon/active-theme system logo &amp; fanart index: alias table,
/// "-w" white preference, collection language suffix, active-theme-before-carbon priority,
/// and the fanart differences (no white, no language). Fixture-based, machine-independent.
/// </summary>
public class ThemeSystemArtIndexTests
{
    // A fake theme set: an "active" theme and es-theme-carbon, each with art/logos + art/background.
    private static ThemeSystemArtIndex BuildFixture(MediaTree tree)
    {
        tree.With("active/art/logos/snes.svg")                 // active wins over carbon for snes
            .With("active/art/logos/mame.svg")
            .With("active/art/logos/mame-w.svg")               // white variant preferred
            .With("active/art/logos/atarijaguar.svg")          // reached via alias jaguar
            .With("active/art/logos/auto-verticalarcade.svg")  // collection base (EN)
            .With("active/art/logos/auto-verticalarcade-fr.svg") // collection localized
            .With("active/art/background/snes.jpg")
            .With("active/art/background/atarijaguar.jpg")      // fanart via alias
            .With("es-theme-carbon/art/logos/snes.svg")        // also here (carbon fallback)
            .With("es-theme-carbon/art/logos/carbononly.svg")  // only in carbon
            .With("es-theme-carbon/art/background/carbononly.jpg");

        return ThemeSystemArtIndex.Build(new[]
        {
            Path.Combine(tree.Root, "active"),
            Path.Combine(tree.Root, "es-theme-carbon"),
        });
    }

    [Fact]
    public void Logo_prefers_active_theme_then_falls_back_to_carbon()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith(Path.Combine("active", "art", "logos", "snes.svg"),
            index.ResolveLogoPath("snes", "snes", null));
        Assert.EndsWith(Path.Combine("es-theme-carbon", "art", "logos", "carbononly.svg"),
            index.ResolveLogoPath("carbononly", "carbononly", null));
    }

    [Fact]
    public void Logo_prefers_the_white_variant()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith("mame-w.svg", index.ResolveLogoPath("mame", "mame", null));
    }

    [Fact]
    public void Logo_resolves_through_the_alias_table()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith("atarijaguar.svg", index.ResolveLogoPath("jaguar", "jaguar", null));
    }

    [Theory]
    [InlineData("fr", "auto-verticalarcade-fr.svg")] // localized when present
    [InlineData("ja", "auto-verticalarcade.svg")]    // falls back to base when the language is absent
    [InlineData(null, "auto-verticalarcade.svg")]    // no language -> base
    public void Collection_logo_uses_language_then_base(string? language, string expectedFile)
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith(expectedFile, index.ResolveLogoPath("auto-verticalarcade", "auto-verticalarcade", language));
    }

    [Fact]
    public void Fanart_uses_alias_and_ignores_white_and_language()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.EndsWith(Path.Combine("active", "art", "background", "atarijaguar.jpg"),
            index.ResolveFanartPath("jaguar", "jaguar"));
    }

    [Fact]
    public void Returns_null_for_an_unknown_system()
    {
        using var tree = new MediaTree();
        var index = BuildFixture(tree);

        Assert.Null(index.ResolveLogoPath("does-not-exist", "does-not-exist", "fr"));
        Assert.Null(index.ResolveFanartPath("does-not-exist", null));
    }

    [Fact]
    public void Empty_theme_set_resolves_to_null_without_throwing()
    {
        var index = ThemeSystemArtIndex.Build(Array.Empty<string>());

        Assert.Null(index.ResolveLogoPath("snes", "snes", null));
        Assert.Null(index.ResolveFanartPath("snes", null));
    }
}
