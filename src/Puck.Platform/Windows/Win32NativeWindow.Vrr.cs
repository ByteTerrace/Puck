using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

// Variable-refresh-rate pacing capabilities. The window reports its monitor's refresh range — so the host pacer can
// clamp its render cadence into the display's VRR window (cap below the max, floor at the min) — and offers a high-
// resolution wait so the pacer hits that cadence accurately without busy-waiting the whole interval. Both are pure
// presentation/timing concerns: they never touch the deterministic fixed-step simulation.
internal sealed partial class Win32NativeWindow : IDisplayRefreshInfo, IPrecisionWaiter {
    private const uint EnumCurrentSettings = unchecked((uint)-1);
    private const uint DmInterlaced = 0x00000002u; // DEVMODE.dmDisplayFlags DM_INTERLACED — interlaced modes report a FIELD rate, not a real progressive refresh
    private const int MaxDisplayModes = 4096; // a defensive bound on the EnumDisplaySettings enumeration

    private Win32HighResolutionWaitableTimer? m_precisionTimer;
    private bool m_precisionTimerResolved;
    // Bumped (on the message-pump thread) when the refresh range may have changed: a WM_DISPLAYCHANGE (mode/topology) or
    // the window crossing to a different monitor. The pacer polls RefreshConfigurationVersion and re-queries only on a
    // change. m_lastMonitorHandle is the monitor QueryRefreshRange last reported on — the baseline for move detection.
    private ulong m_refreshConfigurationVersion;
    private nint m_lastMonitorHandle;

    /// <inheritdoc/>
    public ulong RefreshConfigurationVersion => m_refreshConfigurationVersion;

    /// <inheritdoc/>
    public DisplayRefreshRange QueryRefreshRange() {
        if (
            !OperatingSystem.IsWindows() ||
            (m_windowHandle == 0)
        ) {
            return DisplayRefreshRange.Unknown;
        }

        var monitor = User32.MonitorFromWindow(windowHandle: m_windowHandle, flags: MonitorDefaultToNearest);

        if (monitor == 0) {
            return DisplayRefreshRange.Unknown;
        }

        // Record the monitor this range was read from, so a later window-position change that lands on a DIFFERENT
        // monitor is recognized as a configuration change (and not a same-monitor move).
        m_lastMonitorHandle = monitor;

        var monitorInfo = new MonitorInfoEx { Size = ((uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>()) };

        if (!User32.GetMonitorInfoEx(monitorHandle: monitor, monitorInfo: ref monitorInfo)) {
            return DisplayRefreshRange.Unknown;
        }

        var deviceName = monitorInfo.DeviceName;

        // A frequency of 0 or 1 is the "default/hardware" sentinel, not a real rate — treat the whole query as unknown.
        if (
            !TryEnumDisplaySettings(deviceName: deviceName, modeNumber: EnumCurrentSettings, mode: out var current) ||
            (current.DisplayFrequency <= 1u)
        ) {
            return DisplayRefreshRange.Unknown;
        }

        var width = current.PelsWidth;
        var height = current.PelsHeight;
        var minimum = current.DisplayFrequency;
        var maximum = current.DisplayFrequency;

        for (var index = 0u; (index < MaxDisplayModes); ++index) {
            if (!TryEnumDisplaySettings(deviceName: deviceName, modeNumber: index, mode: out var mode)) {
                break;
            }

            // Only PROGRESSIVE modes at the ACTIVE resolution share the display's refresh window; off-resolution modes
            // would skew it, and an interlaced mode reports a field rate (not a real progressive refresh) that must not
            // pollute the min/max.
            if (
                (mode.PelsWidth != width) ||
                (mode.PelsHeight != height) ||
                (mode.DisplayFrequency <= 1u) ||
                ((mode.DisplayFlags & DmInterlaced) != 0u)
            ) {
                continue;
            }

            minimum = Math.Min(val1: minimum, val2: mode.DisplayFrequency);
            maximum = Math.Max(val1: maximum, val2: mode.DisplayFrequency);
        }

        return new DisplayRefreshRange(
            CurrentHertz: current.DisplayFrequency,
            MaximumHertz: maximum,
            MinimumHertz: minimum
        );
    }

    /// <inheritdoc/>
    public bool TryWait(TimeSpan duration) {
        if (!m_precisionTimerResolved) {
            // Resolve once: TryCreate returns null where the high-resolution flag (or the platform) is unsupported, and
            // the timer is disposed in the window's Dispose.
            m_precisionTimer = Win32HighResolutionWaitableTimer.TryCreate();
            m_precisionTimerResolved = true;
        }

        if (m_precisionTimer is null) {
            return false;
        }

        if (duration > TimeSpan.Zero) {
            _ = m_precisionTimer.WaitOne(dueTime: duration, cancellationWaitHandle: null);
        }

        return true;
    }

    // WM_DISPLAYCHANGE: a display mode/topology change (resolution, refresh rate, monitor added/removed). The range the
    // window sees may differ now, so advance the version; the pacer re-queries on its next loop iteration.
    private void OnDisplayConfigurationChanged() {
        ++m_refreshConfigurationVersion;
    }
    // WM_WINDOWPOSCHANGED fires for every move/resize/z-order change; only a move that lands on a DIFFERENT monitor than
    // the one QueryRefreshRange last read can change the refresh range, so bump only then (avoids re-querying on every
    // drag pixel). Cheap MonitorFromWindow call on the pump thread.
    private void OnWindowPositionChanged(nint windowHandle) {
        if (windowHandle == 0) {
            return;
        }

        var monitor = User32.MonitorFromWindow(windowHandle: windowHandle, flags: MonitorDefaultToNearest);

        if (
            (monitor != 0) &&
            (monitor != m_lastMonitorHandle)
        ) {
            m_lastMonitorHandle = monitor;
            ++m_refreshConfigurationVersion;
        }
    }

    private static bool TryEnumDisplaySettings(string deviceName, uint modeNumber, out DevMode mode) {
        mode = new DevMode { Size = ((ushort)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>()) };

        return User32.EnumDisplaySettings(deviceName: deviceName, modeNumber: modeNumber, devMode: ref mode);
    }
}
