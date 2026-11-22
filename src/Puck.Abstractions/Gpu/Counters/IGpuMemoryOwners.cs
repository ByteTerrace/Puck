using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The host's account as a backend's device memory ledger (<see cref="GpuDeviceMemoryWork"/>) sees it, which the host
/// hands the ledger so the backends hold no reference to the account: the admission door every device allocation asks
/// before it is made (<see cref="Admit"/>, <see cref="Settle"/>), and the observers the ledger reports to, one per
/// segment, which is the segment's physical row of device blocks, and one per owner family and segment, which names the
/// part of that row the family's resources hold. Every charge either observer receives names
/// <see cref="MemoryBacking.DeviceBlocks"/>.
/// </summary>
public interface IGpuMemoryOwners {
    /// <summary>Requests release of unused native blocks. Invoked outside the account lock; handlers may enter
    /// their pool and report physical releases back to the account. Owners unregister at device teardown.</summary>
    event Action? TrimRequested;

    /// <summary>Answers whether an allocation may be made in a segment, before the device is asked: the segment's pool
    /// has room for it beside everything held and promised there, and the segment has room under its own limit. A grant
    /// promises its bytes until <see cref="Settle"/> is called for it, which happens exactly once. Called outside the
    /// ledger's lock, from any thread.</summary>
    /// <param name="segment">The segment the allocation would be placed in, with its pool.</param>
    /// <param name="bytes">The allocation's size, in bytes, as the device will be asked for it.</param>
    /// <param name="payer">The family of the resource that prompted the allocation, whose reservation is drawn on first,
    /// up to the whole allocation, since a shared block's room holds the payer's next resources, and whose class decides
    /// what a full pool answers; <see langword="null"/> for none.</param>
    /// <param name="payerBytes">The bytes of the resource that prompted the allocation, at most <paramref name="bytes"/>:
    /// what the payer's floor is judged by.</param>
    /// <param name="givesWay">Whether the allocation is one its holder can do without
    /// (<see cref="GpuMemoryAdmissionScope"/>): with no room it is refused whatever the payer's class.</param>
    /// <returns>The answer, handed back to <see cref="Settle"/> unless it refuses.</returns>
    GpuMemoryAdmission Admit(GpuMemorySegment segment, long bytes, string? payer, long payerBytes, bool givesWay);
    /// <summary>Ends a grant <see cref="Admit"/> made: its promise is released. A counted allocation's bytes are charged
    /// through the segment's observer by the ledger before this call; a device refusal also lowers the segment's limit to
    /// what the segment holds.</summary>
    /// <param name="segment">The segment the grant was made for.</param>
    /// <param name="admission">The answer <see cref="Admit"/> returned.</param>
    /// <param name="settlement">How the grant ended.</param>
    void Settle(GpuMemorySegment segment, GpuMemoryAdmission admission, GpuMemorySettlement settlement);
    /// <summary>Returns the observer a segment's held blocks are reported to: the physical row itself. Called once for
    /// each segment row, as a device first registers the segment, under the ledger's lock.</summary>
    /// <param name="segment">The segment, with its pool.</param>
    /// <returns>The observer.</returns>
    IAllocationObserver OfSegment(GpuMemorySegment segment);
    /// <summary>Returns the observer a family's resources in a segment are reported to: a name for part of the segment's
    /// row, never a second charge of it. Called once for each family and segment, as the family's first resource is placed
    /// there, under the ledger's lock.</summary>
    /// <param name="segment">The segment, with its pool.</param>
    /// <param name="family">The family (<see cref="GpuObjectName.MemoryFamily"/>).</param>
    /// <returns>The observer.</returns>
    IAllocationObserver OfFamily(GpuMemorySegment segment, string family);
}
