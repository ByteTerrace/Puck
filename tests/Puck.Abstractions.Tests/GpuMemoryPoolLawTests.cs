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
    public void AStaleReturnIsRefusedOnceItsOffsetHoldsAnotherResource() {
        var (pool, log) = Pool();
        var first = Place(pool: pool, key: 0, size: 64UL);

        pool.Return(placement: first);

        var second = Place(pool: pool, key: 0, size: 64UL);

        Assert.Equal(expected: first.Offset, actual: second.Offset);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Return(placement: first));
        // The second resource kept its range: the next placement does not overlap it.
        Assert.Equal(expected: 64UL, actual: Place(pool: pool, key: 0, size: 64UL).Offset);
        pool.Return(placement: second);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Return(placement: second));
        Assert.Equal(expected: ["alloc 1 1024"], actual: log);
    }
    [Fact]
    public void ABindThatThrowsGivesItsRangeBackAndItsOwnBlockGoes() {
        var (pool, log) = Pool();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Place<int>(alignment: 1UL, bind: static _ => throw new InvalidOperationException(), key: 0, size: 64UL));
        Assert.Equal(expected: 0UL, actual: Place(pool: pool, key: 0, size: 64UL).Offset);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => pool.Place<int>(alignment: 1UL, bind: static _ => throw new InvalidOperationException(), key: 1, size: (Block * 2UL)));
        Assert.Equal(expected: ["alloc 1 1024", "alloc 2 2048", "free 2"], actual: log);
        Assert.Equal(expected: 1, actual: pool.Blocks);
    }
    [Fact]
    public void AKeyKeepsOneEmptySharedBlockAndAnOwnBlockGoesWhenItEmpties() {
        var (pool, log) = Pool();
        var a = Place(pool: pool, key: 0, size: Block);
        var b = Place(pool: pool, key: 0, size: 512UL);
        var c = Place(pool: pool, key: 0, size: 512UL);
        var d = Place(pool: pool, key: 0, size: 512UL);

        // An own block (past half a block) goes as it empties; of two empty shared blocks, the second goes.
        pool.Return(placement: a);
        pool.Return(placement: b);
        pool.Return(placement: c);
        pool.Return(placement: d);
        Assert.Equal(expected: ["alloc 1 1024", "alloc 2 1024", "alloc 3 1024", "free 1", "free 3"], actual: log);
        Assert.Equal(expected: 1, actual: pool.Blocks);
    }
    [Fact]
    public void RetirementFreesEveryBlockOnceTakesNothingBackAndPlacesNothing() {
        var (pool, log) = Pool();
        var live = Place(pool: pool, key: 0, size: 64UL);

        _ = Place(pool: pool, key: 1, size: 64UL);
        Assert.True(condition: pool.FreeAll());
        Assert.False(condition: pool.FreeAll());
        pool.Return(placement: live);
        Assert.Equal(expected: ["alloc 1 1024", "alloc 2 1024", "free 1 retiring", "free 2 retiring"], actual: log);
        Assert.True(condition: pool.IsRetired);
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => Place(pool: pool, key: 0, size: 64UL));
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
