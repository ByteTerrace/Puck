using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.Hosting.Jobs;

/// <summary>The bytes one owner holds in each physical row (<see cref="MemoryBacking"/>).</summary>
/// <param name="NativeAllocator">The bytes in the native allocator's row.</param>
/// <param name="NativeDirect">The bytes in the row of native storage outside the allocator.</param>
/// <param name="Managed">The bytes in the managed heap's row.</param>
/// <param name="DeviceBlocks">The bytes in its pool's device blocks.</param>
public readonly record struct MemoryRowBytes(long NativeAllocator, long NativeDirect, long Managed, long DeviceBlocks);
/// <summary>One charged or reserved owner's part of an atomic account snapshot. Names are diagnostic labels;
/// different owners may have the same name, and neither aliases nor idle empty owners add rows.</summary>
/// <param name="Name">The owner's diagnostic name.</param>
/// <param name="Category">The disjoint category its owned bytes occupy.</param>
/// <param name="Owned">Actual storage still charged to this owner, in bytes.</param>
/// <param name="Reserved">Additional bytes promised to its work but not yet allocated.</param>
/// <param name="PeakOwned">The most actual storage this owner has held at once.</param>
/// <param name="ReservationPeak">The owner's current reserved simultaneous-peak target, which is not an allocation.</param>
/// <param name="Disposed">Whether the owner has closed; any outstanding storage still releases through its observer.</param>
/// <param name="Pool">The physical memory the owner's storage occupies.</param>
/// <param name="Observed">Whether the owner stands outside admission: an observed owner, which names part of a row, or a
/// ledger segment's row of device blocks.</param>
/// <param name="Row">Whether the owner is a ledger segment's physical row of device blocks rather than a part named in
/// one (<see cref="MemoryAccount.DeviceOwners"/>).</param>
/// <param name="Rows">The bytes it holds in each row; they add to <paramref name="Owned"/>.</param>
public readonly record struct MemoryOwnerReading(string Name, MemoryCategory Category, long Owned, long Reserved, long PeakOwned, long ReservationPeak, bool Disposed, PhysicalMemory Pool, bool Observed, bool Row, MemoryRowBytes Rows);
/// <summary>A detached account reading and the charged/reserved owners that reconcile to it, captured under one
/// account lock: the admitted owners' rows reconcile to the account's categories and reservations, the segment owners'
/// to their pools' device blocks, and every other owner's to the parts named in the rows
/// (<see cref="MemoryAccount.ReadPools"/>).</summary>
/// <param name="Account">The existing account's category totals and future promises.</param>
/// <param name="Owners">The owners holding actual charges or reservations at that same instant.</param>
public sealed record MemoryOwnerSnapshot(MemoryReading Account, IReadOnlyList<MemoryOwnerReading> Owners);
/// <summary>One physical row of a pool, in bytes: disjoint from every other row, so the rows of a pool add up to what
/// the pool holds, with the part its owners name and the rest, its residual.</summary>
/// <param name="Name">The row's name as a report prints it: <c>device-blocks</c>, <c>native-allocator</c>,
/// <c>native-direct</c>, <c>managed</c> or <c>runtime-and-driver</c>.</param>
/// <param name="Pool">The physical memory it lies in.</param>
/// <param name="Backing">The row owners charge, or <see langword="null"/> for the runtime's and driver's private memory,
/// which no owner can charge.</param>
/// <param name="Bytes">The row's bytes: a ledger's exact count, the sum of its owners' charges where nothing else
/// measures it, or the last recorded sample (<paramref name="Measured"/>); <see langword="null"/> where no sample holds
/// it.</param>
/// <param name="Measured">Whether <paramref name="Bytes"/> is a recorded sample (<see cref="MemoryAccount.Measure"/>),
/// whose age the pools' reading carries, rather than a count kept under the account's lock.</param>
/// <param name="Named">The bytes its owners name.</param>
public readonly record struct MemoryRowReading(string Name, PhysicalMemory Pool, MemoryBacking? Backing, long? Bytes, bool Measured, long Named) {
    /// <summary>Gets the bytes the row holds beyond what its owners name, at least zero (a residual sampling skew would
    /// make negative reads as zero), or <see langword="null"/> when the row is not measured.</summary>
    public long? Residual => ((Bytes is { } bytes) ? Math.Max(val1: 0L, val2: (bytes - Named)) : null);
}
/// <summary>One pool's ledger, in bytes. Nothing admits against it: it is read and printed beside the account.</summary>
/// <param name="Pool">The physical memory.</param>
/// <param name="Present">Whether the process has this pool: host memory always; a discrete adapter's own memory once a
/// recorded device reading names a segment in it or a ledger placed a block in it.</param>
/// <param name="Admitted">The bytes admitted owners charge, which <see cref="MemoryAccount.Owner.TryReserve"/>
/// subtracts; zero outside host memory. A part of the rows, named by those owners.</param>
/// <param name="Reserved">The future promises admitted owners hold; zero outside host memory.</param>
/// <param name="Budget">The pool's budget: the account's budget for host memory; the sum of its segments' recorded
/// budgets for a discrete adapter's memory; <see langword="null"/> where nothing was recorded.</param>
/// <param name="SpendLimit">What may be spent of it: the budget less <see cref="MemoryAccount.Headroom"/> for host
/// memory, as admission spends today; the sum of its segments' spend limits for a discrete adapter's memory;
/// <see langword="null"/> where nothing was recorded.</param>
/// <param name="Rows">The pool's physical rows.</param>
public readonly record struct MemoryPoolReading(PhysicalMemory Pool, bool Present, long Admitted, long Reserved, long? Budget, long? SpendLimit, IReadOnlyList<MemoryRowReading> Rows) {
    /// <summary>Gets the bytes the pool holds: its rows added, each byte once, counting a row no sample holds as
    /// nothing.</summary>
    public long Physical => Rows.Sum(selector: static row => (row.Bytes ?? 0L));
    /// <summary>Gets whether every row holds a figure, so <see cref="Physical"/> is the whole pool.</summary>
    public bool Complete => Rows.All(predicate: static row => row.Bytes is not null);
}
/// <summary>One segment's recorded budget and spend limit, in bytes.</summary>
/// <param name="Segment">The segment, with its pool.</param>
/// <param name="Budget">The budget its API reported.</param>
/// <param name="SpendLimit">The budget less the larger of a tenth of it and the account's headroom.</param>
public readonly record struct MemorySegmentLimit(GpuMemorySegment Segment, long Budget, long SpendLimit);
/// <summary>The account's pools and the device reading it last recorded, captured under one account lock.</summary>
/// <param name="Host">The host memory pool.</param>
/// <param name="Device">A discrete adapter's memory pool, absent on unified memory and without a device.</param>
/// <param name="DeviceReading">The device reading last recorded (<see cref="MemoryAccount.Record"/>).</param>
/// <param name="SegmentLimits">Each recorded segment's budget and spend limit, in the reading's order.</param>
/// <param name="Sample">The host sample last recorded (<see cref="MemoryAccount.Measure"/>), or <see langword="null"/>
/// before the first.</param>
/// <param name="SampleAge">The time since <paramref name="Sample"/> was recorded, or <see langword="null"/> before the
/// first.</param>
/// <param name="NegativeResiduals">The readings, over the account's life, in which a row held less than its owners
/// named, which sampling skew causes; each read such a residual as zero.</param>
public sealed record MemoryPoolsReading(MemoryPoolReading Host, MemoryPoolReading Device, GpuMemoryReading DeviceReading, IReadOnlyList<MemorySegmentLimit> SegmentLimits, HostMemorySample? Sample, TimeSpan? SampleAge, long NegativeResiduals);
/// <summary>One sample of the host rows the account cannot count itself: the native allocator's committed bytes, the
/// collector's committed bytes as its last collection left them, and optionally a walk of the process's address space.
/// The figures are read one after another, so they describe nearby instants, not one.</summary>
/// <param name="AllocatorCommitted">The native allocator's committed bytes (<see cref="IAllocatorStatistics"/>),
/// including its slack and metadata, or <see langword="null"/> for an allocator that reports none.</param>
/// <param name="ManagedCommitted">The bytes the collector has committed, as of its last collection
/// (<see cref="GCMemoryInfo.TotalCommittedBytes"/>).</param>
/// <param name="ManagedCollection">The index of the collection <paramref name="ManagedCommitted"/> describes
/// (<see cref="GCMemoryInfo.Index"/>).</param>
/// <param name="Walk">The process's address space (<see cref="HostMemory.WalkProcess"/>), or <see langword="null"/> when
/// none was walked.</param>
public sealed record HostMemorySample(long? AllocatorCommitted, long ManagedCommitted, long ManagedCollection, ProcessMemoryWalk? Walk) {
    /// <summary>Takes a sample: the allocator's statistics, the collector's last reading and, when asked, a page walk of
    /// the process. Neither collects nor trims anything.</summary>
    /// <param name="allocator">The engine's native allocator, or <see langword="null"/> for none.</param>
    /// <param name="walk">Whether to walk the process's address space, which costs a system call per region.</param>
    /// <returns>The sample.</returns>
    public static HostMemorySample Take(IAllocator? allocator, bool walk) {
        var info = GC.GetGCMemoryInfo(kind: GCKind.Any);
        long? committed = ((allocator as IAllocatorStatistics)?.ReadStatistics() is { } statistics) ? statistics.Committed : null;

        return new(AllocatorCommitted: committed, ManagedCollection: info.Index, ManagedCommitted: info.TotalCommittedBytes, Walk: (walk ? HostMemory.WalkProcess() : null));
    }
}
