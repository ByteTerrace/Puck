using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Hosting.Jobs;

namespace Puck.Launcher;

/// <summary>
/// The frame thread's refresh of the published device's memory reading. Each GPU host calls <see cref="Refresh"/> once a
/// frame, at the top of the frame: the device refreshes its reading only when its API marked it stale
/// (<see cref="IGpuDeviceContext.RefreshMemoryReading"/>), and the process account records it
/// (<see cref="MemoryAccount.Record"/>), which fires no trim and changes no admission. A device not brought up is not
/// brought up, and reads as absent.
/// </summary>
public static class DeviceMemoryReadings {
    /// <summary>Refreshes the device the host context publishes, if any, and records its reading in
    /// <see cref="MemoryAccount.Shared"/>.</summary>
    /// <param name="rootHostContext">The root host context whose published device is read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rootHostContext"/> is <see langword="null"/>.</exception>
    public static void Refresh(IHostContext rootHostContext) {
        ArgumentNullException.ThrowIfNull(argument: rootHostContext);

        if (!rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
            return;
        }

        device.RefreshMemoryReading();
        MemoryAccount.Shared.Record(reading: device.MemoryReading);
    }
}
