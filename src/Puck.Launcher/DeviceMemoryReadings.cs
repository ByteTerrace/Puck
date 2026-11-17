using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Hosting.Jobs;

namespace Puck.Launcher;

/// <summary>
/// The frame thread's service of the process account. Each GPU host calls <see cref="Refresh"/> once a frame, at the
/// top of the frame: the published device refreshes its memory reading when its API marked it stale
/// (<see cref="IGpuDeviceContext.RefreshMemoryReading"/>) and the account records it
/// (<see cref="MemoryAccount.Record"/>), which moves the device pool's limit; the floors the presentation's selector
/// recorded in the account are passed to the device's own reservation (<see cref="IGpuDeviceContext.ReserveMemory"/>);
/// and a pressure the account read since the last frame fires its trim (<see cref="MemoryAccount.ServicePressure"/>)
/// here, outside every admission. A device not brought up is not brought up, and reads as absent.
/// </summary>
public static class DeviceMemoryReadings {
    /// <summary>Services <see cref="MemoryAccount.Shared"/> for the frame: the reading of the device the host context
    /// publishes, if any, the device's reservation of the floors, and a pending pressure trim.</summary>
    /// <param name="rootHostContext">The root host context whose published device is read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rootHostContext"/> is <see langword="null"/>.</exception>
    public static void Refresh(IHostContext rootHostContext) {
        ArgumentNullException.ThrowIfNull(argument: rootHostContext);

        var account = MemoryAccount.Shared;

        if (rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
            device.RefreshMemoryReading();
            account.Record(reading: device.MemoryReading);
            _ = device.ReserveMemory(bytes: ((ulong)account.FloorOf(pool: GpuMemorySegments.ExpectedPool(role: GpuMemoryRole.DeviceLocal, unifiedMemory: device.MemoryProfile.UnifiedMemory))));
        }
        _ = account.ServicePressure();
    }
}
