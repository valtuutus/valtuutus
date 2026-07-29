using Valtuutus.Data;
using FluentAssertions;

namespace Valtuutus.Data.InMemory.Tests;

public class SnapTokenWatermarkSpecs
{
    [Fact]
    public void ComputeWatermark_produces_a_26_char_snap_token()
    {
        var watermark = SnapTokenWatermark.Compute(DateTimeOffset.UtcNow, TimeSpan.FromDays(7));

        watermark.Value.Length.Should().Be(26);
    }

    [Fact]
    public void ComputeWatermark_sorts_below_a_real_ulid_generated_at_the_same_instant()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var watermark = SnapTokenWatermark.Compute(cutoff.AddDays(7), TimeSpan.FromDays(7));

        var sameInstant = Ulid.NewUlid(cutoff);

        string.CompareOrdinal(watermark.Value, sameInstant.ToString()).Should().BeLessThan(0);
    }

    [Fact]
    public void ComputeWatermark_sorts_above_a_real_ulid_generated_before_the_cutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var retention = TimeSpan.FromDays(7);
        var watermark = SnapTokenWatermark.Compute(now, retention);

        var beforeCutoff = Ulid.NewUlid(now - retention - TimeSpan.FromMinutes(1));

        string.CompareOrdinal(beforeCutoff.ToString(), watermark.Value).Should().BeLessThan(0);
    }

    [Fact]
    public void ComputeWatermark_sorts_below_a_real_ulid_generated_after_the_cutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var retention = TimeSpan.FromDays(7);
        var watermark = SnapTokenWatermark.Compute(now, retention);

        var afterCutoff = Ulid.NewUlid(now - retention + TimeSpan.FromMinutes(1));

        string.CompareOrdinal(watermark.Value, afterCutoff.ToString()).Should().BeLessThan(0);
    }
}
