using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// One background GPU service's reservation of what its jobs hold on the device (<see cref="SdfGpuWork"/>): the bytes
/// of its slots' buffers, in the pool device-local buffers lie in and in the host's, each reserved in the account under
/// <see cref="SdfResidencySelector.WorkOwner"/> as workspace before the buffer is allocated
/// (<see cref="SdfResidencySelector.ReserveWork"/>).
/// <para>The reservation's peak is what the buffers hold, raised by a growth before the device is asked for it and
/// lowered when a buffer goes, so the pool's promised bytes name the growth for exactly as long as it is not yet
/// held. A raise the pool has no room for is refused, except a forced one, which becomes the reservation's floor: what
/// the service cannot run without, admitted whatever the pool holds and counted past its limit.</para>
/// Single-threaded, on the recording thread.
/// </summary>
public sealed class SdfGpuWorkHold : IDisposable {
    private readonly MemoryAccount.DeviceReservation m_device;
    private readonly MemoryAccount.DeviceReservation m_host;

    private long m_deviceFloor;
    private long m_hostFloor;

    internal SdfGpuWorkHold(MemoryAccount.DeviceReservation device, MemoryAccount.DeviceReservation host) {
        m_device = device;
        m_host = host;
    }

    /// <summary>Gets the bytes reserved for the service's device-local buffers.</summary>
    public long DeviceBytes { get; private set; }
    /// <summary>Gets the bytes reserved for the service's host-visible buffers, its readbacks among them.</summary>
    public long HostBytes { get; private set; }

    /// <summary>Reserves a buffer's bytes before it is allocated.</summary>
    /// <param name="hostVisible">Whether the buffer is host-visible; otherwise device-local.</param>
    /// <param name="bytes">The buffer's bytes.</param>
    /// <param name="forced">Whether the service has no other job in flight, so the reservation is its floor and is
    /// never refused.</param>
    /// <returns><see langword="false"/> when the pool has no room and the raise was not forced; nothing changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public bool TryRaise(bool hostVisible, long bytes, bool forced) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: bytes);

        var held = (hostVisible ? HostBytes : DeviceBytes);
        var peak = checked(held + bytes);
        var floor = (forced ? peak : (hostVisible ? m_hostFloor : m_deviceFloor));

        if (!(hostVisible ? m_host : m_device).TryReserve(floor: floor, peak: peak)) { return false; }
        Set(bytes: peak, floor: floor, hostVisible: hostVisible);
        return true;
    }
    /// <summary>Gives back a buffer's bytes: one that was released, or one whose allocation failed after its bytes were
    /// reserved.</summary>
    /// <param name="hostVisible">Whether the buffer is host-visible; otherwise device-local.</param>
    /// <param name="bytes">The buffer's bytes.</param>
    public void Lower(bool hostVisible, long bytes) {
        var held = Math.Max(val1: 0L, val2: ((hostVisible ? HostBytes : DeviceBytes) - bytes));
        var floor = Math.Min(val1: (hostVisible ? m_hostFloor : m_deviceFloor), val2: held);

        // Lowering asks the pool for nothing, so it is never refused.
        _ = (hostVisible ? m_host : m_device).TryReserve(floor: floor, peak: held);
        Set(bytes: held, floor: floor, hostVisible: hostVisible);
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_device.Dispose();
        m_host.Dispose();
    }

    private void Set(bool hostVisible, long bytes, long floor) {
        if (hostVisible) {
            HostBytes = bytes;
            m_hostFloor = floor;
        } else {
            DeviceBytes = bytes;
            m_deviceFloor = floor;
        }
    }
}
