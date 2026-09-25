namespace Puck.Abstractions.Gpu;

/// <summary>
/// Chooses where a region the host writes every frame lives, from what the device reports about its memory and nothing
/// else: no platform, product or driver name reaches the choice. A region the host can write directly into device-local
/// memory is written there — in place on coherent unified memory, through a per-frame ring otherwise — and any other
/// region is staged and copied.
/// </summary>
public static class GpuResidency {
    /// <summary>The share of the host-visible device-local heap one region may take and still be written directly: a
    /// region larger than that heap divided by this is staged, so a frame ring of several slots across several regions
    /// stays inside a small aperture.</summary>
    public const ulong HostVisibleShare = 16UL;

    /// <summary>Returns the policy's name as a readout prints it: <c>staged</c>, <c>ring</c> or <c>in-place</c>.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is not a defined policy.</exception>
    public static string Name(GpuResidencyPolicy policy) => policy switch {
        GpuResidencyPolicy.Staged => "staged",
        GpuResidencyPolicy.Ring => "ring",
        GpuResidencyPolicy.InPlace => "in-place",
        _ => throw new ArgumentOutOfRangeException(
            actualValue: policy,
            message: "The residency policy is not defined.",
            paramName: nameof(policy)
        ),
    };
    /// <summary>Selects the policy for a region of <paramref name="byteCount"/> bytes on a device with
    /// <paramref name="profile"/>. A region within <see cref="HostVisibleShare"/> of the host-visible device-local heap
    /// is written through a ring, or in place on coherent unified memory when no submission reading the region is in
    /// flight while the host writes it; a larger region, or any region on a device reporting no host-visible
    /// device-local memory (the default profile included), is staged. An owner whose frame ring keeps a reader in flight
    /// while it writes, as every per-frame owner's does, never gets <see cref="GpuResidencyPolicy.InPlace"/>: in place, a
    /// write would land under that reader.</summary>
    /// <param name="profile">The device's memory profile.</param>
    /// <param name="byteCount">The region's size in bytes; at least one.</param>
    /// <param name="readersInFlight">Whether a submission reading the region can be in flight while the host writes
    /// it.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is zero.</exception>
    public static GpuResidencyPolicy Select(GpuMemoryProfile profile, ulong byteCount, bool readersInFlight) {
        ArgumentOutOfRangeException.ThrowIfZero(value: byteCount);

        if (byteCount > (profile.HostVisibleDeviceLocalBytes / HostVisibleShare)) {
            return GpuResidencyPolicy.Staged;
        }

        return ((profile.CoherentUnifiedMemory && !readersInFlight)
            ? GpuResidencyPolicy.InPlace
            : GpuResidencyPolicy.Ring
        );
    }
}
