using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a lease is as wide as it was asked for and default-valued, a lease taken while
/// another is open never aliases it, a returned buffer serves the next lease without allocating, and a buffer that
/// held references lets go of them when it is returned.</summary>
public sealed class ArenaScratchLawTests {
    [Fact]
    public void ALeaseIsDefaultValuedHoweverItsBufferWasLeft() {
        var scratch = new ArenaScratch();

        using (var first = scratch.Rent<long>(length: 40)) {
            first.Span.Fill(value: long.MaxValue);
        }

        using var second = scratch.Rent<long>(length: 24);

        Assert.Equal(
            24,
            second.Span.Length
        );
        Assert.True(condition: (second.Span.IndexOfAnyExcept(value: 0L) < 0));
    }
    [Fact]
    public void ANestedLeaseNeverAliasesTheOneStillOpen() {
        var scratch = new ArenaScratch();

        using var outer = scratch.Rent<long>(length: 8);

        outer.Span.Fill(value: 7L);

        using (var inner = scratch.Rent<long>(length: 4096)) {
            inner.Span.Fill(value: -1L);
        }

        using var other = scratch.Rent<int>(length: 8);

        other.Span.Fill(value: -1);

        Assert.True(condition: (outer.Span.IndexOfAnyExcept(value: 7L) < 0));
    }
    [Fact]
    public void AWarmedScratchLeasesWithoutAllocating() {
        var scratch = new ArenaScratch();

        void Evaluate() {
            using var values = scratch.Rent<long>(length: 4096);
            using var marks = scratch.Rent<bool>(length: 256);
            using var nested = scratch.Rent<long>(length: 64);

            values.Span[0] = (marks.Span.Length + nested.Span.Length);
        }

        Evaluate();

        var before = AllocationWindow.Least(window: () => {
            for (var round = 0; (round < 1_000); round++) {
                Evaluate();
            }
        });

        Assert.Equal(
            0L,
            before
        );
    }
    [Fact]
    public void AReturnedBufferLetsGoOfTheReferencesItHeld() {
        var scratch = new ArenaScratch();
        var held = new WeakReference(target: null);

        void Hold() {
            using var lease = scratch.Rent<object?>(length: 4);
            var target = new object();

            held.Target = target;
            lease.Span[2] = target;
        }

        Hold();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(condition: held.IsAlive);
    }
    [Fact]
    public void ALeaseOfNothingIsEmpty() {
        var scratch = new ArenaScratch();

        using var none = scratch.Rent<long>(length: 0);
        using var negative = scratch.Rent<long>(length: -3);

        Assert.True(condition: none.Span.IsEmpty);
        Assert.True(condition: negative.Span.IsEmpty);
    }
}
