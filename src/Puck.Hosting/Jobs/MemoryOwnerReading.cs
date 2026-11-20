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
/// <param name="Observed">Whether the owner adds nothing of its own to what a pool holds: a device family, which names
/// part of a segment's row, or a ledger segment's row of device blocks, which is the row.</param>
/// <param name="Row">Whether the owner is a ledger segment's physical row of device blocks rather than a part named in
/// one (<see cref="MemoryAccount.DeviceOwners"/>).</param>
/// <param name="Rows">The bytes it holds in each row; they add to <paramref name="Owned"/>.</param>
/// <param name="Class">What its storage is for; cached storage reads as <see cref="MemoryClass.Cached"/>.</param>
/// <param name="Forced">Whether the owner holds the process's one forced admission
/// (<see cref="MemoryAccount.Owner.TryReserveForced"/>).</param>
public readonly record struct MemoryOwnerReading(string Name, MemoryCategory Category, long Owned, long Reserved, long PeakOwned, long ReservationPeak, bool Disposed, PhysicalMemory Pool, bool Observed, bool Row, MemoryRowBytes Rows, MemoryClass Class, bool Forced);
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
/// <summary>One pool's admission figures, in bytes, read under the account's lock: every admission holds
/// <paramref name="Held"/> plus <paramref name="Promised"/> under <paramref name="Limit"/>, apart from the two counted
/// exceptions, the forced job (<paramref name="Forced"/>) and storage that cannot give way
/// (<paramref name="OverLimitBytes"/>). A limit that falls leaves what is held standing: admission stops growth and
/// gives nothing back.</summary>
/// <param name="Pool">The physical memory.</param>
/// <param name="Limit">What may be spent of the pool, or <see langword="null"/> where nothing bounds it (a discrete
/// adapter's memory no reading names).</param>
/// <param name="Held">What the pool holds: its admitted owners' bytes (host memory) and the device blocks placed in
/// it, a retired object's included until its block is freed.</param>
/// <param name="Peak">The most the pool has held at once.</param>
/// <param name="Promised">What the pool has promised and not yet been charged: its owners' reservations, its device
/// families' and the grants whose native allocation is not counted yet.</param>
/// <param name="Retired">The part of <paramref name="Held"/> that belongs to replaced objects waiting on their last
/// readers, as their holders state it (<see cref="MemoryAccount.DeviceReservation.Retiring"/>, the
/// <see cref="MemoryCategory.Retirement"/> category).</param>
/// <param name="Forced">The peak the process's one forced job was admitted with, zero when none is admitted; host
/// memory only.</param>
/// <param name="ForcedJobs">The forced admissions made over the account's life; host memory only.</param>
/// <param name="OverLimitBytes">The bytes admitted past the limit, over the account's life, for storage that cannot
/// give way.</param>
/// <param name="OverLimitGrants">The allocations admitted past the limit over the account's life.</param>
/// <param name="Refusals">The admissions refused over the account's life: reservations and device allocations.</param>
/// <param name="DeviceRefusals">The admitted allocations the device itself refused.</param>
/// <param name="PressureTrims">The trims pressure fired over the account's life, for every pool together.</param>
/// <param name="Floor">The floors' bytes a selector recorded for the pool (<see cref="MemoryAccount.RecordFloor"/>).</param>
/// <param name="ClassHeld">The held bytes by <see cref="MemoryClass"/>, which add to <paramref name="Held"/>: device
/// blocks no reserving family names are fixed.</param>
/// <param name="ClassPromised">The promised bytes by <see cref="MemoryClass"/>, which add to
/// <paramref name="Promised"/>: grants in flight are fixed.</param>
public sealed record MemoryAdmissionReading(PhysicalMemory Pool, long? Limit, long Held, long Peak, long Promised, long Retired, long Forced, long ForcedJobs, long OverLimitBytes, long OverLimitGrants, long Refusals, long DeviceRefusals, long PressureTrims, long Floor, IReadOnlyList<long> ClassHeld, IReadOnlyList<long> ClassPromised) {
    /// <summary>Gets the owner of the most recent refusal in this pool.</summary>
    public string? LastRefusedOwner { get; init; }
    /// <summary>Gets sampled private bytes beyond the held charges used to reduce host admission room.</summary>
    public long PrivateBeyondHeld { get; init; }
    /// <summary>Gets the room left for a new promise, at least zero, or <see langword="null"/> where nothing bounds the
    /// pool.</summary>
    public long? Room => ((Limit is { } limit) ? Math.Max(val1: 0L, val2: ((limit - Held) - Promised)) : null);
    /// <summary>Gets the bytes held and promised past the limit beyond the forced job's, at least zero: what a limit
    /// that fell leaves standing, and storage that cannot give way.</summary>
    public long OverLimit => ((Limit is { } limit) ? Math.Max(val1: 0L, val2: (((Held + Promised) - Forced) - limit)) : 0L);
}
/// <summary>One ledger segment's admission figures, in bytes.</summary>
/// <param name="Backend">The backend whose ledger holds the segment's row.</param>
/// <param name="Segment">The segment, with its pool.</param>
/// <param name="Limit">The segment's limit: its recorded spend limit, or its size's before a reading names it, held to
/// <paramref name="RefusedLimit"/>; <see langword="null"/> where nothing bounds it.</param>
/// <param name="RefusedLimit">What the segment held when the device last refused an allocation in it, which bounds it
/// until its budget changes; <see langword="null"/> when no refusal stands.</param>
/// <param name="Held">The device blocks the ledger placed in the segment.</param>
/// <param name="Peak">The most it held at once.</param>
/// <param name="Granted">The bytes promised to grants whose native allocation is not counted yet.</param>
public readonly record struct MemorySegmentAdmission(string Backend, GpuMemorySegment Segment, long? Limit, long? RefusedLimit, long Held, long Peak, long Granted);
/// <summary>One pool, in bytes: its admission figures and its physical rows.</summary>
/// <param name="Pool">The physical memory.</param>
/// <param name="Present">Whether the process has this pool: host memory always; a discrete adapter's own memory once a
/// recorded device reading names a segment in it or a ledger placed a block in it.</param>
/// <param name="Admitted">The bytes host owners charge; zero outside host memory. A part of the rows, named by those
/// owners.</param>
/// <param name="Reserved">The future promises host owners hold; zero outside host memory.</param>
/// <param name="Budget">The pool's budget: the account's budget for host memory; the sum of its segments' recorded
/// budgets for a discrete adapter's memory, or the budget set in their place
/// (<see cref="MemoryAccount.SetBudget(PhysicalMemory, long?)"/>); <see langword="null"/> where nothing was
/// recorded.</param>
/// <param name="Admission">What admission holds the pool to: its limit, held and promised bytes and the counted
/// exceptions.</param>
/// <param name="Rows">The pool's physical rows.</param>
public readonly record struct MemoryPoolReading(PhysicalMemory Pool, bool Present, long Admitted, long Reserved, long? Budget, MemoryAdmissionReading Admission, IReadOnlyList<MemoryRowReading> Rows) {
    /// <summary>Gets what may be spent of the pool (<see cref="MemoryAdmissionReading.Limit"/>).</summary>
    public long? SpendLimit => Admission.Limit;
    /// <summary>Gets the bytes the pool holds: its rows added, each byte once, counting a row no sample holds as
    /// nothing.</summary>
    public long Physical => Rows.Sum(selector: static row => (row.Bytes ?? 0L));
    /// <summary>Gets whether every row holds a figure, so <see cref="Physical"/> is the whole pool.</summary>
    public bool Complete => Rows.All(predicate: static row => (row.Bytes is not null));
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
/// <param name="SegmentAdmissions">Each ledger segment's admission figures, by backend then segment index.</param>
/// <param name="Sample">The host sample last recorded (<see cref="MemoryAccount.Measure"/>), or <see langword="null"/>
/// before the first.</param>
/// <param name="SampleAge">The time since <paramref name="Sample"/> was recorded, or <see langword="null"/> before the
/// first.</param>
/// <param name="NegativeResiduals">The readings, over the account's life, in which a row held less than its owners
/// named, which sampling skew causes; each read such a residual as zero.</param>
public sealed record MemoryPoolsReading(MemoryPoolReading Host, MemoryPoolReading Device, GpuMemoryReading DeviceReading, IReadOnlyList<MemorySegmentLimit> SegmentLimits, IReadOnlyList<MemorySegmentAdmission> SegmentAdmissions, HostMemorySample? Sample, TimeSpan? SampleAge, long NegativeResiduals);
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
        long? committed = (((allocator as IAllocatorStatistics)?.ReadStatistics() is { } statistics) ? statistics.Committed : null);

        return new(AllocatorCommitted: committed, ManagedCollection: info.Index, ManagedCommitted: info.TotalCommittedBytes, Walk: (walk ? HostMemory.WalkProcess() : null));
    }
}
