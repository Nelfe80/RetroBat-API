using System.IO;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 5 (§11) - the ownership sidecar. These pin the contract the FillMissing policy relies on:
/// ownership survives a reload, is asserted only while the gamelist still holds what we wrote,
/// releases cleanly, persists atomically outside roms/, and never writes when nothing changed.
/// </summary>
public class MediaSidecarStoreTests : IDisposable
{
    private readonly string _dir;

    public MediaSidecarStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "apiexpose-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private MediaSidecarStore NewStore() => new(_dir);

    [Fact]
    public void RecordedOwnership_survivesReload()
    {
        var a = NewStore();
        a.RecordManaged("gbc", "./game.zip", "wheel", "wheel.png");
        a.Save("gbc");

        var b = NewStore();
        Assert.True(b.OwnsCurrentValue("gbc", "./game.zip", "wheel", "wheel.png"));
        Assert.Equal("wheel.png", b.GetOwnership("gbc", "./game.zip", "wheel").LastValue);
    }

    [Fact]
    public void OwnsCurrentValue_falseWhenGamelistValueChangedExternally()
    {
        var store = NewStore();
        store.RecordManaged("gbc", "./game.zip", "wheel", "wheel.png");

        // User edited the slot in ES → the current value differs from what we wrote → not ours.
        Assert.False(store.OwnsCurrentValue("gbc", "./game.zip", "wheel", "user-choice.png"));
    }

    [Fact]
    public void Abandon_releasesOwnership()
    {
        var store = NewStore();
        store.RecordManaged("gbc", "./game.zip", "wheel", "wheel.png");
        store.AbandonOwnership("gbc", "./game.zip", "wheel");

        Assert.False(store.GetOwnership("gbc", "./game.zip", "wheel").Managed);
    }

    [Fact]
    public void RomPath_isMatchedRegardlessOfSlashOrLeadingDot()
    {
        var store = NewStore();
        store.RecordManaged("gbc", "./Sub/Game.zip", "wheel", "wheel.png");

        // Windows-style separators + no leading dot resolve to the same key.
        Assert.True(store.OwnsCurrentValue("gbc", "sub\\game.zip", "wheel", "wheel.png"));
    }

    [Fact]
    public void Save_writesUnderBaseDirectory_notRoms()
    {
        var store = NewStore();
        store.RecordManaged("gbc", "./game.zip", "wheel", "wheel.png");
        store.Save("gbc");

        var expected = Path.Combine(_dir, "gbc", "sidecar.json");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Save_isSkipped_whenNothingChanged()
    {
        var store = NewStore();
        var path = Path.Combine(_dir, "gbc", "sidecar.json");

        // Nothing recorded → no file at all.
        store.Save("gbc");
        Assert.False(File.Exists(path));

        store.RecordManaged("gbc", "./game.zip", "wheel", "wheel.png");
        store.Save("gbc");
        var firstWrite = File.GetLastWriteTimeUtc(path);

        // Recording the identical value again must not rewrite the file.
        store.RecordManaged("gbc", "./game.zip", "wheel", "wheel.png");
        store.Save("gbc");

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void CorruptSidecar_isTreatedAsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "gbc"));
        File.WriteAllText(Path.Combine(_dir, "gbc", "sidecar.json"), "{ not valid json ");

        var store = NewStore();
        Assert.False(store.GetOwnership("gbc", "./game.zip", "wheel").Managed);
    }
}
