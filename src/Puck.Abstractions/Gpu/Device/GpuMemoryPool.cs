namespace Puck.Abstractions.Gpu;

/// <summary>
/// The blocks of device memory one device sub-allocates its resources from, with the policy every backend shares: blocks
/// of <see cref="BlockBytes"/> per key (a backend's memory type, role and resource kind, which never share a block), each
/// resource placed first fit across its key's shared blocks at the alignment the device reports for it
/// (<see cref="GpuRangeAllocator"/>), one that no shared block holds in a new shared block or, larger than half a block,
/// in a block of its own, a block every resource left freed when it was a resource's own or its key keeps another empty
/// shared block, and every block freed once by <see cref="FreeAll"/>, which
/// retires the pool. The backend supplies only the native calls: <c>allocate</c> makes a block's memory, <c>free</c>
/// releases it, and the <c>bind</c> each <see cref="Place"/> takes binds or places the resource at its
/// <see cref="GpuMemoryPlacement{TBlock}"/>; the backend finds that placement again when the resource is released.
/// <para>Placements never overlap: a block's ranges are handed out only here, and each placement carries its range's
/// ticket (<see cref="GpuRangeAllocator.TicketOf"/>), which <see cref="Return"/> checks before it frees anything, so a
/// placement is returned once and a stale copy of one is refused even after its offset was given to another resource. A
/// range is reused only once it is returned, so a range a queue may still read stays held as long as the resource's owner
/// holds the resource: the pool sees no fence, and retiring a resource after the submissions that read it is its owner's
/// (a <see cref="GpuRetirementQueue{T}"/> or a slot fence).</para>
/// <para>Thread-safe: <c>allocate</c>, <c>free</c> and a placement's <c>bind</c> run under the pool's lock, so they must
/// not call back into it, and no resource is bound into a block that <see cref="FreeAll"/> released.</para>
/// </summary>
/// <typeparam name="TKey">The key of a pool of blocks: what keeps resources apart, such as memory type and role.</typeparam>
/// <typeparam name="TBlock">A block's native memory, as the backend's calls name it.</typeparam>
public sealed class GpuMemoryPool<TKey, TBlock> where TKey : notnull {
    private readonly Func<TKey, ulong, TBlock> m_allocate;
    private readonly Action<TBlock, bool> m_free;

    private readonly Lock m_gate = new();
    private readonly Dictionary<TKey, List<GpuMemoryBlock<TBlock>>> m_pools = [];
    private bool m_retired;

    /// <summary>Initializes a new instance of the <see cref="GpuMemoryPool{TKey, TBlock}"/> class, holding no
    /// block.</summary>
    /// <param name="blockBytes">The bytes of a shared block.</param>
    /// <param name="allocate">The call that makes a block's memory for a key, of a size in bytes; it throws when the device
    /// refuses, and the pool then holds nothing new.</param>
    /// <param name="free">The call that releases a block's memory; its flag is <see langword="true"/> when
    /// <see cref="FreeAll"/> frees the block as it retires the pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocate"/> or <paramref name="free"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockBytes"/> is under two.</exception>
    public GpuMemoryPool(ulong blockBytes, Func<TKey, ulong, TBlock> allocate, Action<TBlock, bool> free) {
        ArgumentNullException.ThrowIfNull(argument: allocate);
        ArgumentNullException.ThrowIfNull(argument: free);
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 2UL, value: blockBytes);

