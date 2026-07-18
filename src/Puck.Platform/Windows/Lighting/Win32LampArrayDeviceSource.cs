using System.Runtime.Versioning;
using Puck.Abstractions.Lighting;
using Puck.Platform.Windows.Hid;

namespace Puck.Platform.Windows.Lighting;

/// <summary>
/// The Windows HID LampArray transport for <see cref="ILampArrayDeviceSource"/>: probes the present HID
/// interfaces (reusing the same SetupAPI enumeration the gamepad transport uses) and opens the ones whose
/// top-level collection is a LampArray (usage page <c>0x59</c>, usage <c>0x01</c>). On a non-Windows OS it
/// reports no devices, so a consumer simply finds nothing rather than failing.
/// </summary>
public sealed class Win32LampArrayDeviceSource : ILampArrayDeviceSource {
    private readonly Dictionary<string, ILampArrayDevice> m_devicesByPath = new(comparer: StringComparer.OrdinalIgnoreCase);
    private ILampArrayDevice[] m_devices = [];
    private bool m_isDisposed;

    /// <summary>Initializes a new instance and performs an initial enumeration.</summary>
    public Win32LampArrayDeviceSource() {
        Rescan();
    }

    /// <inheritdoc />
    public IReadOnlyList<ILampArrayDevice> Devices { get => m_devices; }

    /// <inheritdoc />
    public void Rescan() {
        ObjectDisposedException.ThrowIf(condition: m_isDisposed, instance: this);

        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 5, minor: 1, build: 2600)) {
            return;
        }

        RescanCore();
    }

    [SupportedOSPlatform(platformName: "windows5.1.2600")]
    private void RescanCore() {
        var present = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var interfaceInfo in Win32HumanInterfaceDevice.EnumerateInterfaces()) {
            var path = interfaceInfo.Path;

            if (!present.Add(item: path) || m_devicesByPath.ContainsKey(key: path)) {
                continue;
            }

            var device = Win32LampArrayDevice.TryOpen(devicePath: path);

            if (device is not null) {
                m_devicesByPath[path] = device;
            }
        }

        // Prune devices whose interface has gone away.
        foreach (var path in m_devicesByPath.Keys.ToArray()) {
            if (!present.Contains(item: path)) {
                m_devicesByPath[path].Dispose();
                _ = m_devicesByPath.Remove(key: path);
            }
        }

        m_devices = [.. m_devicesByPath.Values];
    }

    /// <inheritdoc />
    public void Dispose() {
        if (m_isDisposed) {
            return;
        }

        m_isDisposed = true;

        foreach (var device in m_devicesByPath.Values) {
            device.Dispose();
        }

        m_devicesByPath.Clear();
        m_devices = [];
    }
}
