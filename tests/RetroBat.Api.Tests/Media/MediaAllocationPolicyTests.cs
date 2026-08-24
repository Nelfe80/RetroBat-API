using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 5 (§10.1) - the pure allocation decision. These pin the exit criterion: enriching the
/// catalog never overwrites a user binding, fills empty slots, updates only slots APIExpose still
/// owns, and releases ownership the moment the gamelist value no longer matches what we wrote.
/// </summary>
public class MediaAllocationPolicyTests
{
    [Fact]
    public void FillMissing_fillsEmptySlot_andTakesOwnership()
    {
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.FillMissing, "wheel.png", existing: "", apiExposeOwnsExisting: false);

        Assert.True(d.Write);
        Assert.Equal("wheel.png", d.Value);
        Assert.True(d.MarkManaged);
        Assert.False(d.AbandonOwnership);
    }

    [Fact]
    public void FillMissing_neverOverwritesUserBinding()
    {
        // Slot already holds a value APIExpose does NOT own → preserve it, and drop any stale ownership.
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.FillMissing, "new.png", existing: "user-choice.png", apiExposeOwnsExisting: false);

        Assert.False(d.Write);
        Assert.True(d.AbandonOwnership);
    }

    [Fact]
    public void FillMissing_updatesSlotApiExposeStillOwns()
    {
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.FillMissing, "wheel-v2.png", existing: "wheel-v1.png", apiExposeOwnsExisting: true);

        Assert.True(d.Write);
        Assert.Equal("wheel-v2.png", d.Value);
        Assert.True(d.MarkManaged);
    }

    [Fact]
    public void FillMissing_ownedButUnchanged_writesNothing()
    {
        // We own it and the resolved value is identical → zero write (feeds "no save if no change").
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.FillMissing, "wheel.png", existing: "wheel.png", apiExposeOwnsExisting: true);

        Assert.False(d.Write);
        Assert.False(d.AbandonOwnership);
    }

    [Fact]
    public void FillMissing_noResolvedValue_leavesSlotAlone()
    {
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.FillMissing, preferred: "", existing: "", apiExposeOwnsExisting: false);

        Assert.False(d.Write);
        Assert.False(d.AbandonOwnership);
    }

    [Fact]
    public void Force_overwritesEvenUserBinding()
    {
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.Force, "forced.png", existing: "user-choice.png", apiExposeOwnsExisting: false);

        Assert.True(d.Write);
        Assert.Equal("forced.png", d.Value);
        Assert.True(d.MarkManaged);
    }

    [Fact]
    public void Force_withNothingToWrite_isNoOp()
    {
        var d = MediaAllocationPolicy.Decide(MediaWritePolicy.Force, preferred: null, existing: "user-choice.png", apiExposeOwnsExisting: false);

        Assert.False(d.Write);
    }

    [Theory]
    [InlineData("fill_missing", MediaWritePolicy.FillMissing)]
    [InlineData("FillMissing", MediaWritePolicy.FillMissing)]
    [InlineData("managed", MediaWritePolicy.Managed)]
    [InlineData("force", MediaWritePolicy.Force)]
    [InlineData("", MediaWritePolicy.FillMissing)]
    [InlineData(null, MediaWritePolicy.FillMissing)]
    [InlineData("nonsense", MediaWritePolicy.FillMissing)]
    public void Parse_mapsKnownValues_andDefaultsToFillMissing(string? raw, MediaWritePolicy expected)
    {
        Assert.Equal(expected, MediaAllocationPolicy.Parse(raw));
    }
}
