using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;

namespace Puck.Platform.Windows.Interop;

/// <summary>A device interface's SetupAPI detail, read by the two-call protocol <c>SetupDiGetDeviceInterfaceDetailW</c>
/// requires: a first call that fails with <c>ERROR_INSUFFICIENT_BUFFER</c> and reports the size, then a second into a
/// buffer of that size whose leading <c>cbSize</c> names the fixed part of <c>SP_DEVICE_INTERFACE_DETAIL_DATA_W</c>.</summary>
internal static class DeviceInterfaceDetail {
    /// <summary>Reads a device interface's path and the device it belongs to.</summary>
    /// <param name="deviceInfoSet">The device information set the interface was enumerated or opened in.</param>
    /// <param name="interfaceData">The interface.</param>
    /// <param name="deviceInfoData">The device the interface belongs to, filled when this method returns
    /// <see langword="true"/>.</param>
    /// <param name="devicePath">The interface's device path, or empty when this method returns <see langword="false"/>.</param>
    /// <param name="error">The last error of the call that failed, or <c>NO_ERROR</c>.</param>
    /// <returns>Whether the detail was read.</returns>
    [SupportedOSPlatform(platformName: "windows5.0")]
    public static unsafe bool TryRead(SafeHandle deviceInfoSet, in SP_DEVICE_INTERFACE_DATA interfaceData, out SP_DEVINFO_DATA deviceInfoData, out string devicePath, out WIN32_ERROR error) {
        deviceInfoData = new SP_DEVINFO_DATA { cbSize = ((uint)sizeof(SP_DEVINFO_DATA)) };
        devicePath = string.Empty;
        _ = PInvoke.SetupDiGetDeviceInterfaceDetail(
            DeviceInfoData: ref deviceInfoData,
            DeviceInfoSet: deviceInfoSet,
            DeviceInterfaceData: in interfaceData,
            DeviceInterfaceDetailData: default,
            RequiredSize: out var requiredSize
        );
        error = ((WIN32_ERROR)Marshal.GetLastPInvokeError());
        if ((error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER) || (requiredSize == 0u)) {
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(minimumLength: ((int)requiredSize));

        try {
            var detail = buffer.AsSpan(length: ((int)requiredSize), start: 0);

            MemoryMarshal.Write(destination: detail, value: ((uint)sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)));
            if (!PInvoke.SetupDiGetDeviceInterfaceDetail(
                DeviceInfoData: ref deviceInfoData,
                DeviceInfoSet: deviceInfoSet,
                DeviceInterfaceData: in interfaceData,
                DeviceInterfaceDetailData: detail,
                RequiredSize: out _
            )) {
                error = ((WIN32_ERROR)Marshal.GetLastPInvokeError());

                return false;
            }
            fixed (byte* start = detail) {
                devicePath = new string(value: MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: ((char*)Unsafe.AsPointer(value: ref ((SP_DEVICE_INTERFACE_DETAIL_DATA_W*)start)->DevicePath.e0))));
            }
            error = WIN32_ERROR.NO_ERROR;

            return true;
        } finally {
            ArrayPool<byte>.Shared.Return(array: buffer);
        }
    }
}
