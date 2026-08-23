using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 2 — the shared qualification lifted out of the migration. These pin the two things that
/// must not drift: the SUFFIX PRECEDENCE (most specific wins) and the folder constraints, plus the
/// qualification-source label each path reports. A file is typed here without any migration.
/// </summary>
public class MediaQualificationServiceTests
{
    private readonly MediaQualificationService _svc = new();

    private (bool ok, string @base, string kind, string qualification) Qualify(
        string folder, string stem, string ext = ".png")
    {
        var ok = _svc.TryQualify(folder, stem, ext, out var b, out var k, out var q);
        return (ok, b, k, q);
    }

    [Theory]
    // The specific suffix must beat the generic one it contains.
    [InlineData("videos", "sonic-video-normalized", ".mp4", MediaKinds.VideoNormalized)]
    [InlineData("images", "sonic-box3d", ".png", MediaKinds.Box3d)]
    [InlineData("images", "sonic-boxtexture", ".png", MediaKinds.BoxTexture)]
    [InlineData("images", "sonic-screenmarquee", ".png", MediaKinds.ScreenMarquee)]
    [InlineData("images", "sonic-mixrbv1", ".png", MediaKinds.MixRbv1)]
    [InlineData("images", "sonic-wheelcarbon", ".png", MediaKinds.WheelCarbon)]
    public void SuffixPrecedence_mostSpecificWins(string folder, string stem, string ext, string expectedKind)
    {
        var r = Qualify(folder, stem, ext);
        Assert.True(r.ok);
        Assert.Equal(expectedKind, r.kind);
        Assert.Equal("sonic", r.@base);
        Assert.Equal(MediaQualifications.FilenameConvention, r.qualification);
    }

    [Fact]
    public void GenericBoxSuffix_stillResolves_whenNoSpecificMatches()
    {
        var r = Qualify("images", "sonic-box");
        Assert.True(r.ok);
        Assert.Equal(MediaKinds.BoxFront, r.kind);
        Assert.Equal(MediaQualifications.FilenameConvention, r.qualification);
    }

    [Fact]
    public void FolderConstraint_rejectsAKindTheFolderDoesNotAllow()
    {
        // A "-video" file sitting under images/ is not an image-folder medium.
        var r = Qualify("images", "sonic-video");
        Assert.False(r.ok);
    }

    [Theory]
    // No suffix: the folder's own default kind applies (folder-convention).
    [InlineData("images", "sonic", ".png", MediaKinds.Thumbnail)]
    [InlineData("videos", "sonic", ".mp4", MediaKinds.Video)]
    [InlineData("manuals", "sonic", ".pdf", MediaKinds.Manual)]
    public void FolderDefault_appliesWhenNoSuffix(string folder, string stem, string ext, string expectedKind)
    {
        var r = Qualify(folder, stem, ext);
        Assert.True(r.ok);
        Assert.Equal(expectedKind, r.kind);
        Assert.Equal(MediaQualifications.FolderConvention, r.qualification);
    }

    [Fact]
    public void ThemeHbFolder_typesByFolder()
    {
        var r = Qualify("themehb", "sonic", ".xml");
        Assert.True(r.ok);
        Assert.Equal(MediaKinds.ThemeHb, r.kind);
        Assert.Equal(MediaQualifications.FolderConvention, r.qualification);
    }

    [Fact]
    public void Unrecognised_isNotQualified()
    {
        // No known suffix and an extension the folder default does not cover.
        var r = Qualify("images", "sonic", ".xyz");
        Assert.False(r.ok);
    }
}
