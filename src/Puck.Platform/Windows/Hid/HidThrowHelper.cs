using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Puck.Platform.Windows.Hid;

internal static class HidThrowHelper
{
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static ExternalException CreateHumanInterfaceDeviceException(NTSTATUS ntStatus) {
        if (ntStatus == NTSTATUS.HIDP_STATUS_BUFFER_TOO_SMALL) {
            return new ExternalException(message: "The target buffer is too small.");
        }
        else if (ntStatus == NTSTATUS.HIDP_STATUS_INCOMPATIBLE_REPORT_ID) {
            return new ExternalException(message: "The report id is not valid.");
        }
        else if (ntStatus == NTSTATUS.HIDP_STATUS_INVALID_PREPARSED_DATA) {
            return new ExternalException(message: "The preparsed data is not valid.");
        }
        else if (ntStatus == NTSTATUS.HIDP_STATUS_INVALID_REPORT_LENGTH) {
            return new ExternalException(message: "The report length is not valid.");
        }
        else if (ntStatus == NTSTATUS.HIDP_STATUS_INVALID_REPORT_TYPE) {
            return new ExternalException(message: "The specified report type is not valid.");
        }
        else if (ntStatus == NTSTATUS.HIDP_STATUS_USAGE_NOT_FOUND) {
            return new ExternalException(message: "The specified usage is not valid.");
        }
        else {
            return new ExternalException(message: $"Unknown HID status code: {ntStatus}.");
        }
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static Win32Exception CreateWin32Exception(
        string source,
        int? errorCode = default
    ) {
        if (!errorCode.HasValue) {
            errorCode = Marshal.GetLastPInvokeError();
        }

        return new Win32Exception(message: $"Unhandled exception from {source}: [{errorCode}] \"{Marshal.GetPInvokeErrorMessage(error: errorCode.Value)}\".");
    }

    [DoesNotReturn]
    internal static void ThrowHumanInterfaceDeviceException(NTSTATUS ntStatus) {
        throw CreateHumanInterfaceDeviceException(ntStatus: ntStatus);
    }
    [DoesNotReturn]
    internal static void ThrowLastWin32Exception(string source, WIN32_ERROR? errorCode = default) {
        throw CreateWin32Exception(
            errorCode: ((int?)errorCode),
            source: source
        );
    }
}
