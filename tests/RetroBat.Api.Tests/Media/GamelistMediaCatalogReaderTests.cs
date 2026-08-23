using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 3 — reading a user gamelist as a media source. These pin what a &lt;game&gt; entry yields:
/// a binding per present media tag, a durable tag naming its kind (explicit-gamelist), the file
/// name adding a kind (filename-convention), and the rule that matters most — a GENERIC slot
/// (image / marquee / thumbnail) never invents a kind on its own (§7.3).
/// </summary>
public sealed class GamelistMediaCatalogReaderTests : IDisposable
{
    private readonly string _root;
    private readonly MediaQualificationService _qual = new();

    public GamelistMediaCatalogReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lot3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "images"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void PlaceFile(string relative)
        => File.WriteAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), "x");

    private static XElement Game(params (string Tag, string Value)[] fields)
    {
        var game = new XElement("game", new XElement("path", "./game.zip"));
        foreach (var (tag, value) in fields)
        {
            game.Add(new XElement(tag, value));
        }

        return game;
    }

    private GamelistGameMedia? Extract(XElement game)
        => GamelistMediaCatalogReader.ExtractGameMedia(game, _root, _qual);

    [Fact]
    public void DurableTag_yieldsExplicitGamelistCandidate()
    {
        PlaceFile("images/foo.jpg");
        var media = Extract(Game(("fanart", "./images/foo.jpg")));

        Assert.NotNull(media);
        Assert.Contains(media!.Bindings, b => b.Slot == "fanart");
        Assert.Contains(media.Candidates,
            c => c.Kind == MediaKinds.Fanart && c.Qualification == MediaQualifications.ExplicitGamelist);
    }

    [Fact]
    public void GenericSlot_qualifiesByFileName_neverBySlot()
    {
        PlaceFile("images/foo-wheel.png");
        var media = Extract(Game(("marquee", "./images/foo-wheel.png")));

        Assert.NotNull(media);
        Assert.Contains(media!.Bindings, b => b.Slot == "marquee");
        // the wheel kind comes from the FILE name, not the marquee slot
        Assert.Contains(media.Candidates,
            c => c.Kind == MediaKinds.Wheel && c.Qualification == MediaQualifications.FilenameConvention);
        // and the marquee slot alone must NOT create a marquee kind (§7.3)
        Assert.DoesNotContain(media.Candidates, c => c.Kind == MediaKinds.Marquee);
    }

    [Fact]
    public void GenericSlot_plainName_bindsButYieldsNoCandidate()
    {
        PlaceFile("images/foo.png");
        var media = Extract(Game(("image", "./images/foo.png")));

        Assert.NotNull(media);
        Assert.Contains(media!.Bindings, b => b.Slot == "image");
        Assert.Empty(media.Candidates); // generic slot + plain name => no semantic kind (§7.3)
    }

    [Fact]
    public void MissingFile_isNotBound()
    {
        // read-only: a tag pointing at a file that is not there yields nothing.
        Assert.Null(Extract(Game(("image", "./images/absent.png"))));
    }

    [Fact]
    public void NoMediaTags_returnsNull()
        => Assert.Null(Extract(Game()));
}
