using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.Hosting.Jobs;

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
/// <param name="Observed">Whether the owner is observed (<see cref="MemoryAccount.CreateObservedOwner"/>): charged in its
/// pool, outside the account's categories and what admission subtracts.</param>
public readonly record struct MemoryOwnerReading(string Name, MemoryCategory Category, long Owned, long Reserved, long PeakOwned, long ReservationPeak, bool Disposed, PhysicalMemory Pool, bool Observed);
/// <summary>A detached account reading and the charged/reserved owners that reconcile to it, captured under one
/// account lock: the admitted owners' rows reconcile to the account's categories and reservations, and the observed
/// owners' rows to their pools' observed bytes (<see cref="MemoryAccount.ReadPools"/>). Backend, process, managed-heap
/// and allocator readings belong to their own observation windows.</summary>
/// <param name="Account">The existing account's category totals and future promises.</param>
/// <param name="Owners">The owners holding actual charges or reservations at that same instant.</param>
public sealed record MemoryOwnerSnapshot(MemoryReading Account, IReadOnlyList<MemoryOwnerReading> Owners);
/// <summary>One pool's ledger, in bytes. Nothing admits against it: it is read and printed beside the account.</summary>
/// <param name="Pool">The physical memory.</param>
/// <param name="Present">Whether the process has this pool: host memory always; a discrete adapter's own memory once a
/// recorded device reading names a segment in it or an observed owner charges it.</param>
/// <param name="Admitted">The bytes admitted owners charge, which <see cref="MemoryAccount.Owner.TryReserve"/>
/// subtracts; zero outside host memory.</param>
/// <param name="Observed">The bytes observed owners charge, outside what admission subtracts.</param>
/// <param name="Reserved">The future promises admitted owners hold; zero outside host memory.</param>
/// <param name="Budget">The pool's budget: the account's budget for host memory; the sum of its segments' recorded
/// budgets for a discrete adapter's memory; <see langword="null"/> where nothing was recorded.</param>
/// <param name="SpendLimit">What may be spent of it: the budget less <see cref="MemoryAccount.Headroom"/> for host
/// memory, as admission spends today; the sum of its segments' spend limits for a discrete adapter's memory;
/// <see langword="null"/> where nothing was recorded.</param>
public readonly record struct MemoryPoolReading(PhysicalMemory Pool, bool Present, long Admitted, long Observed, long Reserved, long? Budget, long? SpendLimit) {
    /// <summary>Gets the bytes charged to the pool, admitted and observed, each byte once.</summary>
    public long Owned => checked((Admitted + Observed));
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
public sealed record MemoryPoolsReading(MemoryPoolReading Host, MemoryPoolReading Device, GpuMemoryReading DeviceReading, IReadOnlyList<MemorySegmentLimit> SegmentLimits);
