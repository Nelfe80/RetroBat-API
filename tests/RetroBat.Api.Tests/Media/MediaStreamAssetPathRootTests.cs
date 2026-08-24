using System;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// HP5 - PathRoot must be ADDITIVE: absent from the payload unless emitted, so an older
/// MarqueeManager sees byte-for-byte the same snapshot it always did. These pin the JsonIgnore
/// (WhenWritingNull) contract; the value itself mirrors the existing relative-path root choice.
/// </summary>
public class MediaStreamAssetPathRootTests
{
    private static PhysicalMediaWebSocketProjectionService.MediaStreamAsset Sample(string? pathRoot) => new(
        Kind: "wheel",
        Origin: "local",
        Path: "media/systems/megadrive/games/sonic/ui/wheels/wheel.png",
        FileName: "wheel.png",
        Stem: "wheel",
        Extension: "png",
        Length: 1,
        LastWriteTimeUtc: DateTime.UnixEpoch,
        Url: "")
    {
        PathRoot = pathRoot
    };

    [Fact]
    public void PathRoot_isOmitted_whenNull()
    {
        var json = JsonSerializer.Serialize(Sample(null));
        Assert.DoesNotContain("PathRoot", json);
    }

    [Fact]
    public void PathRoot_isPresent_whenSet()
    {
        var json = JsonSerializer.Serialize(Sample("apiexpose"));
        Assert.Contains("\"PathRoot\":\"apiexpose\"", json);
    }
}
