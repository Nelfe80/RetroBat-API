using System.Xml.Linq;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 5 — gamelists must never carry an empty media tag. When the FillMissing policy leaves a slot
/// unwritten, the in-place path must still drop a present-but-empty element (matching the legacy
/// write helpers), while never disturbing a real binding.
/// </summary>
public class EmptyMediaSlotHygieneTests
{
    private static XElement Game(string inner) => XElement.Parse($"<game>{inner}</game>");

    [Fact]
    public void RemovesEmptyElement()
    {
        var game = Game("<image></image>");
        var changed = GamelistUpdateService.RemoveEmptyMediaSlot(game, "image");

        Assert.True(changed);
        Assert.Null(game.Element("image"));
    }

    [Fact]
    public void RemovesSelfClosingEmptyElement()
    {
        var game = Game("<image />");
        var changed = GamelistUpdateService.RemoveEmptyMediaSlot(game, "image");

        Assert.True(changed);
        Assert.Null(game.Element("image"));
    }

    [Fact]
    public void RemovesWhitespaceOnlyElement()
    {
        var game = Game("<image>   </image>");
        var changed = GamelistUpdateService.RemoveEmptyMediaSlot(game, "image");

        Assert.True(changed);
        Assert.Null(game.Element("image"));
    }

    [Fact]
    public void PreservesNonEmptyBinding()
    {
        var game = Game("<image>./user-choice.png</image>");
        var changed = GamelistUpdateService.RemoveEmptyMediaSlot(game, "image");

        Assert.False(changed);
        Assert.Equal("./user-choice.png", game.Element("image")!.Value);
    }

    [Fact]
    public void AbsentElement_isNoOp()
    {
        var game = Game("<name>Game</name>");
        var changed = GamelistUpdateService.RemoveEmptyMediaSlot(game, "image");

        Assert.False(changed);
    }
}
