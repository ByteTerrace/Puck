using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a domain chain past the copy budget is refused from its branch counts, so refusing costs O(1) memory
/// however many copies the authored values name. A domain list is authored content — a federated world hands one over
/// — and the routine that refuses it is the same routine a hostile value would otherwise make pay for its own
/// refusal: 2049 cells per axis is 8.6e9 frames, past <see cref="Array.MaxLength"/>, and a polar count has no
/// representable ceiling at all.
/// <para>Each arm pairs a denial with a control differing in one authored number.</para>
/// </summary>
public sealed class SdfDomainExpansionBudgetLawTests {
    // The refusal's whole working set is three small lists and the refusal string. A materializing refusal allocates
    // ~134 bytes per frame, so any measurement near this ceiling means the frames were built before the budget looked.
    private const long RefusalAllocationCeiling = (64L * 1024L);

    private static long MeasureRefusal(IReadOnlyList<SdfDomainOp> domain, string expectedName) {
        // Warm the path so the measurement below sees the expansion's allocations, not the JIT's.
        _ = SdfDomainExpansion.TryExpand(
            domain: [new SdfDomainOp.Symmetry(Normal: Vector3.UnitX)],
            frames: out _,
            refusal: out _
        );

        var before = GC.GetAllocatedBytesForCurrentThread();
        var expanded = SdfDomainExpansion.TryExpand(
            domain: domain,
            frames: out var frames,
            refusal: out var refusal
        );
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.False(condition: expanded);
        Assert.Empty(collection: frames);
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: expectedName
        );

        return allocated;
    }

    [Fact]
    public void RepeatLimitPastTheBudgetRefusesWithoutMaterializing() {
        var allocated = MeasureRefusal(
            domain: [
                new SdfDomainOp.Repeat(
                    Limit: new Vector3(value: 120f),
                    Spacing: Vector3.One
                ),
            ],
            expectedName: "repeat"
        );

        Assert.True(
            condition: (allocated <= RefusalAllocationCeiling),
            userMessage: $"refusing a 241^3-cell repeat allocated {allocated} bytes, so the frames were built before the budget was consulted"
        );
    }
    [Fact]
    public void RepeatLimitWithinTheBudgetExpands() {
        Assert.True(
            condition: SdfDomainExpansion.TryExpand(
                domain: [
                    new SdfDomainOp.Repeat(
                        Limit: new Vector3(value: 1f),
                        Spacing: Vector3.One
                    ),
                ],
                frames: out var frames,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        Assert.Equal(
            actual: frames.Length,
            expected: 27
        );
    }
    [Fact]
    public void RepeatLimitAtTheBudgetEdgeStillExpands() {
        // 3^3 = 27 <= 64 < 5^3 = 125: the pair straddles the default budget on a single authored number.
        Assert.True(condition: SdfDomainExpansion.TryExpand(
            domain: [
                new SdfDomainOp.Repeat(
                    Limit: new Vector3(value: 1f),
                    Spacing: Vector3.One
                ),
            ],
            frames: out _,
            refusal: out _
        ));
        Assert.False(condition: SdfDomainExpansion.TryExpand(
            domain: [
                new SdfDomainOp.Repeat(
                    Limit: new Vector3(value: 2f),
                    Spacing: Vector3.One
                ),
            ],
            frames: out _,
            refusal: out _
        ));
    }
    [Fact]
    public void PolarCountPastTheBudgetRefusesWithoutMaterializing() {
        var allocated = MeasureRefusal(
            domain: [new SdfDomainOp.Polar(Count: int.MaxValue)],
            expectedName: "polar"
        );

        Assert.True(
            condition: (allocated <= RefusalAllocationCeiling),
            userMessage: $"refusing a {int.MaxValue}-sector polar allocated {allocated} bytes, so the frames were built before the budget was consulted"
        );
    }
    [Fact]
    public void PolarCountWithinTheBudgetExpands() {
        Assert.True(
            condition: SdfDomainExpansion.TryExpand(
                domain: [new SdfDomainOp.Polar(Count: 6)],
                frames: out var frames,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        Assert.Equal(
            actual: frames.Length,
            expected: 6
        );
    }
    [Fact]
    public void PolarMirrorDoublesTheCountAgainstTheBudget() {
        // The same authored count, admitted unmirrored and refused mirrored: the budget reads the branch count the op
        // would produce, not the count field.
        Assert.True(condition: SdfDomainExpansion.TryExpand(
            domain: [new SdfDomainOp.Polar(Count: 40)],
            frames: out var plain,
            refusal: out _
        ));
        Assert.Equal(
            actual: plain.Length,
            expected: 40
        );
        Assert.False(condition: SdfDomainExpansion.TryExpand(
            domain: [
                new SdfDomainOp.Polar(
                    Count: 40,
                    Mirror: true
                ),
            ],
            frames: out _,
            refusal: out var refusal
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "polar"
        );
    }
    [Fact]
    public void AChainRefusesAtTheOpThatOverrunsTheBudget() {
        // Symmetry (2) then polar (40) is 80 copies: the chain is refused, and the refusal names the op whose branch
        // set overran it rather than the chain as a whole.
        Assert.False(condition: SdfDomainExpansion.TryExpand(
            domain: [
                new SdfDomainOp.Symmetry(Normal: Vector3.UnitX),
                new SdfDomainOp.Polar(Count: 40),
            ],
            frames: out _,
            refusal: out var refusal
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "polar"
        );
        Assert.True(
            condition: SdfDomainExpansion.TryExpand(
                domain: [
                    new SdfDomainOp.Symmetry(Normal: Vector3.UnitX),
                    new SdfDomainOp.Polar(Count: 30),
                ],
                frames: out var frames,
                refusal: out var control
            ),
            userMessage: control
        );
        Assert.Equal(
            actual: frames.Length,
            expected: 60
        );
    }
}
