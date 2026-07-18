using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Puck.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace Puck.Platform.Windows;

// Display timing and precision-wait capabilities. Physical signal timing comes from CCD; VRR support comes only from
// explicit declarations in the effective monitor EDID. Neither affects deterministic simulation state.
internal sealed partial class Win32NativeWindow : IDisplayTimingInfo, IPrecisionWaiter {
    private const uint EnumCurrentSettings = unchecked((uint)-1);
    private const uint DisplayConfigPathSupportVirtualMode = 0x00000008u;
    private const uint DicsFlagGlobal = 0x00000001u;
    private const uint DiregDevice = 0x00000001u;
    private const uint KeyRead = 0x00020019u;
    private const int MaxDisplayConfigQueryAttempts = 3;
    private const int EdidBlockSize = 128;

    private Win32HighResolutionWaitableTimer? m_precisionTimer;
    private bool m_precisionTimerResolved;
    private ulong m_displayConfigurationVersion;
    private nint m_lastMonitorHandle;

    /// <inheritdoc/>
    public ulong DisplayConfigurationVersion => m_displayConfigurationVersion;

    /// <inheritdoc/>
    public DisplayTimingSnapshot QueryDisplayTiming() {
        if (
            !OperatingSystem.IsWindows() ||
            (m_windowHandle == 0)
        ) {
            return DisplayTimingSnapshot.Unknown;
        }

        var monitor = User32.MonitorFromWindow(windowHandle: m_windowHandle, flags: MonitorDefaultToNearest);

        if (monitor == 0) {
            return DisplayTimingSnapshot.Unknown;
        }

        m_lastMonitorHandle = monitor;

        var monitorInfo = new MonitorInfoEx { Size = ((uint)Marshal.SizeOf<MonitorInfoEx>()) };

        if (!User32.GetMonitorInfoEx(monitorHandle: monitor, monitorInfo: ref monitorInfo)) {
            return DisplayTimingSnapshot.Unknown;
        }

        if (
            OperatingSystem.IsWindowsVersionAtLeast(major: 6, minor: 1) &&
            TryQueryActiveDisplay(deviceName: monitorInfo.DeviceName, snapshot: out var snapshot)
        ) {
            return snapshot;
        }

        // CCD can be transiently unavailable during topology changes. Preserve the useful physical-signal fact from
        // DEVMODE, but do not infer any VRR capability from selectable fixed display modes.
        if (
            TryEnumDisplaySettings(deviceName: monitorInfo.DeviceName, modeNumber: EnumCurrentSettings, mode: out var current) &&
            (current.DisplayFrequency > 1u)
        ) {
            return new DisplayTimingSnapshot(
                Signal: new DisplaySignalTiming(hertz: current.DisplayFrequency),
                VariableRefresh: VariableRefreshCapabilities.Unknown
            );
        }

        return DisplayTimingSnapshot.Unknown;
    }

    /// <inheritdoc/>
    public bool TryWait(TimeSpan duration) {
        if (!m_precisionTimerResolved) {
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

    private void OnDisplayConfigurationChanged() {
        ++m_displayConfigurationVersion;
    }

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
            ++m_displayConfigurationVersion;
        }
    }

    private static bool TryEnumDisplaySettings(string deviceName, uint modeNumber, out DevMode mode) {
        mode = new DevMode { Size = ((ushort)Marshal.SizeOf<DevMode>()) };

        return User32.EnumDisplaySettings(deviceName: deviceName, modeNumber: modeNumber, devMode: ref mode);
    }

