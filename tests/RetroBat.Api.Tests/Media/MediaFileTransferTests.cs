using System;
using System.IO;
using System.Threading.Tasks;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 9 — the migration's transfer primitive. The invariants that make a destructive migration safe:
/// Copy never removes the source; Move removes it only after a verified copy is in place; an existing
/// target is replaced with verified content; a missing source is a no-op.
/// </summary>
public class MediaFileTransferTests : IDisposable
{
    private readonly string _dir;

    public MediaFileTransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "apiexpose-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Copy_leavesSource_andCreatesIdenticalTarget()
    {
        var source = Write("src/a.png", "PIXELS");
        var target = Path.Combine(_dir, "store/a.png");

        var result = await MediaFileTransfer.TransferAsync(source, target, MigrationTransferMode.Copy);

        Assert.True(result.Success);
        Assert.True(File.Exists(source));                 // source preserved
        Assert.Equal("PIXELS", File.ReadAllText(target)); // target created, identical
        Assert.False(File.Exists(target + ".migrating.tmp"));
    }

    [Fact]
    public async Task Move_deletesSource_afterVerifiedCopy()
    {
        var source = Write("src/b.png", "DATA");
        var target = Path.Combine(_dir, "store/b.png");

        var result = await MediaFileTransfer.TransferAsync(source, target, MigrationTransferMode.Move);

        Assert.True(result.Success);
        Assert.False(File.Exists(source));                // source removed
        Assert.Equal("DATA", File.ReadAllText(target));
    }

    [Fact]
    public async Task Copy_replacesExistingTarget_withVerifiedContent_keepingSource()
    {
        var source = Write("src/c.png", "NEW");
        var target = Write("store/c.png", "OLD");

        var result = await MediaFileTransfer.TransferAsync(source, target, MigrationTransferMode.Copy);

        Assert.True(result.Success);
        Assert.True(File.Exists(source));
        Assert.Equal("NEW", File.ReadAllText(target));    // existing target replaced
    }

    [Fact]
    public async Task Move_replacesExistingTarget_andRemovesSource()
    {
        var source = Write("src/d.png", "FRESH");
        var target = Write("store/d.png", "STALE");

        var result = await MediaFileTransfer.TransferAsync(source, target, MigrationTransferMode.Move);

        Assert.True(result.Success);
        Assert.False(File.Exists(source));
        Assert.Equal("FRESH", File.ReadAllText(target));
    }

    [Fact]
    public async Task MissingSource_isNoOp()
    {
        var source = Path.Combine(_dir, "src/gone.png");
        var target = Path.Combine(_dir, "store/gone.png");

        var result = await MediaFileTransfer.TransferAsync(source, target, MigrationTransferMode.Move);

        Assert.False(result.Success);
        Assert.Equal("source-missing", result.Reason);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("move", MigrationTransferMode.Move)]
    [InlineData("Move", MigrationTransferMode.Move)]
    [InlineData("copy", MigrationTransferMode.Copy)]
    [InlineData("", MigrationTransferMode.Copy)]
    [InlineData(null, MigrationTransferMode.Copy)]
    [InlineData("nonsense", MigrationTransferMode.Copy)]
    public void ParseMode_defaultsToCopy(string? raw, MigrationTransferMode expected)
    {
        Assert.Equal(expected, MediaFileTransfer.ParseMode(raw));
    }
}