        BlockBytes = blockBytes;
        m_allocate = allocate;
        m_free = free;
    }

    /// <summary>Gets the bytes of a shared block.</summary>
    public ulong BlockBytes { get; }
    /// <summary>Gets the blocks the pool holds now; zero once retired.</summary>
    public int Blocks {
        get {
            lock (m_gate) {
                var blocks = 0;

                foreach (var pool in m_pools.Values) { blocks += pool.Count; }

                return blocks;
            }
        }
    }
    /// <summary>Gets whether <see cref="FreeAll"/> retired the pool.</summary>
    public bool IsRetired {
        get {
            lock (m_gate) {
                return m_retired;
            }
        }
    }

    /// <summary>Places a resource in a block of a key, the first with room at its alignment or a new one, and binds it
    /// there under the pool's lock. A <paramref name="bind"/> that throws gives the range back before the exception
    /// leaves.</summary>
    /// <typeparam name="TResult">What the binding answers, such as the resource it created.</typeparam>
    /// <param name="key">The key of the blocks it may share.</param>
    /// <param name="size">The bytes it needs; at least one.</param>
    /// <param name="alignment">The alignment its offset needs, as the device reports it: a power of two, or zero or one
    /// for none.</param>
    /// <param name="bind">The call that binds or places the resource at its placement and records the placement for the
    /// resource's release, which returns it (<see cref="Return"/>); it must not call back into the pool.</param>
    /// <returns>What <paramref name="bind"/> answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bind"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is zero, or <paramref name="alignment"/> is
    /// not a power of two.</exception>
    /// <exception cref="ObjectDisposedException">The pool was retired (<see cref="FreeAll"/>).</exception>
    public TResult Place<TResult>(TKey key, ulong size, ulong alignment, Func<GpuMemoryPlacement<TBlock>, TResult> bind) {
        ArgumentNullException.ThrowIfNull(argument: bind);
        ArgumentOutOfRangeException.ThrowIfZero(value: size);
        GpuRangeAllocator.ThrowIfNotAlignment(alignment: alignment);

        var own = (size > (BlockBytes / 2UL));

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_retired, instance: this);
            if (!m_pools.TryGetValue(key: key, value: out var pool)) {
                pool = [];
                m_pools.Add(key: key, value: pool);
            }
            var placement = default(GpuMemoryPlacement<TBlock>);

            foreach (var block in pool) {
                if (!block.Own && block.Ranges.TryAllocate(alignment: alignment, count: size, start: out var offset)) {
                    placement = new GpuMemoryPlacement<TBlock>(block: block, offset: offset, size: size, ticket: block.Ranges.TicketOf(start: offset));
                    break;
                }
            }
            if (placement.Block is null) {
                var bytes = (own ? size : BlockBytes);
                var created = new GpuMemoryBlock<TBlock>(native: m_allocate(arg1: key, arg2: bytes), own: own, owner: this, pool: pool, ranges: new GpuRangeAllocator(size: bytes));

                pool.Add(item: created);

                // An empty block holds any size up to its own at offset zero, which every alignment admits.
                var offset = created.Ranges.Allocate(count: size);

                placement = new GpuMemoryPlacement<TBlock>(block: created, offset: offset, size: size, ticket: created.Ranges.TicketOf(start: offset));
            }
            try {
                return bind(arg: placement);
            } catch {
                // A bind that reached FreeAll on this thread left no block to give the range back to.
                if (!m_retired) { Give(placement: placement); }
                throw;
            }
        }
    }
    /// <summary>Returns a placement's range to its block as its resource is released. A block
    /// left empty is freed when it was a resource's own, or when its key already keeps another empty block. A retired
    /// pool takes nothing back: <see cref="FreeAll"/> freed every block.</summary>
    /// <param name="placement">The placement <see cref="Place"/> returned.</param>
    /// <exception cref="ArgumentException"><paramref name="placement"/> is not one this pool placed.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="placement"/> was returned already, whether or not its
    /// offset has been given to another resource since.</exception>
    public void Return(GpuMemoryPlacement<TBlock> placement) {
        var block = placement.Block;

        if ((block is null) || !ReferenceEquals(objA: block.Owner, objB: this)) {
            throw new ArgumentException(message: "The placement is not one this pool placed.", paramName: nameof(placement));
        }

        lock (m_gate) {
            if (m_retired) {
                return;
            }

            Give(placement: placement);
        }
    }
    /// <summary>Frees every block once, as the device is torn down, whatever resources a device loss left in them, and
    /// retires the pool, which then places nothing and takes nothing back. A second call does nothing.</summary>
    /// <returns>Whether this call retired the pool.</returns>
    public bool FreeAll() {
        lock (m_gate) {
            if (m_retired) {
                return false;
            }

            m_retired = true;
            foreach (var pool in m_pools.Values) {
                foreach (var block in pool) { m_free(arg1: block.Native, arg2: true); }
            }
            m_pools.Clear();

            return true;
        }
    }

    // Frees a placement's range, refused for a stale placement before anything changes, and frees its block when the
    // range left it empty and the block was a resource's own or its key keeps another empty one; the caller holds the lock.
    private void Give(GpuMemoryPlacement<TBlock> placement) {
        var block = placement.Block;

        _ = block.Ranges.Free(start: placement.Offset, ticket: placement.Ticket);
        if ((block.Ranges.LiveRanges != 0) || (!block.Own && !HoldsAnotherEmpty(block: block))) {
            return;
        }

        _ = block.Pool.Remove(item: block);
        m_free(arg1: block.Native, arg2: false);
    }
    // Whether a block's key keeps an empty shared block besides it.
    private static bool HoldsAnotherEmpty(GpuMemoryBlock<TBlock> block) {
        foreach (var other in block.Pool) {
            if (!ReferenceEquals(objA: other, objB: block) && !other.Own && (other.Ranges.LiveRanges == 0)) {
                return true;
            }
        }

        return false;
    }
}
/// <summary>Where a <see cref="GpuMemoryPool{TKey, TBlock}"/> placed one resource: its block's native memory and the
/// range of it the resource holds until the placement is returned.</summary>
/// <typeparam name="TBlock">A block's native memory, as the backend's calls name it.</typeparam>
public readonly struct GpuMemoryPlacement<TBlock> {
    internal GpuMemoryPlacement(GpuMemoryBlock<TBlock> block, ulong offset, ulong size, ulong ticket) {
        Block = block;
        Offset = offset;
        Size = size;
        Ticket = ticket;
    }

    /// <summary>Gets the block's native memory, which the backend binds or places the resource in.</summary>
    public TBlock Native => Block.Native;
    /// <summary>Gets the resource's offset into its block, in bytes, aligned as it asked.</summary>
    public ulong Offset { get; }
    /// <summary>Gets the bytes the resource holds.</summary>
    public ulong Size { get; }

    internal GpuMemoryBlock<TBlock> Block { get; }
    // The ticket of the range in its block, which tells this placement from a later one at the same offset.
    internal ulong Ticket { get; }
}
/// <summary>One block of a <see cref="GpuMemoryPool{TKey, TBlock}"/>: its native memory, its ranges, whether it is one
/// resource's own, and the key's blocks it belongs to.</summary>
/// <typeparam name="TBlock">A block's native memory.</typeparam>
internal sealed class GpuMemoryBlock<TBlock>(TBlock native, bool own, object owner, List<GpuMemoryBlock<TBlock>> pool, GpuRangeAllocator ranges) {
    public TBlock Native { get; } = native;
    public bool Own { get; } = own;
    public object Owner { get; } = owner;
    public List<GpuMemoryBlock<TBlock>> Pool { get; } = pool;
    public GpuRangeAllocator Ranges { get; } = ranges;
}
