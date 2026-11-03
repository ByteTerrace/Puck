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
public readonly record struct MemoryOwnerReading(string Name, MemoryCategory Category, long Owned, long Reserved, long PeakOwned, long ReservationPeak, bool Disposed);

/// <summary>A detached account reading and the charged/reserved owners that reconcile to it, captured under one
/// account lock. Backend, process, managed-heap and allocator readings belong to their own observation windows.</summary>
/// <param name="Account">The existing account's category totals and future promises.</param>
/// <param name="Owners">The owners holding actual charges or reservations at that same instant.</param>
public sealed record MemoryOwnerSnapshot(MemoryReading Account, IReadOnlyList<MemoryOwnerReading> Owners);
