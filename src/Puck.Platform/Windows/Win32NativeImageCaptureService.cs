using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Platform.Windows.Interop;
using Windows.Graphics.Capture;

namespace Puck.Platform.Windows;

/// <summary>Creates compositor-owned Windows Graphics Capture feeds for visible top-level windows and whole monitors.</summary>
public sealed class Win32NativeImageCaptureService : INativeImageCaptureService {
    private const int MinimumWindowsBuild = 19041;
    private const uint MonitorInfoPrimary = 0x00000001;

    /// <inheritdoc/>
    [SupportedOSPlatformGuard("windows10.0.19041")]
    public bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: MinimumWindowsBuild) && IsGraphicsCaptureSupported();

    /// <inheritdoc/>
    public bool TryCreateWindowCapture(string windowTitleFragment, int width, int height, double refreshRateHz, [NotNullWhen(true)] out INativeImageCaptureFeed? feed, long? adapterLuid = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: windowTitleFragment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: refreshRateHz);

        if (!double.IsFinite(refreshRateHz)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(refreshRateHz), actualValue: refreshRateHz, message: "The refresh rate must be finite.");
        }

        feed = null;
        if (!IsSupported || !TryFindWindow(titleFragment: windowTitleFragment, windowHandle: out var windowHandle)) {
            return false;
        }

        if (!Win32GraphicsCaptureFeed.TryCreate(
            windowHandle: windowHandle,
            width: width,
            height: height,
            refreshRateHz: refreshRateHz,
            feed: out var windowsFeed,
            adapterLuid: adapterLuid
        )) {
            return false;
        }

        feed = windowsFeed;
        return true;
    }

    /// <inheritdoc/>
    public bool TryCreateMonitorCapture(int monitorIndex, int width, int height, double refreshRateHz, [NotNullWhen(true)] out INativeImageCaptureFeed? feed, long? adapterLuid = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: refreshRateHz);

        if (!double.IsFinite(refreshRateHz)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(refreshRateHz), actualValue: refreshRateHz, message: "The refresh rate must be finite.");
        }

        feed = null;
        if (!IsSupported || !TryResolveMonitor(monitorIndex: monitorIndex, monitorHandle: out var monitorHandle)) {
            return false;
        }

        if (!Win32GraphicsCaptureFeed.TryCreateForMonitor(
            monitorHandle: monitorHandle,
            width: width,
            height: height,
            refreshRateHz: refreshRateHz,
            feed: out var monitorFeed,
            adapterLuid: adapterLuid
        )) {
            return false;
        }

        feed = monitorFeed;
        return true;
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private static bool IsGraphicsCaptureSupported() {
        try {
            return GraphicsCaptureSession.IsSupported();
        } catch {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryFindWindow(string titleFragment, out nint windowHandle) {
        var match = (nint)0;
        var buffer = Array.Empty<char>();

        _ = User32.EnumWindows(
            (candidate, _) => {
                if (!User32.IsWindowVisible(windowHandle: candidate)) {
                    return true;
                }

                var titleLength = User32.GetWindowTextLength(windowHandle: candidate);
                if (titleLength <= 0) {
                    return true;
                }

                var required = (titleLength + 1);
                if (buffer.Length < required) {
                    buffer = new char[required];
                }

                var copied = User32.GetWindowText(windowHandle: candidate, text: buffer, maxLength: buffer.Length);
                if ((copied <= 0) || !buffer.AsSpan(start: 0, length: copied).Contains(value: titleFragment, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }

                match = candidate;
                return false;
            },
            parameter: 0
        );

        windowHandle = match;
        return (match != 0);
    }

    // Resolves the logical monitor index (0 = primary, rest in enumeration order) to an HMONITOR. An index past the
    // last monitor is an absent target, not an argument error, so it returns false rather than throwing.
    [SupportedOSPlatform("windows")]
    private static bool TryResolveMonitor(int monitorIndex, out nint monitorHandle) {
        monitorHandle = 0;
        if (monitorIndex < 0) {
            return false;
        }

        var handles = new List<nint>();
        var primaryOrdinal = -1;
        _ = User32.EnumDisplayMonitors(
            deviceContext: 0,
            clipRectangle: 0,
            callback: (candidate, _, _, _) => {
                var info = new MonitorInfo {
                    Size = (uint)Marshal.SizeOf<MonitorInfo>(),
                };
                if (User32.GetMonitorInfo(monitorHandle: candidate, monitorInfo: ref info)) {
                    if ((info.Flags & MonitorInfoPrimary) != 0) {
                        primaryOrdinal = handles.Count;
                    }

                    handles.Add(item: candidate);
                }

                return true;
            },
            parameter: 0
        );

        // Logical order places the primary first, then the remaining monitors in enumeration order.
        var ordinal = 0;
        if (primaryOrdinal >= 0) {
            if (monitorIndex == 0) {
                monitorHandle = handles[primaryOrdinal];
                return true;
            }

            ordinal = 1;
        }

        for (var i = 0; i < handles.Count; i++) {
            if (i == primaryOrdinal) {
                continue;
            }

            if (ordinal == monitorIndex) {
                monitorHandle = handles[i];
                return true;
            }

            ordinal++;
        }

        return false;
    }
}
