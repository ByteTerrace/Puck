using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="AllocationWindow"/>: a body that allocates nothing reads zero, a body that allocates in every
/// window fails naming the window, the GC mode, and the type it allocated, sampled on the measuring thread, and a total
/// runs its body once and counts what that run allocated.
/// </summary>
public sealed class AllocationWindowLawTests {
    private static KnownAllocation? Sink;
    private static int Counter;

    // The type the allocating body makes; its name is what the verdict must carry.
    private sealed class KnownAllocation {
        public long Payload { get; } = 1L;
    }

    [Fact]
    public void ABodyThatAllocatesNothingReadsZero() {
        static void Body() =>
            Counter++;

        Body();

        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Body)
        );
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Measure(window: Body)
        );
    }
    [Fact]
    public void ABodyThatAllocatesFailsNamingTheTypeItAllocated() {
        static void Body() =>
            Sink = new KnownAllocation();

        Body();

        Assert.NotEqual(
            expected: 0L,
            actual: AllocationWindow.Measure(window: Body)
        );

        var verdict = Assert.Throws<AllocationWindowException>(testCode: () => AllocationWindow.Least(window: Body));

        Assert.Equal(
            expected: nameof(ABodyThatAllocatesFailsNamingTheTypeItAllocated),
            actual: verdict.WindowName
        );
        Assert.True(condition: (verdict.Bytes > 0L));
        Assert.Equal(
            expected: AllocationWindow.GcMode,
            actual: verdict.GcMode
        );
        Assert.Contains(
            collection: verdict.SampledTypes,
            filter: static sample => sample.TypeName.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: nameof(KnownAllocation)
            )
        );
        Assert.Contains(
            expectedSubstring: nameof(KnownAllocation),
            actualString: verdict.Message
        );
        Assert.Contains(
            expectedSubstring: AllocationWindow.GcMode,
            actualString: verdict.Message
        );
        GC.KeepAlive(obj: Sink);
    }
    [Fact]
    public void ATotalRunsTheBodyOnceAndCountsWhatItAllocated() {
        const int Length = 4096;
        var runs = 0;
        byte[]? kept = null;

        var total = AllocationWindow.Total(window: () => {
            runs++;
            kept = new byte[Length];
        });

        Assert.Equal(
            actual: runs,
            expected: 1
        );
        Assert.InRange(
            actual: total,
            high: (Length + 1024L),
            low: Length
        );
        GC.KeepAlive(obj: kept);
    }
}