    [SupportedOSPlatform(platformName: "windows6.1")]
    private static unsafe bool TryQueryActiveDisplay(string deviceName, out DisplayTimingSnapshot snapshot) {
        snapshot = DisplayTimingSnapshot.Unknown;

        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 6, minor: 1)) {
            return false;
        }

        var queryFlags = (
            QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS |
            QUERY_DISPLAY_CONFIG_FLAGS.QDC_VIRTUAL_MODE_AWARE
        );

        if (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 22000)) {
            queryFlags |= QUERY_DISPLAY_CONFIG_FLAGS.QDC_VIRTUAL_REFRESH_RATE_AWARE;
        }

        for (var attempt = 0; attempt < MaxDisplayConfigQueryAttempts; ++attempt) {
            var result = PInvoke.GetDisplayConfigBufferSizes(
                flags: queryFlags,
                numPathArrayElements: out var pathCount,
                numModeInfoArrayElements: out var modeCount
            );

            if (
                (result != WIN32_ERROR.NO_ERROR) ||
                (pathCount == 0u) ||
                (modeCount == 0u)
            ) {
                return false;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            fixed (DISPLAYCONFIG_PATH_INFO* pathPointer = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modePointer = modes) {
                result = PInvoke.QueryDisplayConfig(
                    flags: queryFlags,
                    numPathArrayElements: ref pathCount,
                    pathArray: pathPointer,
                    numModeInfoArrayElements: ref modeCount,
                    modeInfoArray: modePointer,
                    currentTopologyId: null
                );
            }

            if (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER) {
                continue;
            }

            if (result != WIN32_ERROR.NO_ERROR) {
                return false;
            }

            var targetAccumulator = default(ActiveTargetAccumulator);

            for (var pathIndex = 0u; pathIndex < pathCount; ++pathIndex) {
                ref readonly var path = ref paths[pathIndex];

                if (!PathMatchesSource(path: in path, deviceName: deviceName)) {
                    continue;
                }

                var targetModeIndex = (
                    ((path.flags & DisplayConfigPathSupportVirtualMode) != 0u) ?
                    path.targetInfo.Anonymous.Anonymous.targetModeInfoIdx :
                    path.targetInfo.Anonymous.modeInfoIdx
                );

                if (targetModeIndex >= modeCount) {
                    targetAccumulator.AddUnknownTarget();
                    continue;
                }

                ref readonly var targetMode = ref modes[targetModeIndex];

                if (targetMode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET) {
                    targetAccumulator.AddUnknownTarget();
                    continue;
                }

                var signalRate = targetMode.Anonymous.targetMode.targetVideoSignalInfo.vSyncFreq;

                if (signalRate.Denominator == 0u) {
                    targetAccumulator.AddUnknownTarget();
                    continue;
                }

                var signalHertz = (((double)signalRate.Numerator) / signalRate.Denominator);

                if (!double.IsFinite(d: signalHertz) || (signalHertz <= 0.0)) {
                    targetAccumulator.AddUnknownTarget();
                    continue;
                }

                var variableRefresh = QueryTargetVariableRefresh(path: in path, activeSignalHertz: signalHertz);

                targetAccumulator.AddTarget(signalHertz: signalHertz, variableRefresh: variableRefresh);
            }

            return targetAccumulator.TryCreateSnapshot(snapshot: out snapshot);
        }

        return false;
    }

    [SupportedOSPlatform(platformName: "windows6.1")]
    private static unsafe bool PathMatchesSource(in DISPLAYCONFIG_PATH_INFO path, string deviceName) {
        var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = ((uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME)),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };

        return (
            (PInvoke.DisplayConfigGetDeviceInfo(requestPacket: ref sourceName.header) == 0) &&
            string.Equals(
                a: sourceName.viewGdiDeviceName.ToString(),
                b: deviceName,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
        );
    }

    [SupportedOSPlatform(platformName: "windows6.1")]
    private static unsafe VariableRefreshCapabilities QueryTargetVariableRefresh(in DISPLAYCONFIG_PATH_INFO path, double activeSignalHertz) {
        var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = ((uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME)),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        if (PInvoke.DisplayConfigGetDeviceInfo(requestPacket: ref targetName.header) != 0) {
            return VariableRefreshCapabilities.Unknown;
        }

        var monitorDevicePath = targetName.monitorDevicePath.ToString();

        if (
            string.IsNullOrWhiteSpace(value: monitorDevicePath) ||
            !TryReadEffectiveEdid(monitorDevicePath: monitorDevicePath, edid: out var edid)
        ) {
            return VariableRefreshCapabilities.Unknown;
        }

        return EdidVariableRefreshParser.Parse(edid: edid, activeSignalHertz: activeSignalHertz);
    }

    [SupportedOSPlatform(platformName: "windows6.1")]
    private static unsafe bool TryReadEffectiveEdid(string monitorDevicePath, out byte[] edid) {
        edid = [];

        using var deviceInfoSet = PInvoke.SetupDiGetClassDevs(
            ClassGuid: PInvoke.GUID_DEVINTERFACE_MONITOR,
            Enumerator: null,
            hwndParent: HWND.Null,
            Flags: SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT
        );

        if (deviceInfoSet.IsInvalid) {
            return false;
        }

        var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = ((uint)sizeof(SP_DEVICE_INTERFACE_DATA)) };

        if (!PInvoke.SetupDiOpenDeviceInterface(
            DeviceInfoSet: deviceInfoSet,
            DevicePath: monitorDevicePath,
            OpenFlags: 0u,
            DeviceInterfaceData: &interfaceData
        )) {
            return false;
        }

        var requiredSize = 0u;

        _ = PInvoke.SetupDiGetDeviceInterfaceDetail(
            DeviceInfoSet: deviceInfoSet,
            DeviceInterfaceData: in interfaceData,
            DeviceInterfaceDetailData: null,
            DeviceInterfaceDetailDataSize: 0u,
            RequiredSize: &requiredSize,
            DeviceInfoData: null
        );

        if ((requiredSize == 0u) || (Marshal.GetLastPInvokeError() != ((int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER))) {
            return false;
        }

        var detailBuffer = ArrayPool<byte>.Shared.Rent(minimumLength: ((int)requiredSize));

        try {
            var deviceInfoData = new SP_DEVINFO_DATA { cbSize = ((uint)sizeof(SP_DEVINFO_DATA)) };

            fixed (byte* detailPointer = detailBuffer) {
                var detailData = ((SP_DEVICE_INTERFACE_DETAIL_DATA_W*)detailPointer);

                detailData->cbSize = ((uint)sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W));

                if (!PInvoke.SetupDiGetDeviceInterfaceDetail(
                    DeviceInfoSet: deviceInfoSet,
                    DeviceInterfaceData: in interfaceData,
                    DeviceInterfaceDetailData: detailData,
                    DeviceInterfaceDetailDataSize: requiredSize,
                    RequiredSize: &requiredSize,
                    DeviceInfoData: &deviceInfoData
                )) {
                    return false;
                }
            }

            using var deviceRegistryHandle = PInvoke.SetupDiOpenDevRegKey(
                DeviceInfoSet: deviceInfoSet,
                DeviceInfoData: in deviceInfoData,
                Scope: DicsFlagGlobal,
                HwProfile: 0u,
                KeyType: DiregDevice,
                samDesired: KeyRead
            );

            if (deviceRegistryHandle.IsInvalid) {
                return false;
            }

            using var deviceRegistryKey = RegistryKey.FromHandle(handle: deviceRegistryHandle, view: RegistryView.Default);

            // SetupDiOpenDevRegKey(DIREG_DEV) normally returns the device hardware key (the Device Parameters key on
            // current Windows). Retain the explicit subkey fallback for drivers that expose the device-instance root.
            if (deviceRegistryKey.GetValue(name: "EDID", defaultValue: null, options: RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] hardwareKeyEdid) {
                edid = ApplyEdidOverrides(rawEdid: hardwareKeyEdid, parametersKey: deviceRegistryKey);

                return true;
            }

            using var parametersKey = deviceRegistryKey.OpenSubKey(name: "Device Parameters", writable: false);

            if (parametersKey?.GetValue(name: "EDID", defaultValue: null, options: RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] instanceRootEdid) {
                edid = ApplyEdidOverrides(rawEdid: instanceRootEdid, parametersKey: parametersKey);

                return true;
            }

            return false;
        } finally {
            ArrayPool<byte>.Shared.Return(array: detailBuffer);
        }
    }

    [SupportedOSPlatform(platformName: "windows6.1")]
    private static byte[] ApplyEdidOverrides(byte[] rawEdid, RegistryKey parametersKey) {
        using var overrideKey = parametersKey.OpenSubKey(name: "EDID_OVERRIDE", writable: false);

        if (overrideKey is null) {
            return rawEdid;
        }

        var effective = ((byte[])rawEdid.Clone());

        foreach (var valueName in overrideKey.GetValueNames()) {
            if (
                !int.TryParse(s: valueName, result: out var blockIndex) ||
                (blockIndex is < 0 or > 255) ||
                (overrideKey.GetValue(name: valueName, defaultValue: null, options: RegistryValueOptions.DoNotExpandEnvironmentNames) is not byte[] block) ||
                (block.Length != EdidBlockSize)
            ) {
                continue;
            }

            var requiredLength = checked((blockIndex + 1) * EdidBlockSize);

            if (effective.Length < requiredLength) {
                Array.Resize(array: ref effective, newSize: requiredLength);
            }

            block.CopyTo(array: effective, index: (blockIndex * EdidBlockSize));
        }

        return effective;
    }

    private struct ActiveTargetAccumulator {
        private bool m_hasTarget;
        private bool m_hasUnknownTarget;
        private double m_signalHertz;
        private VariableRefreshCapabilities m_variableRefresh;

        public void AddUnknownTarget() {
            m_hasUnknownTarget = true;
        }

        public void AddTarget(double signalHertz, VariableRefreshCapabilities variableRefresh) {
            if (!m_hasTarget) {
                m_hasTarget = true;
                m_signalHertz = signalHertz;
                m_variableRefresh = variableRefresh;

                return;
            }

            m_signalHertz = Math.Min(val1: m_signalHertz, val2: signalHertz);
            m_variableRefresh = Intersect(left: m_variableRefresh, right: variableRefresh);
        }

        public readonly bool TryCreateSnapshot(out DisplayTimingSnapshot snapshot) {
            // One unreadable member of a cloned source makes both the clone-wide signal ceiling and VRR intersection
            // unknowable. Return a successful unknown query so the caller does not replace it with the source's DEVMODE
            // rate, which could belong to a faster readable target.
            if (m_hasUnknownTarget) {
                snapshot = DisplayTimingSnapshot.Unknown;

                return true;
            }

            if (!m_hasTarget) {
                snapshot = DisplayTimingSnapshot.Unknown;

                return false;
            }

            snapshot = new DisplayTimingSnapshot(
                Signal: new DisplaySignalTiming(hertz: m_signalHertz),
                VariableRefresh: m_variableRefresh
            );

            return true;
        }

        private static VariableRefreshCapabilities Intersect(VariableRefreshCapabilities left, VariableRefreshCapabilities right) {
            if (
                (left.Support == VariableRefreshSupport.Unknown) ||
                (right.Support == VariableRefreshSupport.Unknown)
            ) {
                return VariableRefreshCapabilities.Unknown;
            }

            if (
                (left.Support == VariableRefreshSupport.Unsupported) ||
                (right.Support == VariableRefreshSupport.Unsupported)
            ) {
                return VariableRefreshCapabilities.Unsupported;
            }

            if ((left.Range is not { } leftRange) || (right.Range is not { } rightRange)) {
                return VariableRefreshCapabilities.Unknown;
            }

            var minimum = Math.Max(val1: leftRange.MinimumHertz, val2: rightRange.MinimumHertz);
            var maximum = (leftRange.MaximumHertz, rightRange.MaximumHertz) switch {
                ({ } leftMaximum, { } rightMaximum) => ((double?)Math.Min(val1: leftMaximum, val2: rightMaximum)),
                ({ } leftMaximum, null) => ((double?)leftMaximum),
                (null, { } rightMaximum) => ((double?)rightMaximum),
                _ => ((double?)null),
            };

            if ((maximum is { } maximumHertz) && (maximumHertz <= minimum)) {
                return VariableRefreshCapabilities.Unknown;
            }

            return VariableRefreshCapabilities.CreateSupported(
                range: new VariableRefreshRange(minimumHertz: minimum, maximumHertz: maximum),
                source: (left.Source | right.Source)
            );
        }
    }
}
