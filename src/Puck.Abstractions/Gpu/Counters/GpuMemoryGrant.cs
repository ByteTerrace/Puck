using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>What the host's account answered an admission (<see cref="IGpuMemoryOwners.Admit"/>).</summary>
public enum GpuMemoryVerdict {
    /// <summary>The allocation has no room and its class gives way: nothing may be allocated.</summary>
    Refused,
    /// <summary>The allocation fits its pool's limit and its segment's.</summary>
    Granted,
    /// <summary>The allocation has no room and its class cannot give way (<see cref="MemoryClasses.GivesWay"/>): it is
    /// admitted past the limit and counted.</summary>
    OverLimit,
}
/// <summary>How a grant ended (<see cref="IGpuMemoryOwners.Settle"/>).</summary>
public enum GpuMemorySettlement {
    /// <summary>The native allocation succeeded and the ledger charged its segment's row.</summary>
    Counted,
    /// <summary>The native allocation was never attempted, or failed for a reason other than memory.</summary>
    Returned,
    /// <summary>The device refused the native allocation for want of memory.</summary>
    DeviceRefused,
}
/// <summary>The account's answer to one admission, which its settlement hands back unchanged.</summary>
/// <param name="Verdict">Whether the allocation may be made.</param>
/// <param name="Bytes">The allocation's size, in bytes: what the segment is promised until the grant settles.</param>
/// <param name="Net">The bytes the pool is promised until the grant settles: <paramref name="Bytes"/> less the part a
/// reservation already promised.</param>
public readonly record struct GpuMemoryAdmission(GpuMemoryVerdict Verdict, long Bytes, long Net);
/// <summary>
/// The account's leave to make one device allocation, which only <see cref="GpuDeviceMemoryWork.TryAdmit"/> makes and
/// <see cref="GpuDeviceMemoryWork.CountAllocated"/> takes: the device, the size the device is asked for, the role, the
/// segment and the family. A grant is settled exactly once. While it stands its bytes are promised in the account, so two
/// allocations cannot spend the same room.
/// </summary>
public sealed class GpuMemoryGrant {
    private int m_settled;

    internal GpuMemoryGrant(GpuDeviceMemoryWork ledger, nint device, long bytes, GpuMemoryRole role, GpuMemorySegment segment, string? family, GpuMemoryAdmission admission) {
        Ledger = ledger;
        Device = device;
        Bytes = bytes;
        Role = role;
        Segment = segment;
        Family = family;
        Admission = admission;
    }

    /// <summary>Gets the allocation's size, in bytes, as the device is asked for it.</summary>
    public long Bytes { get; }
    /// <summary>Gets the native device the allocation is made on.</summary>
    public nint Device { get; }
    /// <summary>Gets the family that names the whole allocation, or <see langword="null"/> for a shared block.</summary>
    public string? Family { get; }
    /// <summary>Gets whether the allocation was admitted past a limit because its class cannot give way.</summary>
    public bool OverLimit => (Admission.Verdict == GpuMemoryVerdict.OverLimit);
    /// <summary>Gets what the allocation is for.</summary>
    public GpuMemoryRole Role { get; }
    /// <summary>Gets the segment the allocation is placed in.</summary>
    public GpuMemorySegment Segment { get; }

    internal GpuMemoryAdmission Admission { get; }
    internal GpuDeviceMemoryWork Ledger { get; }

    /// <summary>Hands the grant back because the device refused the native allocation for want of memory: the promise is
    /// released, the segment's limit is lowered to what it holds, and the device's reading is marked stale
    /// (<see cref="GpuDeviceMemoryWork.DeviceRefusals"/>).</summary>
    /// <returns>The exception the creation door throws for the refused allocation.</returns>
    /// <exception cref="InvalidOperationException">The grant was settled already.</exception>
    public GpuMemoryRefusedException Refuse() {
        Take();
        Ledger.Settle(grant: this, settlement: GpuMemorySettlement.DeviceRefused);

        return new GpuMemoryRefusedException(bytes: Bytes, byDevice: true, family: Family, segment: Segment.Name);
    }
    /// <summary>Hands the grant back because the native allocation was not made for a reason other than memory.</summary>
    /// <exception cref="InvalidOperationException">The grant was settled already.</exception>
    public void Return() {
        Take();
        Ledger.Settle(grant: this, settlement: GpuMemorySettlement.Returned);
    }

    // Marks the grant settled; a grant settles once.
    internal void Take() {
        if (Interlocked.Exchange(location1: ref m_settled, value: 1) != 0) {
            throw new InvalidOperationException(message: "A device memory grant is settled once: counted, returned or refused.");
        }
    }
}
/// <summary>
/// A device allocation that was not made for want of memory: the account refused it before the device was asked, or the
/// device refused it. Nothing was allocated and nothing is counted; the caller keeps what it had. An owner whose storage
/// gives way (a region that grows, a pipeline that replaces its graph) catches it and keeps the object it would have
/// replaced.
/// </summary>
public sealed class GpuMemoryRefusedException : Exception {
    /// <summary>Initializes a new instance of the <see cref="GpuMemoryRefusedException"/> class.</summary>
    /// <param name="bytes">The bytes the device was to be asked for.</param>
    /// <param name="segment">The name of the segment the allocation would have been placed in.</param>
    /// <param name="family">The family the allocation is attributed to, or <see langword="null"/> for none.</param>
    /// <param name="byDevice">Whether the device refused; <see langword="false"/> when the account refused before the
    /// device was asked.</param>
    public GpuMemoryRefusedException(long bytes, string segment, string? family, bool byDevice)
        : base(message: $"A device allocation of {bytes} bytes in segment {segment} for {(family ?? "a shared block")} was refused by the {(byDevice ? "device" : "memory account")}; nothing was allocated.") {
        Bytes = bytes;
        ByDevice = byDevice;
        Family = family;
        Segment = segment;
    }

    /// <summary>Gets the bytes the device was to be asked for.</summary>
    public long Bytes { get; }
    /// <summary>Gets whether the device refused, rather than the account.</summary>
    public bool ByDevice { get; }
    /// <summary>Gets the family the allocation is attributed to, or <see langword="null"/>.</summary>
    public string? Family { get; }
    /// <summary>Gets the name of the segment the allocation would have been placed in.</summary>
    public string Segment { get; }
}
