using Xunit;

namespace Puck.State.Tests;

public sealed class ReferenceScheduleMemoryProfileLawTests {
    [Fact]
    public void TheServiceFormIsTheExplicitConservativeOne() {
        Assert.Equal(
            "MemoryCycles = Starts * StartupCycles + DependentAccesses * AdditionalLatencyCycles + ceil(BytesTransferred * BandwidthDenominator / BandwidthNumerator)",
            ReferenceScheduleManifest.MemoryServiceForm
        );
        foreach (var text in new[] {
            ReferenceScheduleManifest.MemoryAdditionalLatencyDefinition,
            ReferenceScheduleManifest.MemoryAggregation,
            ReferenceScheduleManifest.MemoryResidencyRule,
        }) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: text));
        }
    }
    [Fact]
    public void EveryDeclaredAccessClassIsRecordedExactlyOnceAndSaysWhatItTransfers() {
        Assert.Equal(
            Enum.GetValues<MemoryAccessClass>().Order(),
            ReferenceScheduleManifest.MemoryClasses.Select(selector: entry => entry.Class).Order()
        );
        foreach (var entry in ReferenceScheduleManifest.MemoryClasses) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: entry.TransferredBytes));
            Assert.Contains(
                actualString: entry.TransferredBytes,
                expectedSubstring: "line"
            );
        }
    }
    [Fact]
    public void EveryMemoryCoefficientIsEitherSubstantiatedOrExplicitlyUnmodeled() {
        foreach (var entry in ReferenceScheduleManifest.MemoryClasses) {
            foreach (var coefficient in new[] { entry.StartupCycles, entry.AdditionalLatencyCycles }) {
                Assert.False(condition: coefficient.IsOverflow);
                if (coefficient.IsKnown) {
                    Assert.True(condition: (coefficient.Cycles >= 0L));
                    continue;
                }
                Assert.False(condition: string.IsNullOrWhiteSpace(value: coefficient.Reason));
            }
            if (entry.Bandwidth is { } rate) {
                Assert.True(condition: (rate.Numerator > 0L));
                Assert.True(condition: (rate.Denominator > 0L));
                Assert.Null(@object: entry.BandwidthUnmodeled);
                continue;
            }
            Assert.False(condition: string.IsNullOrWhiteSpace(value: entry.BandwidthUnmodeled));
        }
    }
    [Fact]
    public void AnUnmodeledClassNeverPricesAsZeroAndAProvedEmptyOperationDoes() {
        foreach (var entry in ReferenceScheduleManifest.MemoryClasses) {
            var empty = CostModel.MemoryCycles(
                accessClass: entry.Class,
                bytesTransferred: 0L,
                dependentAccesses: 0L,
                starts: 0L
            );

            Assert.True(condition: empty.IsKnown);
            Assert.Equal(
                0L,
                empty.Cycles
            );

            var substantiated = (entry.StartupCycles.IsKnown && entry.AdditionalLatencyCycles.IsKnown && (entry.Bandwidth is not null));
            var priced = CostModel.MemoryCycles(
                accessClass: entry.Class,
                bytesTransferred: 64L,
                dependentAccesses: 1L,
                starts: 1L
            );

            Assert.Equal(
                substantiated,
                priced.IsKnown
            );
            if (!substantiated) {
                Assert.True(condition: priced.IsUnmodeled);
                Assert.False(condition: string.IsNullOrWhiteSpace(value: priced.Reason));
            }
        }
    }
}
