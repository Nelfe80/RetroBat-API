using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 8 — the secure asset resolver. The security cases are the point: a reference may only ever
/// resolve to a file at or under an ALLOWLISTED root. Traversal, absolute escape, crossing roots,
/// an unknown root, or a malformed reference must all resolve to null; a real user/canonical media
/// must round-trip.
/// </summary>
public class GamelistMediaAssetResolverTests : IDisposable
{
    private readonly string _base;
    private readonly string _mediaRoot;
    private readonly string _romsRoot;
    private readonly GamelistMediaAssetResolver _resolver;

    public GamelistMediaAssetResolverTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "apiexpose-asset-" + Guid.NewGuid().ToString("N"));
        _mediaRoot = Path.Combine(_base, "media");
        _romsRoot = Path.Combine(_base, "roms");
        Directory.CreateDirectory(Path.Combine(_mediaRoot, "systems", "gbc"));
        Directory.CreateDirectory(Path.Combine(_romsRoot, "gbc", "images"));
        File.WriteAllText(Path.Combine(_mediaRoot, "systems", "gbc", "wheel.png"), "canonical");
        File.WriteAllText(Path.Combine(_romsRoot, "gbc", "images", "user.png"), "user");
        // A sibling directory that a "roms" prefix attack might try to reach.
        Directory.CreateDirectory(Path.Combine(_base, "roms-evil"));
        File.WriteAllText(Path.Combine(_base, "roms-evil", "secret.png"), "secret");

        _resolver = new GamelistMediaAssetResolver(new Dictionary<string, string>
        {
            ["media"] = _mediaRoot,
            ["roms"] = _romsRoot
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    // Mirrors the production base64url encoding, to craft malicious references directly.
    private static string EncodeRef(string raw)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void CanonicalMedia_roundTrips()
    {
        // A gamelist value pointing into the canonical store (relative to roms/gbc/).
        var reference = _resolver.BuildReference("gbc", "./../../media/systems/gbc/wheel.png");
        Assert.NotNull(reference);
        Assert.Equal(Path.Combine(_mediaRoot, "systems", "gbc", "wheel.png"), _resolver.TryResolve(reference));
    }

    [Fact]
    public void UserMediaUnderRoms_roundTrips()
    {
        var reference = _resolver.BuildReference("gbc", "./images/user.png");
        Assert.NotNull(reference);
        Assert.Equal(Path.Combine(_romsRoot, "gbc", "images", "user.png"), _resolver.TryResolve(reference));
    }

    [Fact]
    public void Traversal_inGamelistPath_escapingAllRoots_buildsNothing()
    {
        var reference = _resolver.BuildReference("gbc", "../../../../../../Windows/System32/drivers/etc/hosts");
        Assert.Null(reference);
    }

    [Fact]
    public void CraftedReference_withTraversal_isRejected()
    {
        // Claims the roms root but climbs out to another directory.
        var malicious = EncodeRef("roms|../roms-evil/secret.png");
        Assert.Null(_resolver.TryResolve(malicious));
    }

    [Fact]
    public void CraftedReference_crossingRoots_isRejected()
    {
        // Real file, but under "media" while the reference claims "roms".
        var malicious = EncodeRef("roms|../media/systems/gbc/wheel.png");
        Assert.Null(_resolver.TryResolve(malicious));
    }

    [Fact]
    public void UnknownRoot_isRejected()
    {
        var malicious = EncodeRef("secrets|systems/gbc/wheel.png");
        Assert.Null(_resolver.TryResolve(malicious));
    }

    [Fact]
    public void AbsolutePath_inReference_isRejected()
    {
        var malicious = EncodeRef($"roms|{Path.Combine(_base, "roms-evil", "secret.png")}");
        Assert.Null(_resolver.TryResolve(malicious));
    }

    [Fact]
    public void MissingFile_resolvesToNull()
    {
        var reference = _resolver.BuildReference("gbc", "./images/does-not-exist.png");
        Assert.NotNull(reference);              // reference is well-formed (under an allowlisted root)
        Assert.Null(_resolver.TryResolve(reference)); // but the file is gone -> 404
    }

    [Fact]
    public void MalformedReference_resolvesToNull()
    {
        Assert.Null(_resolver.TryResolve("!!!not-base64!!!"));
        Assert.Null(_resolver.TryResolve(""));
        Assert.Null(_resolver.TryResolve(EncodeRef("no-separator-here")));
    }
}
