using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The observers a backend's device memory ledger (<see cref="GpuDeviceMemoryWork"/>) reports to, which the host hands it
/// so the backends hold no reference to the host's account: one per segment, which is the segment's physical row of
/// device blocks, and one per owner family and segment, which names the part of that row the family's resources hold.
/// Every charge either kind receives names <see cref="MemoryBacking.DeviceBlocks"/>.
/// </summary>
public interface IGpuMemoryOwners {
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
