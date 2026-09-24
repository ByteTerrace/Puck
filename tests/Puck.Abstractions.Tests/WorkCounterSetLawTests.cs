using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="WorkCounterSet"/>: each count or add lands exactly once in its own kind, writers on many threads
/// lose nothing, a total never goes down and nothing resets it, an undeclared kind is unavailable to a reader and refused
/// to a writer, the set keeps the kinds and classes its owner declared, and reading every kind allocates nothing.
/// </summary>
public sealed class WorkCounterSetLawTests {
    private static readonly WorkKind Requests = new(name: "test.set.requests", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    private static readonly WorkKind Hits = new(name: "test.set.hits", unit: "count", workClass: WorkClass.Pacing);
    private static readonly WorkKind Bytes = new(name: "test.set.bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    private static readonly WorkKind Foreign = new(name: "test.other.visits", unit: "count", workClass: WorkClass.Deterministic);

    private static WorkCounterSet Set() =>
        new(
            kinds: [Requests, Hits, Bytes],
            name: "test.set"
        );

    [Fact]
    public void EachWriteLandsOnceInItsOwnKind() {
        var set = Set();

        set.Count(kind: Requests);
        set.Count(kind: Requests);
        set.Add(amount: 40L, kind: Bytes);
        set.Add(amount: 0L, kind: Bytes);

        Assert.Equal(expected: 2L, actual: set.Read(kind: Requests));
        Assert.Equal(expected: 0L, actual: set.Read(kind: Hits));
        Assert.Equal(expected: 40L, actual: set.Read(kind: Bytes));
        Assert.True(condition: set.TryRead(kind: Bytes, value: out var bytes));
        Assert.Equal(actual: bytes, expected: 40L);
    }
    [Fact]
    public void WritersOnManyThreadsLoseNothing() {
        const int Writers = 8;
        const int Writes = 10_000;
        var set = Set();

        _ = Parallel.For(
            body: _ => {
                for (var write = 0; (write < Writes); write++) {
                    set.Count(kind: Requests);
                    set.Add(amount: 3L, kind: Bytes);
                }
            },
            fromInclusive: 0,
            toExclusive: Writers
        );

        Assert.Equal(expected: ((1L * Writers) * Writes), actual: set.Read(kind: Requests));
        Assert.Equal(expected: ((3L * Writers) * Writes), actual: set.Read(kind: Bytes));
    }
    [Fact]
    public void ATotalNeverGoesDown() {
        var set = Set();
        var previous = 0L;

        for (var write = 0; (write < 16); write++) {
            set.Count(kind: Hits);

            var current = set.Read(kind: Hits);

            Assert.True(condition: (current > previous));
            previous = current;
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => set.Add(amount: -1L, kind: Hits));
        Assert.Equal(expected: previous, actual: set.Read(kind: Hits));
    }
    [Fact]
    public void AnUndeclaredKindIsUnavailableToAReaderAndRefusedToAWriter() {
        var set = Set();

        Assert.False(condition: set.TryRead(kind: Foreign, value: out var value));
        Assert.Equal(actual: value, expected: 0L);
        _ = Assert.Throws<ArgumentException>(testCode: () => set.Count(kind: Foreign));
        _ = Assert.Throws<ArgumentException>(testCode: () => set.Read(kind: Foreign));
    }
    [Fact]
    public void TheSetKeepsItsOwnersKindsAndClasses() {
        var set = Set();

        Assert.Equal(expected: "test.set", actual: set.Name);
        Assert.Equal(expected: [Requests, Hits, Bytes], actual: set.WorkKinds.ToArray());
        Assert.Equal(
            expected: [WorkClass.PerBackendDeterministic, WorkClass.Pacing, WorkClass.Deterministic],
            actual: set.WorkKinds.ToArray().Select(selector: static kind => kind.Class)
        );
    }
    [Fact]
    public void AMalformedSetIsRefused() {
        _ = Assert.Throws<ArgumentException>(testCode: () => new WorkCounterSet(kinds: [Requests], name: "set"));
        _ = Assert.Throws<ArgumentNullException>(testCode: () => new WorkCounterSet(kinds: [Requests], name: null!));
        _ = Assert.Throws<ArgumentException>(testCode: () => new WorkCounterSet(kinds: [], name: "test.set"));
        _ = Assert.Throws<ArgumentException>(testCode: () => new WorkCounterSet(kinds: [Requests, Requests], name: "test.set"));
        _ = Assert.Throws<ArgumentException>(testCode: () => new WorkCounterSet(kinds: [Requests, null!], name: "test.set"));
    }
    [Fact]
    public void ReadingEveryKindAllocatesNothing() {
        var set = Set();
        IWorkCounterSource source = set;

        set.Count(kind: Requests);

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                foreach (var kind in source.WorkKinds) {
                    _ = source.TryRead(kind: kind, value: out _);
                }
            }),
            expected: 0L
        );
    }
}
