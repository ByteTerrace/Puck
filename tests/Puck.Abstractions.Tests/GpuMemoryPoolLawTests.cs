using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuMemoryPool{TKey, TBlock}"/> over numbered fake blocks: a stale copy of a returned placement is
/// refused even once its offset holds another resource, which stays live; a bind that throws gives its range back; a
/// key keeps one empty shared block and frees a second, and a resource's own block goes when it empties; retirement
/// frees every block once, takes nothing back and places nothing.
/// </summary>
public sealed class GpuMemoryPoolLawTests {
    private const ulong Block = 1024UL;

    [Fact]
    public void PressureReleasesEmptyBlocksAndRemembersLiveBlocksUntilTheirLastReturn() {
        var (pool, log) = Pool();
        var empty = Place(key: 0, pool: pool, size: 64);
        var live = Place(key: 1, pool: pool, size: 64);
        var neighbour = Place(key: 1, pool: pool, size: 64);

        pool.Return(placement: empty);
        pool.Trim();
        pool.Trim();
        Assert.Equal(actual: log, expected: ["alloc 1 1024", "alloc 2 1024", "free 1"]);
        Assert.Equal(1, pool.Blocks);
        pool.Return(placement: live);
        Assert.Equal(1, pool.Blocks);
        pool.Return(placement: neighbour); // The owner's fence can return this much later than the pressure request.
        Assert.Equal(0, pool.Blocks);
        Assert.Equal(actual: log, expected: ["alloc 1 1024", "alloc 2 1024", "free 1", "free 2"]);
        Assert.Throws<InvalidOperationException>(testCode: () => pool.Return(placement: neighbour));
        var fresh = Place(key: 0, pool: pool, size: 64);

        pool.Return(placement: fresh); // New blocks do not inherit an obsolete pressure request.
        Assert.Equal(1, pool.Blocks);
        pool.FreeAll();
        pool.Trim();
        Assert.Equal(0, pool.Blocks);
    }
    [Fact]
    public void AStaleReturnIsRefusedOnceItsOffsetHoldsAnotherResource() {
        var (pool, log) = Pool();
        var first = Place(key: 0, pool: pool, size: 64UL);

        pool.Return(placement: first);

        var second = Place(key: 0, pool: pool, size: 64UL);

        Assert.Equal(expected: first.Offset, actual: second.Offset);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Return(placement: first));
        // The second resource kept its range: the next placement does not overlap it.
        Assert.Equal(expected: 64UL, actual: Place(key: 0, pool: pool, size: 64UL).Offset);
        pool.Return(placement: second);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Return(placement: second));
        Assert.Equal(actual: log, expected: ["alloc 1 1024"]);
    }
    [Fact]
    public void ABindThatThrowsGivesItsRangeBackAndItsOwnBlockGoes() {
        var (pool, log) = Pool();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Place<int>(alignment: 1UL, bind: static _ => throw new InvalidOperationException(), key: 0, size: 64UL));
        Assert.Equal(expected: 0UL, actual: Place(key: 0, pool: pool, size: 64UL).Offset);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Place<int>(alignment: 1UL, bind: static _ => throw new InvalidOperationException(), key: 1, size: (Block * 2UL)));
        Assert.Equal(actual: log, expected: ["alloc 1 1024", "alloc 2 2048", "free 2"]);
        Assert.Equal(expected: 1, actual: pool.Blocks);
    }
    [Fact]
    public void AKeyKeepsOneEmptySharedBlockAndAnOwnBlockGoesWhenItEmpties() {
        var (pool, log) = Pool();
        var a = Place(key: 0, pool: pool, size: Block);
        var b = Place(key: 0, pool: pool, size: 512UL);
        var c = Place(key: 0, pool: pool, size: 512UL);
        var d = Place(key: 0, pool: pool, size: 512UL);

        // An own block (past half a block) goes as it empties; of two empty shared blocks, the second goes.
        pool.Return(placement: a);
        pool.Return(placement: b);
        pool.Return(placement: c);
        pool.Return(placement: d);
        Assert.Equal(actual: log, expected: ["alloc 1 1024", "alloc 2 1024", "alloc 3 1024", "free 1", "free 3"]);
        Assert.Equal(expected: 1, actual: pool.Blocks);
    }
    [Fact]
    public void RetirementFreesEveryBlockOnceTakesNothingBackAndPlacesNothing() {
        var (pool, log) = Pool();
        var live = Place(key: 0, pool: pool, size: 64UL);

        _ = Place(key: 1, pool: pool, size: 64UL);
        Assert.True(condition: pool.FreeAll());
        Assert.False(condition: pool.FreeAll());
        pool.Return(placement: live);
        Assert.Equal(actual: log, expected: ["alloc 1 1024", "alloc 2 1024", "free 1 retiring", "free 2 retiring"]);
        Assert.True(condition: pool.IsRetired);
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => Place(key: 0, pool: pool, size: 64UL));
    }

    private static GpuMemoryPlacement<int> Place(GpuMemoryPool<int, int> pool, int key, ulong size) =>
        pool.Place(alignment: 64UL, bind: static placement => placement, key: key, size: size);
    private static (GpuMemoryPool<int, int> Pool, List<string> Log) Pool() {
        var log = new List<string>();
        var next = 0;
        var pool = new GpuMemoryPool<int, int>(
            allocate: request => {
                log.Add(item: $"alloc {++next} {request.Bytes}");
                return next;
            },
            blockBytes: Block,
            free: (block, retiring) => log.Add(item: (retiring ? $"free {block} retiring" : $"free {block}"))
        );

        return (pool, log);
    }
}
